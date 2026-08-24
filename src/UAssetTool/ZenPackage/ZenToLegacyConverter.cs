using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Converts Zen packages to legacy .uasset/.uexp format
/// Ported from retoc-rivals/src/asset_conversion.rs
/// </summary>
public class ZenToLegacyConverter
{
    // Constants matching Rust code
    private const string CORE_OBJECT_PACKAGE_NAME = "/Script/CoreUObject";
    private const string PACKAGE_CLASS_NAME = "Package";
    private const string CLASS_CLASS_NAME = "Class";
    private const string OBJECT_CLASS_NAME = "Object";
    private const string PRESTREAM_PACKAGE_CLASS_NAME = "PrestreamPackage";
    
    private readonly FZenPackageHeader _zenPackage;
    private readonly byte[] _rawPackageData;
    private readonly FZenPackageContext? _context;
    private readonly ScriptObjectsDatabase? _scriptObjects;
    private readonly ulong _packageId;
    private readonly int _sourceContainerIndex = -1; // Container that provided this package's ExportBundleData
    
    // Legacy package being built
    private readonly LegacyPackageBuilder _builder;
    
    // Lookup tables
    private readonly Dictionary<ResolvedZenImport, int> _resolvedImportLookup = new();
    private readonly Dictionary<ulong, int> _zenImportLookup = new(); // FPackageObjectIndex.Value -> import index
    private readonly Dictionary<int, int> _originalImportOrder = new(); // zen import index -> legacy import index
    
    private bool _needsToRebuildExportsData = false; // Whether export data needs reordering from bundle order
    private bool _debugMode = false; // Enable debug output for dependency tracing
    
    public void SetDebugMode(bool enabled) => _debugMode = enabled;

    /// <summary>
    /// Create converter with full context for proper import resolution
    /// </summary>
    public ZenToLegacyConverter(FZenPackageContext context, ulong packageId)
    {
        _context = context;
        _packageId = packageId;
        _scriptObjects = context.ScriptObjects;
        _sourceContainerIndex = context.GetPackageContainerIndex(packageId);
        
        var cached = context.GetCachedPackage(packageId);
        if (cached == null)
            throw new ArgumentException($"Package {packageId:X16} not found in context");
        
        _zenPackage = cached.Header;
        _rawPackageData = cached.RawData;
        _builder = new LegacyPackageBuilder();
    }

    /// <summary>
    /// Create converter without context (limited import resolution)
    /// </summary>
    public ZenToLegacyConverter(FZenPackageHeader zenPackage, byte[] rawPackageData, ScriptObjectsDatabase? scriptObjects = null)
    {
        _zenPackage = zenPackage;
        _rawPackageData = rawPackageData;
        _scriptObjects = scriptObjects;
        _context = null;
        _packageId = 0;
        _builder = new LegacyPackageBuilder();
    }

    /// <summary>
    /// Convert Zen package to legacy format
    /// </summary>
    public LegacyAssetBundle Convert(bool skipBulkData = false)
    {
        BeginBuildSummary();
        CopyPackageSections();
        BuildImportMap();
        BuildExportMap();
        ResolvePrestreamPackageImports();
        ResolveExportDependencies();
        FinalizeAsset();
        
        var bundle = SerializeAsset();
        
        // Extract bulk data from the SAME container that provided the ExportBundleData.
        // This prevents extracting the game's .ubulk when a mod texture has no mipmaps.
        if (!skipBulkData && _context != null && _packageId != 0)
        {
            byte[]? bulkData = _context.ReadBulkData(_packageId, _sourceContainerIndex);
            byte[]? optionalBulkData = _context.ReadOptionalBulkData(_packageId, _sourceContainerIndex);
            byte[]? memoryMappedBulkData = _context.ReadMemoryMappedBulkData(_packageId, _sourceContainerIndex);
            
            if (bulkData != null && bulkData.Length > 0)
                bundle.BulkData = bulkData;
            
            if (optionalBulkData != null && optionalBulkData.Length > 0)
                bundle.OptionalBulkData = optionalBulkData;
            
            if (memoryMappedBulkData != null && memoryMappedBulkData.Length > 0)
                bundle.MemoryMappedBulkData = memoryMappedBulkData;
        }
        
        return bundle;
    }

    /// <summary>
    /// Get the package IDs of all imported game packages (excludes /Script/ engine packages).
    /// Used for dependency extraction.
    /// </summary>
    public IEnumerable<ulong> GetImportedPackageIds()
    {
        for (int i = 0; i < _zenPackage.ImportedPackages.Count; i++)
        {
            if (i < _zenPackage.ImportedPackageNames.Count)
            {
                string packageName = _zenPackage.ImportedPackageNames[i];
                if (packageName.StartsWith("/Script/"))
                    continue;
            }
            yield return _zenPackage.ImportedPackages[i];
        }
    }

    /// <summary>
    /// Get imported package names with their IDs for debugging.
    /// </summary>
    public IEnumerable<(ulong Id, string Name)> GetImportedPackageInfo()
    {
        for (int i = 0; i < _zenPackage.ImportedPackages.Count; i++)
        {
            ulong pkgId = _zenPackage.ImportedPackages[i];
            string? name = null;
            
            if (_context != null)
                name = _context.GetPackagePath(pkgId);
            
            if (string.IsNullOrEmpty(name) && i < _zenPackage.ImportedPackageNames.Count)
                name = _zenPackage.ImportedPackageNames[i];
            
            if (string.IsNullOrEmpty(name))
                name = $"(unknown_{pkgId:X16})";
            
            yield return (pkgId, name);
        }
    }

    /// <summary>
    /// Heuristic: a "bare leaf name" is a package name that lacks a directory path
    /// (no slashes, or doesn't start with a UE root prefix like /Game/, /Engine/,
    /// /Script/, /Plugins/). Such names are anomalous for a Summary.Name and indicate
    /// the package was authored with only the leaf object name stored.
    /// </summary>
    private static bool LooksLikeBareLeafName(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (!name.Contains('/')) return true; // no slashes => bare leaf
        if (name.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("/Plugins/", StringComparison.OrdinalIgnoreCase)) return false;
        // Any other absolute UE-style root (starts with '/') is acceptable
        if (name.StartsWith("/")) return false;
        // Has slashes but no leading slash - probably still a path, accept it
        return false;
    }

    private void BeginBuildSummary()
    {
        // Use the zen package's own SourcePackageName for the FolderName.
        // Normally we prefer this over _context.GetPackagePath() because the UTOC
        // directory index may have different casing than the zen name map
        // (e.g., "Mvp" vs "MVP"). Retoc also uses the zen package name.
        //
        // HOWEVER: some assets (especially mods) ship with a Summary.Name that only
        // contains the leaf object name (e.g. "SK_1033_1033502") instead of the full
        // package path. In that case we must fall back to the UTOC directory index,
        // otherwise the written .uasset's FolderName loses the full /Game/.../ path
        // and tools (FModel, the game itself when re-imported) lose track of where
        // the asset belongs.
        string packageName = _zenPackage.SourcePackageName();
        if (LooksLikeBareLeafName(packageName) && _context != null && _packageId != 0)
        {
            string? fullPath = _context.GetPackagePath(_packageId);
            if (!string.IsNullOrEmpty(fullPath) && !LooksLikeBareLeafName(fullPath))
            {
                if (_debugMode)
                    Console.Error.WriteLine($"[BeginBuildSummary] Summary.Name='{packageName}' is bare leaf; using UTOC path '{fullPath}'");
                packageName = fullPath;
            }
        }

        // Normalize package name - resolve /../ patterns
        // e.g., /Game/Marvel/../../../Marvel/Content/Marvel/Characters/... -> /Game/Marvel/Characters/...
        if (packageName.Contains("/../"))
        {
            // Resolve the path segments
            var segments = packageName.Split('/').ToList();
            var resolved = new List<string>();
            foreach (var seg in segments)
            {
                if (seg == ".." && resolved.Count > 0 && resolved[resolved.Count - 1] != "..")
                    resolved.RemoveAt(resolved.Count - 1);
                else if (seg != ".")
                    resolved.Add(seg);
            }
            string resolvedPath = string.Join("/", resolved);
            
            // Find /Content/ and rebuild as /Game/...
            int contentIdx = resolvedPath.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (contentIdx >= 0)
                packageName = "/Game" + resolvedPath.Substring(contentIdx + "/Content".Length);
            else
                packageName = resolvedPath;
        }
        
        _builder.PackageName = packageName;
        _builder.PackageFlags = _zenPackage.Summary.PackageFlags;
        
        _builder.VersioningInfo = new LegacyVersioningInfo
        {
            FileVersionUE4 = _zenPackage.VersioningInfo.FileVersionUE4,
            FileVersionUE5 = _zenPackage.VersioningInfo.FileVersionUE5,
            LicenseeVersion = _zenPackage.VersioningInfo.LicenseeVersion,
            IsUnversioned = _zenPackage.IsUnversioned,
            CustomVersions = new List<FCustomVersion>(_zenPackage.VersioningInfo.CustomVersions)
        };
    }

    private void CopyPackageSections()
    {
        // Copy name map
        _builder.NameMap = new List<string>(_zenPackage.NameMap);
        // NamesReferencedFromExportDataCount = original zen name map count
        // This counts only names referenced from export (serialized) data, not names added for imports
        _builder.NamesReferencedFromExportDataCount = _zenPackage.NameMap.Count;
        
        // Copy bulk data resources from zen package
        foreach (var zenBulkData in _zenPackage.BulkData)
        {
            _builder.DataResources.Add(new LegacyDataResource
            {
                Flags = 0,
                SerialOffset = zenBulkData.SerialOffset,
                DuplicateSerialOffset = zenBulkData.DuplicateSerialOffset,
                SerialSize = zenBulkData.SerialSize,
                RawSize = zenBulkData.SerialSize,
                OuterIndex = FPackageIndex.CreateNull(),
                LegacyBulkDataFlags = zenBulkData.Flags
            });
        }
        
        // Note: Do NOT fabricate empty DataResource entries when there are no zen BulkData entries.
        // retoc leaves DataResources empty and sets DataResourceOffset to -1 in that case.
    }

    private void BuildImportMap()
    {
        if (_debugMode)
        {
            Console.Error.WriteLine($"[DEBUG] BuildImportMap: {_zenPackage.ImportMap.Count} imports to resolve");
            Console.Error.WriteLine($"[DEBUG] ImportedPackages: {_zenPackage.ImportedPackages.Count}");
            Console.Error.WriteLine($"[DEBUG] ImportedPublicExportHashes: {_zenPackage.ImportedPublicExportHashes.Count}");
            for (int i = 0; i < _zenPackage.ImportedPackages.Count; i++)
            {
                string pkgName = i < _zenPackage.ImportedPackageNames.Count ? _zenPackage.ImportedPackageNames[i] : "?";
                Console.Error.WriteLine($"[DEBUG]   ImportedPackage[{i}]: {_zenPackage.ImportedPackages[i]:X16} = {pkgName}");
            }
        }
        
        for (int importIndex = 0; importIndex < _zenPackage.ImportMap.Count; importIndex++)
        {
            var importObjectIndex = _zenPackage.ImportMap[importIndex];
            
            // Skip holes in the zen import map
            if (importObjectIndex.IsNull())
            {
                if (_debugMode)
                    Console.Error.WriteLine($"[DEBUG] Import[{importIndex}]: NULL");
                continue;
            }
            
            if (_debugMode)
            {
                string importType = importObjectIndex.IsScriptImport() ? "Script" : 
                                   importObjectIndex.IsImport() ? "Package" : "Other";
                Console.Error.WriteLine($"[DEBUG] Import[{importIndex}]: Type={importType}, Value={importObjectIndex.Value:X16}");
                if (importObjectIndex.IsImport())
                {
                    var pkgImport = importObjectIndex.GetPackageImport();
                    Console.Error.WriteLine($"[DEBUG]   PackageIndex={pkgImport.ImportedPackageIndex}, HashIndex={pkgImport.ImportedPublicExportHashIndex}");
                }
            }
            
            try
            {
                int resultPackageIndex = ResolveLocalPackageObject(importObjectIndex);
                
                // ResolveLocalPackageObject returns negative for imports, positive for exports
                // We need the actual import index (0-based positive) for _originalImportOrder
                if (resultPackageIndex >= 0) // Not an import (is export or null)
                {
                    throw new Exception($"Import map object did not resolve into an import");
                }
                
                // Convert from package index (-1, -2, ...) to import array index (0, 1, ...)
                int resultImportMapIndex = -(resultPackageIndex + 1);
                _originalImportOrder[importIndex] = resultImportMapIndex;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to resolve import {importIndex}: {ex.Message}");
                
                // Create placeholder import
                int nullPackageImport = CreateAndAddUnknownPackageImport();
                int importMapIndex = _builder.Imports.Count;
                var nullObjectImport = CreateUnknownObjectImportEntry(nullPackageImport);
                _builder.Imports.Add(nullObjectImport);
                _zenImportLookup[importObjectIndex.Value] = importMapIndex;
                _originalImportOrder[importIndex] = importMapIndex;
            }
        }
    }

    private void BuildExportMap()
    {
        bool debugMode = Environment.GetEnvironmentVariable("DEBUG") == "1";
        
        for (int exportIndex = 0; exportIndex < _zenPackage.ExportMap.Count; exportIndex++)
        {
            var zenExport = _zenPackage.ExportMap[exportIndex];
            
            // Resolve indices with fallback
            FPackageIndex classIndex = ResolveWithFallbackAsPackageIndex(zenExport.ClassIndex, "class_index");
            FPackageIndex superIndex = ResolveWithFallbackAsPackageIndex(zenExport.SuperIndex, "super_index");
            FPackageIndex templateIndex = ResolveWithFallbackAsPackageIndex(zenExport.TemplateIndex, "template_index");
            FPackageIndex outerIndex = ResolveWithFallbackAsPackageIndex(zenExport.OuterIndex, "outer_index");
            
            // For export object names from the package NameMap, use the zen index directly
            // since we already copied the NameMap. This avoids ParseNameWithNumber incorrectly
            // splitting names like "MI_1033502_ElectricRifle_Weapon_01" into base + number.
            int objectName;
            int objectNameNumber;
            if (zenExport.ObjectName.Type == EMappedNameType.Package && zenExport.ObjectName.Index < _zenPackage.NameMap.Count)
            {
                objectName = (int)zenExport.ObjectName.Index;
                objectNameNumber = (int)zenExport.ObjectName.Number;
            }
            else
            {
                // Fallback for non-package names: resolve and store
                string exportNameString = _zenPackage.GetName(zenExport.ObjectName);
                if (debugMode && exportIndex < 20)
                {
                    Console.Error.WriteLine($"[DEBUG] Export[{exportIndex}]: ObjectName.Index={zenExport.ObjectName.Index}, Name=\"{exportNameString}\" (resolved)");
                }
                (objectName, objectNameNumber) = StoreOrFindNameWithNumber(exportNameString);
            }
            uint objectFlags = zenExport.ObjectFlags;
            
            long serialSize = (long)zenExport.CookedSerialSize;
            // Serial offset will be recalculated after all exports are added
            // In Zen format, exports are in bundle order which may differ from sequential order
            long serialOffset = -1;
            
            bool isNotForClient = zenExport.FilterFlags == EExportFilterFlags.NotForClient;
            bool isNotForServer = zenExport.FilterFlags == EExportFilterFlags.NotForServer;
            
            uint assetObjectFlags = (uint)(EObjectFlags.Public | EObjectFlags.Standalone | EObjectFlags.Transactional);
            bool isAsset = zenExport.OuterIndex.IsNull() && (objectFlags & assetObjectFlags) == assetObjectFlags;
            bool generatePublicHash = _zenPackage.ContainerHeaderVersion >= EIoContainerHeaderVersion.LocalizedPackages &&
                                     (objectFlags & (uint)EObjectFlags.Public) == 0 && zenExport.IsPublicExport();
            
            var newExport = new LegacyObjectExport
            {
                ClassIndex = classIndex,
                SuperIndex = superIndex,
                TemplateIndex = templateIndex,
                OuterIndex = outerIndex,
                ObjectName = objectName,
                ObjectNameNumber = objectNameNumber,
                ObjectFlags = objectFlags,
                SerialSize = serialSize,
                SerialOffset = serialOffset,
                IsNotForClient = isNotForClient,
                IsNotForServer = isNotForServer,
                IsAsset = isAsset,
                GeneratePublicHash = generatePublicHash,
                FirstExportDependencyIndex = -1,
                ScriptSerializationStartOffset = 0,
                ScriptSerializationEndOffset = serialSize
            };
            
            _builder.Exports.Add(newExport);
        }
        
        // Determine if we need to rebuild export data (reorder from bundle order to sequential)
        // For UE5.3+ NoExportInfo packages, bundle headers are NOT in the zen package itself -
        // they come from the container header store entry. Since we don't read those, our
        // generated bundle headers are synthetic. In this case, export data is already in
        // sequential order and we should use CookedSerialOffset directly (matching retoc).
        _needsToRebuildExportsData = _zenPackage.ExportBundleHeaders.Any(h => h.SerialOffset != ulong.MaxValue);
        
        if (_needsToRebuildExportsData)
        {
            // Recalculate serial offsets sequentially for rebuild
            long currentExportSerialOffset = 0;
            for (int i = 0; i < _builder.Exports.Count; i++)
            {
                _builder.Exports[i].SerialOffset = currentExportSerialOffset;
                currentExportSerialOffset += _builder.Exports[i].SerialSize;
            }
        }
        else
        {
            // Use CookedSerialOffset directly - data is already in correct order
            for (int i = 0; i < _builder.Exports.Count; i++)
            {
                _builder.Exports[i].SerialOffset = (long)_zenPackage.ExportMap[i].CookedSerialOffset;
            }
        }
    }

    /// <summary>
    /// Resolve PrestreamPackage imports for packages in ImportedPackages that are not
    /// referenced by any import map entry. These are special imports that hint the loader
    /// to pre-stream another package. Matches retoc's resolve_prestream_package_imports.
    /// </summary>
    private void ResolvePrestreamPackageImports()
    {
        if (_context == null)
            return;
        
        // Collect all package IDs that are already represented in the import map
        // (imports with null outer whose object names are non-/Script/ package paths)
        var existingPackageIds = new HashSet<ulong>();
        foreach (var import in _builder.Imports)
        {
            if (!import.OuterIndex.IsNull())
                continue;
            
            // Get the object name
            if (import.ObjectName < 0 || import.ObjectName >= _builder.NameMap.Count)
                continue;
            
            string objectName = _builder.NameMap[import.ObjectName];
            if (objectName.StartsWith("/Script/"))
                continue;
            
            // Compute package ID from the FULL FName (base name + number suffix)
            // The FName number is stored separately in ObjectNameNumber
            string fullFName = objectName;
            if (import.ObjectNameNumber > 0)
                fullFName = $"{objectName}_{import.ObjectNameNumber - 1}";
            ulong packageId = FPackageId.FromName(fullFName);
            existingPackageIds.Add(packageId);
        }
        
        // Find imported packages not already in the import map
        foreach (ulong importedPackageId in _zenPackage.ImportedPackages)
        {
            if (existingPackageIds.Contains(importedPackageId))
                continue;
            
            // Try to resolve the package to get its name
            var targetPackage = _context.GetPackage(importedPackageId);
            if (targetPackage == null)
            {
                Console.Error.WriteLine($"[ZenToLegacy] Failed to resolve pre-stream package {importedPackageId:X16}");
                continue;
            }
            
            string resolvedPackageName = targetPackage.SourcePackageName();
            
            int classPackageIdx = StoreOrFindName(CORE_OBJECT_PACKAGE_NAME);
            int classNameIdx = StoreOrFindName(PRESTREAM_PACKAGE_CLASS_NAME);
            int objectNameIdx = StoreOrFindName(resolvedPackageName);
            
            _builder.Imports.Add(new LegacyObjectImport
            {
                ClassPackage = classPackageIdx,
                ClassPackageNumber = 0,
                ClassName = classNameIdx,
                ClassNameNumber = 0,
                OuterIndex = FPackageIndex.CreateNull(),
                ObjectName = objectNameIdx,
                ObjectNameNumber = 0,
                IsOptional = false
            });
        }
    }

    private void ResolveExportDependencies()
    {
        // Create standalone dependency objects first (matching Rust)
        var exportDependencies = new List<StandaloneExportDependencies>();
        for (int i = 0; i < _builder.Exports.Count; i++)
        {
            exportDependencies.Add(new StandaloneExportDependencies());
        }
        
        // If we have dependency bundle entries, this is new style (UE5.3+) dependencies
        if (_zenPackage.DependencyBundleEntries.Count > 0)
        {
            ResolveDependencyBundles(exportDependencies);
        }
        
        // Apply standalone dependencies to the package global list (matching Rust)
        ApplyDependenciesToPackage(exportDependencies);
    }

    private void ResolveDependencyBundles(List<StandaloneExportDependencies> exportDependencies)
    {
        for (int exportIndex = 0; exportIndex < _zenPackage.DependencyBundleHeaders.Count && exportIndex < exportDependencies.Count; exportIndex++)
        {
            var bundleHeader = _zenPackage.DependencyBundleHeaders[exportIndex];
            var dependencies = exportDependencies[exportIndex];
            
            int firstDepIndex = bundleHeader.FirstEntryIndex;
            
            // Create before create
            int lastCbC = firstDepIndex + (int)bundleHeader.CreateBeforeCreateDependencies;
            for (int i = firstDepIndex; i < lastCbC && i < _zenPackage.DependencyBundleEntries.Count; i++)
            {
                var entry = _zenPackage.DependencyBundleEntries[i];
                dependencies.CreateBeforeCreate.Add(RemapZenPackageIndex(entry.LocalImportOrExportIndex));
            }
            
            // Serialize before create
            int lastSbC = lastCbC + (int)bundleHeader.SerializeBeforeCreateDependencies;
            for (int i = lastCbC; i < lastSbC && i < _zenPackage.DependencyBundleEntries.Count; i++)
            {
                var entry = _zenPackage.DependencyBundleEntries[i];
                dependencies.SerializeBeforeCreate.Add(RemapZenPackageIndex(entry.LocalImportOrExportIndex));
            }
            
            // Create before serialize
            int lastCbS = lastSbC + (int)bundleHeader.CreateBeforeSerializeDependencies;
            for (int i = lastSbC; i < lastCbS && i < _zenPackage.DependencyBundleEntries.Count; i++)
            {
                var entry = _zenPackage.DependencyBundleEntries[i];
                dependencies.CreateBeforeSerialize.Add(RemapZenPackageIndex(entry.LocalImportOrExportIndex));
            }
            
            // Serialize before serialize
            int lastSbS = lastCbS + (int)bundleHeader.SerializeBeforeSerializeDependencies;
            for (int i = lastCbS; i < lastSbS && i < _zenPackage.DependencyBundleEntries.Count; i++)
            {
                var entry = _zenPackage.DependencyBundleEntries[i];
                dependencies.SerializeBeforeSerialize.Add(RemapZenPackageIndex(entry.LocalImportOrExportIndex));
            }
        }
    }

    private FPackageIndex RemapZenPackageIndex(FPackageIndex index)
    {
        if (index.IsImport())
        {
            int zenImportIndex = index.ToImportIndex();
            if (_originalImportOrder.TryGetValue(zenImportIndex, out int remapped))
            {
                return FPackageIndex.CreateImport(remapped);
            }
            // Zen import index not in our mapping - log for debugging
            if (_debugMode)
            {
                Console.Error.WriteLine($"[DEBUG] Unmapped zen import index: {zenImportIndex}, ImportMap size: {_zenPackage.ImportMap.Count}, OriginalImportOrder keys: {string.Join(",", _originalImportOrder.Keys.Take(20))}");
            }
            return index;
        }
        return index;
    }

    private void ApplyDependenciesToPackage(List<StandaloneExportDependencies> exportDependencies)
    {
        // Matching Rust's apply_standalone_dependencies_to_package exactly
        for (int exportIndex = 0; exportIndex < _builder.Exports.Count; exportIndex++)
        {
            var export = _builder.Exports[exportIndex];
            var deps = exportDependencies[exportIndex];
            
            // Ensure that we have outer and super as create before create dependencies (matching Rust lines 899-905)
            if (!export.OuterIndex.IsNull() && !deps.CreateBeforeCreate.Contains(export.OuterIndex))
            {
                deps.CreateBeforeCreate.Add(export.OuterIndex);
            }
            if (!export.SuperIndex.IsNull() && !deps.CreateBeforeCreate.Contains(export.SuperIndex))
            {
                // Note: Rust checks create_before_create but pushes to serialize_before_serialize (line 903-904)
                deps.SerializeBeforeSerialize.Add(export.SuperIndex);
            }
            
            // Ensure that we have class and archetype as serialize before create dependencies (matching Rust lines 906-912)
            if (!export.ClassIndex.IsNull() && !deps.SerializeBeforeCreate.Contains(export.ClassIndex))
            {
                deps.SerializeBeforeCreate.Add(export.ClassIndex);
            }
            if (!export.TemplateIndex.IsNull() && !deps.SerializeBeforeCreate.Contains(export.TemplateIndex))
            {
                deps.SerializeBeforeCreate.Add(export.TemplateIndex);
            }
            
            // If we have no dependencies altogether, do not write first dependency import on the export (matching Rust line 914-916)
            if (deps.CreateBeforeCreate.Count == 0 && deps.SerializeBeforeCreate.Count == 0 &&
                deps.CreateBeforeSerialize.Count == 0 && deps.SerializeBeforeSerialize.Count == 0)
                continue;
            
            // Set the index of the preload dependencies start, and the numbers of each dependency (matching Rust lines 919-924)
            export.FirstExportDependencyIndex = _builder.PreloadDependencies.Count;
            export.SerializeBeforeSerializeDependencies = deps.SerializeBeforeSerialize.Count;
            export.CreateBeforeSerializeDependencies = deps.CreateBeforeSerialize.Count;
            export.SerializeBeforeCreateDependencies = deps.SerializeBeforeCreate.Count;
            export.CreateBeforeCreateDependencies = deps.CreateBeforeCreate.Count;
            
            // Append preload dependencies for this export now to the legacy package (matching Rust lines 926-930)
            _builder.PreloadDependencies.AddRange(deps.SerializeBeforeSerialize);
            _builder.PreloadDependencies.AddRange(deps.CreateBeforeSerialize);
            _builder.PreloadDependencies.AddRange(deps.SerializeBeforeCreate);
            _builder.PreloadDependencies.AddRange(deps.CreateBeforeCreate);
        }
    }

    private void FinalizeAsset()
    {
        // Remap import indices to final positions
        int importMapSize = Math.Max(_builder.Imports.Count, _zenPackage.ImportMap.Count);
        var importRemapMap = new Dictionary<int, int>();
        var newImportMap = new List<LegacyObjectImport>();
        
        var predefinedPositions = new HashSet<int>(_originalImportOrder.Values);
        int currentLegacyImportIndex = 0;
        
        for (int finalImportIndex = 0; finalImportIndex < importMapSize; finalImportIndex++)
        {
            if (_originalImportOrder.TryGetValue(finalImportIndex, out int existingPosition))
            {
                importRemapMap[existingPosition] = finalImportIndex;
                if (existingPosition < _builder.Imports.Count)
                    newImportMap.Add(_builder.Imports[existingPosition]);
                else
                    newImportMap.Add(CreateUnknownPackageImportEntry());
                continue;
            }
            
            while (predefinedPositions.Contains(currentLegacyImportIndex))
                currentLegacyImportIndex++;
            
            if (currentLegacyImportIndex >= _builder.Imports.Count)
            {
                newImportMap.Add(CreateUnknownPackageImportEntry());
                continue;
            }
            
            int existingPos = currentLegacyImportIndex++;
            importRemapMap[existingPos] = finalImportIndex;
            newImportMap.Add(_builder.Imports[existingPos]);
        }
        
        _builder.Imports = newImportMap;
        
        // Remap all references
        FPackageIndex Remap(FPackageIndex idx)
        {
            if (idx.IsImport() && importRemapMap.TryGetValue(idx.ToImportIndex(), out int newIdx))
                return FPackageIndex.CreateImport(newIdx);
            return idx;
        }
        
        foreach (var export in _builder.Exports)
        {
            export.ClassIndex = Remap(export.ClassIndex);
            export.SuperIndex = Remap(export.SuperIndex);
            export.TemplateIndex = Remap(export.TemplateIndex);
            export.OuterIndex = Remap(export.OuterIndex);
        }
        
        foreach (var import in _builder.Imports)
        {
            import.OuterIndex = Remap(import.OuterIndex);
        }
        
        foreach (var res in _builder.DataResources)
        {
            res.OuterIndex = Remap(res.OuterIndex);
        }
        
        for (int i = 0; i < _builder.PreloadDependencies.Count; i++)
        {
            _builder.PreloadDependencies[i] = Remap(_builder.PreloadDependencies[i]);
        }
    }

    private LegacyAssetBundle SerializeAsset()
    {
        // First pass: write header to calculate size
        byte[] assetData;
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            WriteLegacyPackageHeader(writer);
            assetData = ms.ToArray();
        }
        
        int headerSize = assetData.Length;
        
        // Fix SerialOffset in exports - should be header size + position in .uexp
        long currentOffset = 0;
        foreach (var export in _builder.Exports)
        {
            export.SerialOffset = headerSize + currentOffset;
            currentOffset += export.SerialSize;
        }
        
        // Second pass: write header again with correct serial offsets
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            WriteLegacyPackageHeader(writer);
            assetData = ms.ToArray();
        }
        
        // Build .uexp data
        byte[] exportsData = RebuildExportData();
        
        return new LegacyAssetBundle
        {
            AssetData = assetData,
            ExportsData = exportsData
        };
    }

    private void WriteLegacyPackageHeader(BinaryWriter writer)
    {
        const uint PACKAGE_FILE_TAG = 0x9E2A83C1;
        const int LEGACY_FILE_VERSION_UE5 = -8;
        
        // Write magic
        writer.Write(PACKAGE_FILE_TAG);
        
        // LegacyFileVersion: -8 for UE5 packages
        writer.Write(LEGACY_FILE_VERSION_UE5);
        
        // LegacyUE3Version - always 0
        writer.Write(0);
        
        // FileVersionUE4 - 0 for unversioned
        int fileVersionUE4 = _builder.VersioningInfo.IsUnversioned ? 0 : _builder.VersioningInfo.FileVersionUE4;
        writer.Write(fileVersionUE4);
        
        // FileVersionUE5 - 0 for unversioned (only for LEGACY_FILE_VERSION_UE5)
        int fileVersionUE5 = _builder.VersioningInfo.IsUnversioned ? 0 : _builder.VersioningInfo.FileVersionUE5;
        writer.Write(fileVersionUE5);
        
        // FileVersionLicenseeUE - 0 for unversioned
        int licenseeVersion = _builder.VersioningInfo.IsUnversioned ? 0 : _builder.VersioningInfo.LicenseeVersion;
        writer.Write(licenseeVersion);
        
        // Custom versions - empty array for unversioned
        if (_builder.VersioningInfo.IsUnversioned || _builder.VersioningInfo.CustomVersions.Count == 0)
        {
            writer.Write(0); // CustomVersionCount = 0
        }
        else
        {
            writer.Write(_builder.VersioningInfo.CustomVersions.Count);
            foreach (var cv in _builder.VersioningInfo.CustomVersions)
            {
                // FCustomVersion: Key (GUID), Version (int)
                writer.Write(cv.Key.ToByteArray());
                writer.Write(cv.Version);
            }
        }
        
        // TotalHeaderSize - placeholder
        long headerSizePos = writer.BaseStream.Position;
        writer.Write(0); // Placeholder
        
        // FolderName (package name)
        WriteFString(writer, _builder.PackageName);
        
        // Package flags
        writer.Write(_builder.PackageFlags);
        
        // NameCount and NameOffset
        writer.Write(_builder.NameMap.Count);
        long nameOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // NameOffset placeholder
        
        // SoftObjectPathsCount/Offset - always written for UE5
        writer.Write(0); // SoftObjectPathsCount
        long softObjOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // SoftObjectPathsOffset placeholder
        
        // GatherableTextDataCount/Offset - always written
        writer.Write(0);
        writer.Write(0);
        
        // ExportCount and ExportOffset
        writer.Write(_builder.Exports.Count);
        long exportOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // ExportOffset placeholder
        
        // ImportCount and ImportOffset
        writer.Write(_builder.Imports.Count);
        long importOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // ImportOffset placeholder
        
        // DependsOffset
        long dependsOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // Placeholder
        
        // SoftPackageReferencesCount/Offset - count=0, offset=0 for cooked packages
        writer.Write(0);
        writer.Write(0);
        
        // SearchableNamesOffset
        writer.Write(0);
        
        // ThumbnailTableOffset
        writer.Write(0);
        
        // PackageGuid (16 bytes) - zeroed for cooked packages
        writer.Write(new byte[16]);
        
        // Generations array
        writer.Write(1); // GenerationCount
        writer.Write(_builder.Exports.Count); // ExportCount for generation
        writer.Write(_builder.NameMap.Count); // NameCount for generation
        
        // EngineVersion (FEngineVersion) - all zeros for cooked packages
        writer.Write((ushort)0); // Major
        writer.Write((ushort)0); // Minor  
        writer.Write((ushort)0); // Patch
        writer.Write(0); // Changelist
        WriteFString(writer, ""); // Branch
        
        // CompatibleWithEngineVersion - all zeros for cooked packages
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0);
        WriteFString(writer, "");
        
        // CompressionFlags
        writer.Write(0);
        
        // CompressedChunks (empty array)
        writer.Write(0);
        
        // PackageSource
        writer.Write(0);
        
        // AdditionalPackagesToCook (empty array)
        writer.Write(0);
        
        // AssetRegistryDataOffset
        long assetRegistryDataOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // Placeholder
        
        // BulkDataStartOffset - should be the end of last export (header size + total export data size)
        // This is set later after we know the actual sizes
        long bulkDataStartOffsetPos = writer.BaseStream.Position;
        writer.Write((long)0); // Placeholder
        
        // WorldTileInfoDataOffset
        writer.Write(0);
        
        // ChunkIDs (empty array)
        writer.Write(0);
        
        // PreloadDependencyCount/Offset
        writer.Write(_builder.PreloadDependencies.Count);
        long preloadDepOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // Placeholder
        
        // NamesReferencedFromExportDataCount
        writer.Write(_builder.NamesReferencedFromExportDataCount);
        
        // PayloadTocOffset
        writer.Write((long)-1);
        
        // DataResourceOffset only (Rust doesn't write count, just offset)
        long dataResourceOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // Placeholder
        
        // ---- Write actual data sections ----
        
        // Write name map
        long nameOffset = writer.BaseStream.Position;
        foreach (var name in _builder.NameMap)
        {
            WriteFString(writer, name);
            // Name hash (not really used in modern UE)
            writer.Write(0);
        }
        
        // Write import map
        long importOffset = writer.BaseStream.Position;
        foreach (var import in _builder.Imports)
        {
            WriteImport(writer, import);
        }
        
        // Write export map
        long exportOffset = writer.BaseStream.Position;
        foreach (var export in _builder.Exports)
        {
            WriteExport(writer, export);
        }
        
        // Depends offset is right after exports
        // For cooked packages, write empty depends array (count=0) for each export
        long dependsOffset = writer.BaseStream.Position;
        for (int i = 0; i < _builder.Exports.Count; i++)
        {
            writer.Write(0); // Empty dependency count for this export
        }
        
        // Asset registry data - write count=0 for cooked packages
        // retoc writes this section and sets AssetRegistryDataOffset to point here
        long assetRegistryDataOffset = writer.BaseStream.Position;
        writer.Write(0); // asset_object_data_count
        
        // Write preload dependencies
        long preloadDepOffset = writer.BaseStream.Position;
        foreach (var dep in _builder.PreloadDependencies)
        {
            writer.Write(dep.Index);
        }
        
        // Write data resources (UE5.3+ format) - only if there are actual resources
        // retoc skips this section entirely and writes DataResourceOffset = -1 when empty
        long dataResourceOffset = -1;
        if (_builder.DataResources.Count > 0)
        {
            dataResourceOffset = writer.BaseStream.Position;
            writer.Write((uint)0); // DataResourceVersion = 0 (Initial)
            writer.Write(_builder.DataResources.Count);
            foreach (var res in _builder.DataResources)
            {
                WriteDataResource(writer, res);
            }
        }
        
        // Update header size
        long headerSize = writer.BaseStream.Position;
        
        // Go back and fill in offsets
        writer.BaseStream.Seek(headerSizePos, SeekOrigin.Begin);
        writer.Write((int)headerSize);
        
        writer.BaseStream.Seek(nameOffsetPos, SeekOrigin.Begin);
        writer.Write((int)nameOffset);
        
        // SoftObjectPathsOffset should point to ImportOffset when count is 0
        writer.BaseStream.Seek(softObjOffsetPos, SeekOrigin.Begin);
        writer.Write((int)importOffset);
        
        writer.BaseStream.Seek(importOffsetPos, SeekOrigin.Begin);
        writer.Write((int)importOffset);
        
        writer.BaseStream.Seek(exportOffsetPos, SeekOrigin.Begin);
        writer.Write((int)exportOffset);
        
        writer.BaseStream.Seek(dependsOffsetPos, SeekOrigin.Begin);
        writer.Write((int)dependsOffset);
        
        // SoftPackageReferencesOffset - leave as 0 for cooked packages
        // (don't need to fill in, already written as 0)
        
        writer.BaseStream.Seek(assetRegistryDataOffsetPos, SeekOrigin.Begin);
        writer.Write((int)assetRegistryDataOffset);
        
        writer.BaseStream.Seek(preloadDepOffsetPos, SeekOrigin.Begin);
        writer.Write((int)preloadDepOffset);
        
        writer.BaseStream.Seek(dataResourceOffsetPos, SeekOrigin.Begin);
        writer.Write((int)dataResourceOffset);
        
        // Fill in BulkDataStartOffset - it's the end of last export (header + export data)
        long totalExportSize = 0;
        foreach (var exp in _builder.Exports)
        {
            totalExportSize += exp.SerialSize;
        }
        long bulkDataStartOffset = headerSize + totalExportSize;
        writer.BaseStream.Seek(bulkDataStartOffsetPos, SeekOrigin.Begin);
        writer.Write(bulkDataStartOffset);
        
        // Seek to end
        writer.BaseStream.Seek(0, SeekOrigin.End);
    }

    private byte[] RebuildExportData()
    {
        int headerSize = (int)_zenPackage.Summary.HeaderSize;
        
        // If we have real bundle headers (with explicit SerialOffsets), exports need reordering
        // For UE5.3+ NoExportInfo where headers are generated (all SerialOffset=MaxValue),
        // data is already in correct order - just strip the zen header (matching retoc)
        if (_needsToRebuildExportsData)
        {
            return RebuildExportDataFromBundles();
        }
        
        // Fallback: no bundle headers, just strip the header
        int dataLength = _rawPackageData.Length - headerSize;
        
        // Calculate total export serial size + footer
        long totalExportSize = 0;
        foreach (var export in _builder.Exports)
            totalExportSize += export.SerialSize;
        int totalFileSize = (int)totalExportSize + 4; // +4 for PACKAGE_FILE_TAG footer
        
        byte[] result = new byte[totalFileSize];
        Array.Copy(_rawPackageData, headerSize, result, 0, Math.Min(dataLength, (int)totalExportSize));
        
        // Write PACKAGE_FILE_TAG footer
        result[totalFileSize - 4] = 0xC1;
        result[totalFileSize - 3] = 0x83;
        result[totalFileSize - 2] = 0x2A;
        result[totalFileSize - 1] = 0x9E;
        return result;
    }
    
    /// <summary>
    /// Rebuild export data by iterating through export bundle headers and their entries,
    /// using each bundle's SerialOffset to determine where its data starts in the raw package.
    /// Only Serialize commands contribute data; Create commands are zero-size.
    /// Matches retoc's rebuild_asset_export_data_internal.
    /// </summary>
    private byte[] RebuildExportDataFromBundles()
    {
        int zenHeaderSize = (int)_zenPackage.Summary.HeaderSize;
        
        // Calculate total export serial size
        long totalExportSerialSize = 0;
        foreach (var export in _builder.Exports)
            totalExportSerialSize += export.SerialSize;
        
        // We need the legacy header size to convert SerialOffset (which includes
        // the legacy header size) back to a position within the .uexp buffer.
        long legacyHeaderSize = 0;
        if (_builder.Exports.Count > 0)
        {
            legacyHeaderSize = _builder.Exports.Min(e => e.SerialOffset);
        }
        
        // Allocate result buffer: exports + PACKAGE_FILE_TAG footer
        int totalFileSize = (int)totalExportSerialSize + 4;
        byte[] result = new byte[totalFileSize];
        
        // Iterate through export bundle headers, using their SerialOffset to find data position.
        // For UE5+ bundles with explicit SerialOffset, use that to set the starting position.
        // For legacy bundles (SerialOffset=ulong.MaxValue), track running position sequentially.
        // Matches retoc's rebuild_asset_export_data_internal.
        long currentPackageOffset = zenHeaderSize;
        
        for (int bundleIndex = 0; bundleIndex < _zenPackage.ExportBundleHeaders.Count; bundleIndex++)
        {
            var bundleHeader = _zenPackage.ExportBundleHeaders[bundleIndex];
            
            long currentSerialOffset;
            if (bundleHeader.SerialOffset != ulong.MaxValue)
            {
                currentSerialOffset = zenHeaderSize + (long)bundleHeader.SerialOffset;
            }
            else
            {
                currentSerialOffset = currentPackageOffset;
            }
            
            for (uint i = 0; i < bundleHeader.EntryCount; i++)
            {
                int entryIndex = (int)bundleHeader.FirstEntryIndex + (int)i;
                if (entryIndex >= _zenPackage.ExportBundleEntries.Count)
                    break;
                
                var bundleEntry = _zenPackage.ExportBundleEntries[entryIndex];
                int exportIndex = (int)bundleEntry.LocalExportIndex;
                
                if (exportIndex < 0 || exportIndex >= _builder.Exports.Count)
                    continue;
                
                // Only Serialize commands contain actual export data
                if (bundleEntry.CommandType != EExportCommandType.Serialize)
                    continue;
                
                int exportSerialSize = (int)_builder.Exports[exportIndex].SerialSize;
                long targetOffset = _builder.Exports[exportIndex].SerialOffset - legacyHeaderSize;
                
                if (currentSerialOffset >= 0 && currentSerialOffset + exportSerialSize <= _rawPackageData.Length &&
                    targetOffset >= 0 && targetOffset + exportSerialSize <= totalExportSerialSize)
                {
                    Array.Copy(_rawPackageData, currentSerialOffset, result, (int)targetOffset, exportSerialSize);
                }
                
                currentSerialOffset += exportSerialSize;
            }
            
            currentPackageOffset = currentSerialOffset;
        }
        
        // Write PACKAGE_FILE_TAG footer
        result[totalFileSize - 4] = 0xC1;
        result[totalFileSize - 3] = 0x83;
        result[totalFileSize - 2] = 0x2A;
        result[totalFileSize - 1] = 0x9E;
        
        return result;
    }

    private int ResolveLocalPackageObject(FPackageObjectIndex packageObject)
    {
        if (packageObject.IsNull())
            return 0; // Null package index
        
        if (packageObject.IsExport())
            return (int)packageObject.GetExportIndex() + 1; // Positive = export
        
        // Check cache
        if (_zenImportLookup.TryGetValue(packageObject.Value, out int cached))
            return -(cached + 1); // Negative = import
        
        // Resolve the import
        var resolved = ResolveGenericZenImport(packageObject);
        int importIndex = FindOrAddResolvedImport(resolved);
        _zenImportLookup[packageObject.Value] = importIndex;
        
        return -(importIndex + 1);
    }

    private int ResolveWithFallback(FPackageObjectIndex packageObject, string context)
    {
        try
        {
            return ResolveLocalPackageObject(packageObject);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to resolve {context}: {ex.Message}");
            
            int nullPackage = CreateAndAddUnknownPackageImport();
            var nullImport = CreateUnknownObjectImportEntry(nullPackage);
            int importIndex = _builder.Imports.Count;
            _builder.Imports.Add(nullImport);
            _zenImportLookup[packageObject.Value] = importIndex;
            
            return -(importIndex + 1);
        }
    }

    private FPackageIndex ResolveWithFallbackAsPackageIndex(FPackageObjectIndex packageObject, string context)
    {
        int resolved = ResolveWithFallback(packageObject, context);
        return new FPackageIndex(resolved);
    }

    private ResolvedZenImport ResolveGenericZenImport(FPackageObjectIndex import)
    {
        if (import.IsScriptImport())
        {
            return ResolveScriptImport(import);
        }
        else if (import.IsPackageImport())
        {
            return ResolvePackageImport(import);
        }
        else if (import.IsExport())
        {
            return ResolveExportAsImport(import);
        }
        
        throw new Exception($"Cannot resolve null package object index: {import}");
    }

    private ResolvedZenImport ResolveScriptImport(FPackageObjectIndex import)
    {
        if (_scriptObjects == null)
        {
            // Without script objects, create a generic import
            return new ResolvedZenImport
            {
                ClassPackage = CORE_OBJECT_PACKAGE_NAME,
                ClassName = OBJECT_CLASS_NAME,
                ObjectName = $"__ScriptImport_{import.Value:X16}__"
            };
        }
        
        var scriptObject = _scriptObjects.GetScriptObject(import);
        if (scriptObject == null)
        {
            return new ResolvedZenImport
            {
                ClassPackage = CORE_OBJECT_PACKAGE_NAME,
                ClassName = OBJECT_CLASS_NAME,
                ObjectName = $"__ScriptImport_{import.Value:X16}__"
            };
        }
        
        string objectName = _scriptObjects.GetName(scriptObject.ObjectName);
        
        
        if (scriptObject.OuterIndex.IsNull())
        {
            // This is a package
            return new ResolvedZenImport
            {
                ClassPackage = CORE_OBJECT_PACKAGE_NAME,
                ClassName = PACKAGE_CLASS_NAME,
                ObjectName = objectName
            };
        }
        
        // Resolve outer
        var resolvedOuter = ResolveScriptImport(scriptObject.OuterIndex);
        
        // Check if this is a class (pointed to by a CDO)
        if (_scriptObjects.IsClass(import))
        {
            return new ResolvedZenImport
            {
                ClassPackage = CORE_OBJECT_PACKAGE_NAME,
                ClassName = CLASS_CLASS_NAME,
                ObjectName = objectName,
                Outer = resolvedOuter
            };
        }
        
        // Check if this is a CDO (Default__ prefix, outer is package, has cdo_class_index)
        bool isCdoObject = resolvedOuter.Outer == null && objectName.StartsWith("Default__");
        if (isCdoObject && !scriptObject.CdoClassIndex.IsNull())
        {
            // Resolve the CDO's class
            var resolvedClass = ResolveScriptImport(scriptObject.CdoClassIndex);
            
            // Find the package by traversing up the outer chain
            string classPackage = GetPackageFromImport(resolvedClass);
            
            return new ResolvedZenImport
            {
                ClassPackage = classPackage,
                ClassName = resolvedClass.ObjectName,
                ObjectName = objectName,
                Outer = resolvedOuter
            };
        }
        
        // Default to Object
        return new ResolvedZenImport
        {
            ClassPackage = CORE_OBJECT_PACKAGE_NAME,
            ClassName = OBJECT_CLASS_NAME,
            ObjectName = objectName,
            Outer = resolvedOuter
        };
    }

    private ResolvedZenImport ResolvePackageImport(FPackageObjectIndex import)
    {
        var packageImport = import.GetPackageImport();
        
        if (packageImport.ImportedPackageIndex >= _zenPackage.ImportedPackages.Count)
        {
            throw new Exception($"Package index out of bounds: {packageImport.ImportedPackageIndex}");
        }
        
        ulong importedPackageId = _zenPackage.ImportedPackages[packageImport.ImportedPackageIndex];
        string packageName = packageImport.ImportedPackageIndex < _zenPackage.ImportedPackageNames.Count
            ? _zenPackage.ImportedPackageNames[packageImport.ImportedPackageIndex]
            : $"/Game/__Package_{importedPackageId:X16}__";
        int packageNameNumber = packageImport.ImportedPackageIndex < _zenPackage.ImportedPackageNameNumbers.Count
            ? _zenPackage.ImportedPackageNameNumbers[packageImport.ImportedPackageIndex]
            : 0;
        
        // Create the package import (outer) with base name and FName number separated
        var packageOuter = new ResolvedZenImport
        {
            ClassPackage = CORE_OBJECT_PACKAGE_NAME,
            ClassName = PACKAGE_CLASS_NAME,
            ObjectName = packageName,
            ObjectNameNumber = packageNameNumber
        };
        
        // If we have context, resolve the actual export from the imported package
        if (_context != null)
        {
            var targetPackage = _context.GetPackage(importedPackageId);
            if (targetPackage == null && _debugMode)
            {
                Console.Error.WriteLine($"[DEBUG] Package not found: {importedPackageId:X16} ({packageName})");
            }
            if (targetPackage != null)
            {
                // Find the export by its public hash
                if (packageImport.ImportedPublicExportHashIndex < _zenPackage.ImportedPublicExportHashes.Count)
                {
                    ulong exportHash = _zenPackage.ImportedPublicExportHashes[packageImport.ImportedPublicExportHashIndex];
                    
                    // O(1) lookup via cached hash→exportIndex dictionary (built once per package).
                    // Replaces the old O(N) foreach that called CityHash64 on every export per lookup.
                    int foundExportIdx = _context.GetExportIndexByHash(importedPackageId, exportHash, _scriptObjects);
                    
                    if (foundExportIdx >= 0)
                    {
                        var export = targetPackage.ExportMap[foundExportIdx];

                        if (_debugMode)
                        {
                            Console.Error.WriteLine($"[DEBUG] Resolving export from {packageName}:");
                            Console.Error.WriteLine($"  ObjectName.Index={export.ObjectName.Index}, Type={export.ObjectName.Type}");
                            if (export.ObjectName.Index < targetPackage.NameMap.Count)
                                Console.Error.WriteLine($"  Name at index={targetPackage.NameMap[(int)export.ObjectName.Index]}");
                        }

                        var (exportBaseName, exportNumber) = GetExportBaseNameAndNumber(targetPackage, export.ObjectName);
                        var (className, classPackage) = ResolveExportClassInfo(targetPackage, export.ClassIndex);

                        return new ResolvedZenImport
                        {
                            ClassPackage = classPackage,
                            ClassName = className,
                            ObjectName = exportBaseName,
                            ObjectNameNumber = exportNumber,
                            // Rebuild the object's real outer chain inside the target package rather
                            // than parenting straight to the package. A nested object is addressed as
                            // "Outer/Inner", and that path is what the public export hash is computed
                            // from, so flattening it here silently destroyed the path: repacking then
                            // hashed the bare leaf and produced an import no package could resolve.
                            // Correct for top-level objects either way, which is why only nested
                            // imports (lobby ShowBP/LikeBP referencing components of another
                            // blueprint's CDO) broke.
                            Outer = BuildImportOuterChain(targetPackage, export.OuterIndex, packageOuter, 0)
                        };
                    }
                    else
                    {
                        if (_debugMode)
                        {
                            Console.Error.WriteLine($"[DEBUG] Export hash not found: {exportHash:X16} in {packageName} (has {targetPackage.ExportMap.Count} exports)");
                        }

                        // Fallback: if only one export, use it directly
                        if (targetPackage.ExportMap.Count == 1)
                        {
                            var export = targetPackage.ExportMap[0];
                            var (fbBaseName, fbNumber) = GetExportBaseNameAndNumber(targetPackage, export.ObjectName);
                            var (fbClassName, fbClassPackage) = ResolveExportClassInfo(targetPackage, export.ClassIndex);
                            return new ResolvedZenImport { ClassPackage = fbClassPackage, ClassName = fbClassName, ObjectName = fbBaseName, ObjectNameNumber = fbNumber, Outer = packageOuter };
                        }

                        // Fallback: use hash index positionally
                        int exportIdx = (int)packageImport.ImportedPublicExportHashIndex;
                        if (exportIdx < targetPackage.ExportMap.Count)
                        {
                            var export = targetPackage.ExportMap[exportIdx];
                            var (fb2BaseName, fb2Number) = GetExportBaseNameAndNumber(targetPackage, export.ObjectName);
                            var (fb2ClassName, fb2ClassPackage) = ResolveExportClassInfo(targetPackage, export.ClassIndex);
                            return new ResolvedZenImport { ClassPackage = fb2ClassPackage, ClassName = fb2ClassName, ObjectName = fb2BaseName, ObjectNameNumber = fb2Number, Outer = packageOuter };
                        }
                    }
                }
            }
        }
        
        // Fallback: create a generic object import with export hash index as name suffix
        // Try to get a meaningful name from the export hash index
        string fallbackName = $"Export_{packageImport.ImportedPublicExportHashIndex}";
        if (_debugMode)
        {
            Console.Error.WriteLine($"[DEBUG] Fallback import: pkg={packageName}, hashIdx={packageImport.ImportedPublicExportHashIndex}");
        }
        return new ResolvedZenImport
        {
            ClassPackage = CORE_OBJECT_PACKAGE_NAME,
            ClassName = OBJECT_CLASS_NAME,
            ObjectName = fallbackName,
            Outer = packageOuter
        };
    }

    private ResolvedZenImport ResolveExportAsImport(FPackageObjectIndex import)
    {
        int exportIndex = (int)import.GetExportIndex();
        if (exportIndex >= _zenPackage.ExportMap.Count)
        {
            throw new Exception($"Export index out of bounds: {exportIndex}");
        }
        
        var export = _zenPackage.ExportMap[exportIndex];
        string exportName = _zenPackage.GetName(export.ObjectName);
        
        // Resolve the class of this export
        string className = OBJECT_CLASS_NAME;
        string classPackage = CORE_OBJECT_PACKAGE_NAME;
        
        if (!export.ClassIndex.IsNull())
        {
            // Resolve the class import to get class name and package
            var resolvedClass = ResolveGenericZenImport(export.ClassIndex);
            className = resolvedClass.ObjectName;
            // Get the package by traversing up the outer chain
            classPackage = GetPackageFromImport(resolvedClass);
        }
        
        ResolvedZenImport? outer = null;
        if (!export.OuterIndex.IsNull())
        {
            outer = ResolveGenericZenImport(export.OuterIndex);
        }
        else
        {
            // Top-level export, outer is the package
            outer = new ResolvedZenImport
            {
                ClassPackage = CORE_OBJECT_PACKAGE_NAME,
                ClassName = PACKAGE_CLASS_NAME,
                ObjectName = _zenPackage.SourcePackageName()
            };
        }
        
        return new ResolvedZenImport
        {
            ClassPackage = classPackage,
            ClassName = className,
            ObjectName = exportName,
            Outer = outer
        };
    }

    /// <summary>
    /// Rebuild the chain of outers for an object imported from another package, so the legacy
    /// import table mirrors the real hierarchy (package -> CDO -> component) instead of parenting
    /// every imported object directly to its package.
    ///
    /// The chain matters because a public export hash is computed from the object's path within
    /// its package. Flattening it loses that path, and since the hash cannot be inverted, the
    /// packer had no way to reconstruct it later.
    /// </summary>
    private ResolvedZenImport BuildImportOuterChain(FZenPackageHeader targetPackage, FPackageObjectIndex outerIndex, ResolvedZenImport packageOuter, int depth)
    {
        // Anything that is not an export in the target package is the package itself.
        if (depth > 16 || outerIndex.IsNull() || !outerIndex.IsExport())
            return packageOuter;

        int idx = (int)outerIndex.GetExportIndex();
        if (idx < 0 || idx >= targetPackage.ExportMap.Count)
            return packageOuter;

        var outerExport = targetPackage.ExportMap[idx];
        var (baseName, number) = GetExportBaseNameAndNumber(targetPackage, outerExport.ObjectName);
        var (className, classPackage) = ResolveExportClassInfo(targetPackage, outerExport.ClassIndex);

        return new ResolvedZenImport
        {
            ClassPackage = classPackage,
            ClassName = className,
            ObjectName = baseName,
            ObjectNameNumber = number,
            Outer = BuildImportOuterChain(targetPackage, outerExport.OuterIndex, packageOuter, depth + 1)
        };
    }

    private int FindOrAddResolvedImport(ResolvedZenImport import)
    {
        // Check if we already have this import
        if (_resolvedImportLookup.TryGetValue(import, out int existing))
            return existing;
        
        // Resolve outer first
        int outerIndex = 0;
        if (import.Outer != null)
        {
            outerIndex = -(FindOrAddResolvedImport(import.Outer) + 1);
        }
        
        // Store base names in the name map, keep FName numbers separate.
        // The ObjectName on ResolvedZenImport is the BASE name (without number suffix).
        // The ObjectNameNumber carries the FName Number field separately.
        int classPackageIdx = StoreOrFindName(import.ClassPackage);
        int classNameIdx = StoreOrFindName(import.ClassName);
        int objectNameIdx = StoreOrFindName(import.ObjectName);
        
        // Create import entry
        int newIndex = _builder.Imports.Count;
        _builder.Imports.Add(new LegacyObjectImport
        {
            ClassPackage = classPackageIdx,
            ClassPackageNumber = 0,
            ClassName = classNameIdx,
            ClassNameNumber = 0,
            OuterIndex = new FPackageIndex(outerIndex),
            ObjectName = objectNameIdx,
            ObjectNameNumber = import.ObjectNameNumber,
            IsOptional = false
        });
        
        _resolvedImportLookup[import] = newIndex;
        return newIndex;
    }

    /// <summary>
    /// Get the base name and FName number from an export's ObjectName in a target package.
    /// Unlike GetName() which bakes the number into the string, this keeps them separate
    /// for correct legacy import entry construction.
    /// </summary>
    private (string baseName, int number) GetExportBaseNameAndNumber(FZenPackageHeader targetPackage, FMappedName objectName)
    {
        string baseName;
        if (objectName.Type == EMappedNameType.Package)
        {
            baseName = objectName.Index < targetPackage.NameMap.Count
                ? targetPackage.NameMap[(int)objectName.Index]
                : $"__UNKNOWN_NAME_{objectName.Type}_{objectName.Index}__";
        }
        else if (objectName.Type == EMappedNameType.Global && _scriptObjects != null)
        {
            // For global names, ScriptObjectsDatabase returns the full name with number baked in
            // We need to extract base name and number ourselves
            string fullName = _scriptObjects.GetName(objectName);
            // If Number > 0, the suffix was baked in by ScriptObjectsDatabase.GetName
            // But for global names used as export names, Number is typically 0
            if (objectName.Number > 0)
            {
                // Strip the baked-in suffix to get base name
                string expectedSuffix = $"_{objectName.Number - 1}";
                if (fullName.EndsWith(expectedSuffix))
                    baseName = fullName.Substring(0, fullName.Length - expectedSuffix.Length);
                else
                    baseName = fullName;
            }
            else
            {
                baseName = fullName;
            }
        }
        else
        {
            baseName = $"__UNKNOWN_NAME_{objectName.Type}_{objectName.Index}__";
        }
        
        return (baseName, (int)objectName.Number);
    }

    private int StoreOrFindName(string name)
    {
        int idx = _builder.NameMap.IndexOf(name);
        if (idx >= 0) return idx;
        
        idx = _builder.NameMap.Count;
        _builder.NameMap.Add(name);
        return idx;
    }
    
    /// <summary>
    /// Parse a name with potential numeric suffix (e.g., "PyAbility_105701")
    /// Returns the base name and the number (stored as number+1 in FName format)
    /// </summary>
    private (string baseName, int number) ParseNameWithNumber(string fullName)
    {
        // Check if the name ends with _N where N is a number
        int lastUnderscore = fullName.LastIndexOf('_');
        if (lastUnderscore > 0 && lastUnderscore < fullName.Length - 1)
        {
            string suffix = fullName.Substring(lastUnderscore + 1);
            if (int.TryParse(suffix, out int num))
            {
                // Found numeric suffix - return base name and number+1 (FName format)
                return (fullName.Substring(0, lastUnderscore), num + 1);
            }
        }
        // No numeric suffix
        return (fullName, 0);
    }
    
    /// <summary>
    /// Store name with numeric suffix handling - stores base name and returns (index, number)
    /// </summary>
    private (int index, int number) StoreOrFindNameWithNumber(string fullName)
    {
        var (baseName, number) = ParseNameWithNumber(fullName);
        int index = StoreOrFindName(baseName);
        return (index, number);
    }

    /// <summary>
    /// Resolve the class (ClassName + ClassPackage) of an export belonging to a target package.
    /// Handles all three FPackageObjectIndex kinds:
    ///  - ScriptImport: class lives in /Script/* (looked up via ScriptObjectsDatabase)
    ///  - PackageImport: class is a public export in a package imported by targetPackage
    ///    (e.g. a Blueprint-generated class defined in another package)
    ///  - Export: class is another export inside targetPackage itself (common for
    ///    BP-generated classes that live in the same package as their CDO)
    ///
    /// Returns (ClassName, ClassPackage). Defaults to (Object, /Script/CoreUObject) when
    /// resolution fails.
    /// </summary>
    private (string className, string classPackage) ResolveExportClassInfo(
        FZenPackageHeader targetPackage, FPackageObjectIndex classIndex)
    {
        string className = OBJECT_CLASS_NAME;
        string classPackage = CORE_OBJECT_PACKAGE_NAME;

        if (classIndex.IsNull())
            return (className, classPackage);

        // Case 1: /Script/* class (engine type)
        if (classIndex.IsScriptImport())
        {
            if (_scriptObjects == null) return (className, classPackage);
            var classObj = _scriptObjects.GetScriptObject(classIndex);
            if (classObj == null) return (className, classPackage);

            className = _scriptObjects.GetName(classObj.ObjectName);
            if (!classObj.OuterIndex.IsNull())
            {
                var outerObj = _scriptObjects.GetScriptObject(classObj.OuterIndex);
                if (outerObj != null)
                    classPackage = _scriptObjects.GetName(outerObj.ObjectName);
            }
            return (className, classPackage);
        }

        // Case 2: class is a public export of another package imported by targetPackage
        // (e.g. Blueprint class defined in a sibling package)
        if (classIndex.IsPackageImport())
        {
            var (pkgIdx, hashIdx) = classIndex.GetPackageImport();
            if (pkgIdx < 0 || pkgIdx >= targetPackage.ImportedPackages.Count)
                return (className, classPackage);

            string importedPkgName = pkgIdx < targetPackage.ImportedPackageNames.Count
                ? targetPackage.ImportedPackageNames[pkgIdx]
                : string.Empty;
            ulong importedPkgId = targetPackage.ImportedPackages[pkgIdx];

            if (_context != null && hashIdx >= 0 && hashIdx < targetPackage.ImportedPublicExportHashes.Count)
            {
                ulong expHash = targetPackage.ImportedPublicExportHashes[hashIdx];
                var classPkg = _context.GetPackage(importedPkgId);
                if (classPkg != null)
                {
                    foreach (var exp in classPkg.ExportMap)
                    {
                        string expName = classPkg.GetName(exp.ObjectName, _scriptObjects);
                        ulong computedHash = IoStore.CityHash.CityHash64(expName.ToLowerInvariant());
                        if (exp.PublicExportHash == expHash || computedHash == expHash)
                        {
                            className = expName;
                            break;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(importedPkgName))
                classPackage = importedPkgName;
            return (className, classPackage);
        }

        // Case 3: class is another export inside targetPackage itself
        // (common: BP-generated class sits alongside its CDO in the same package)
        if (classIndex.IsExport())
        {
            int expIdx = (int)classIndex.GetExportIndex();
            if (expIdx >= 0 && expIdx < targetPackage.ExportMap.Count)
            {
                var classExp = targetPackage.ExportMap[expIdx];
                className = targetPackage.GetName(classExp.ObjectName, _scriptObjects);
                classPackage = targetPackage.SourcePackageName();
            }
            return (className, classPackage);
        }

        return (className, classPackage);
    }

    /// <summary>
    /// Traverse the outer chain to find the package (the top-level object with no outer)
    /// </summary>
    private string GetPackageFromImport(ResolvedZenImport import)
    {
        // If no outer, this IS the package
        if (import.Outer == null)
            return import.ObjectName;
        
        // Traverse up the chain
        var current = import.Outer;
        while (current.Outer != null)
        {
            current = current.Outer;
        }
        return current.ObjectName;
    }

    private int CreateAndAddUnknownPackageImport()
    {
        int newIndex = _builder.Imports.Count;
        _builder.Imports.Add(CreateUnknownPackageImportEntry());
        return newIndex;
    }

    private LegacyObjectImport CreateUnknownPackageImportEntry()
    {
        return new LegacyObjectImport
        {
            ClassPackage = StoreOrFindName(CORE_OBJECT_PACKAGE_NAME),
            ClassName = StoreOrFindName(PACKAGE_CLASS_NAME),
            OuterIndex = FPackageIndex.CreateNull(),
            ObjectName = StoreOrFindName("/Engine/UnknownPackage"),
            IsOptional = false
        };
    }

    private LegacyObjectImport CreateUnknownObjectImportEntry(int outerIndex)
    {
        return new LegacyObjectImport
        {
            ClassPackage = StoreOrFindName(CORE_OBJECT_PACKAGE_NAME),
            ClassName = StoreOrFindName(OBJECT_CLASS_NAME),
            OuterIndex = FPackageIndex.CreateImport(outerIndex),
            ObjectName = StoreOrFindName("UnknownExport"),
            IsOptional = false
        };
    }

    private void WriteString(BinaryWriter writer, string value)
    {
        if (NeedsUtf16(value))
        {
            byte[] bytes = Encoding.Unicode.GetBytes(value);
            // Negative length signals UTF-16LE encoding (char count + null, negated)
            writer.Write(-(value.Length + 1));
            writer.Write(bytes);
            writer.Write((short)0); // UTF-16 null terminator
        }
        else
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length + 1);
            writer.Write(bytes);
            writer.Write((byte)0);
        }
    }

    private void WriteFString(BinaryWriter writer, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.Write(0); // Length 0
            return;
        }
        
        // FString format:
        //   Positive length + UTF-8/Latin1 bytes + null byte  (for ASCII-safe strings)
        //   Negative length + UTF-16LE bytes + null ushort    (for non-ASCII strings)
        if (NeedsUtf16(value))
        {
            byte[] bytes = Encoding.Unicode.GetBytes(value);
            // Negative length = -(charCount + 1) to signal UTF-16LE
            writer.Write(-(value.Length + 1));
            writer.Write(bytes);
            writer.Write((short)0); // UTF-16 null terminator (2 bytes)
        }
        else
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length + 1); // +1 for null terminator
            writer.Write(bytes);
            writer.Write((byte)0); // null terminator
        }
    }

    /// <summary>
    /// Check if a string contains non-ASCII characters that require UTF-16LE encoding.
    /// UE's FString uses negative length to signal UTF-16LE for strings with characters > 0x7F.
    /// </summary>
    private static bool NeedsUtf16(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (char c in value)
        {
            if (c > 127) return true;
        }
        return false;
    }

    private void WriteImport(BinaryWriter writer, LegacyObjectImport import)
    {
        // FMinimalName: index (i32) + number (i32)
        writer.Write(import.ClassPackage);
        writer.Write(import.ClassPackageNumber);
        writer.Write(import.ClassName);
        writer.Write(import.ClassNameNumber);
        writer.Write(import.OuterIndex.Index);
        writer.Write(import.ObjectName);
        writer.Write(import.ObjectNameNumber);
        
        // PackageName - written when FilterEditorOnly is false (for One File Per Actor support)
        // For cooked packages this is always an empty FMinimalName
        bool isFilterEditorOnly = (_builder.PackageFlags & 0x80000000) != 0;
        if (!isFilterEditorOnly)
        {
            writer.Write(0); // PackageName index (0 = None)
            writer.Write(0); // PackageName number
        }
        
        // bOptional - written as int32 for UE5.3+ (OptionalResources version)
        writer.Write(import.IsOptional ? 1 : 0);
    }

    private void WriteExport(BinaryWriter writer, LegacyObjectExport export)
    {
        // FPackageIndex (4 bytes each)
        writer.Write(export.ClassIndex.Index);
        writer.Write(export.SuperIndex.Index);
        writer.Write(export.TemplateIndex.Index);
        writer.Write(export.OuterIndex.Index);
        
        // FMinimalName: index (i32) + number (i32)
        writer.Write(export.ObjectName);
        writer.Write(export.ObjectNameNumber);
        
        writer.Write(export.ObjectFlags);
        writer.Write(export.SerialSize);   // i64
        writer.Write(export.SerialOffset); // i64
        
        // is_forced_export: i32 (always 0 for modern packages)
        writer.Write(0);
        
        // is_not_for_client, is_not_for_server: i32
        writer.Write(export.IsNotForClient ? 1 : 0);
        writer.Write(export.IsNotForServer ? 1 : 0);
        
        // For UE5.3 (unversioned), is_inherited_instance is serialized as i32
        writer.Write(0); // is_inherited_instance
        
        // package_flags: u32 (always 0 for modern packages)
        writer.Write((uint)0);
        
        // is_not_always_loaded_for_editor_game, is_asset: i32
        writer.Write(0); // is_not_always_loaded_for_editor_game
        writer.Write(export.IsAsset ? 1 : 0);
        
        // generate_public_hash: i32 (for UE5.3+)
        writer.Write(export.GeneratePublicHash ? 1 : 0);
        
        // Dependency indices (i32 each)
        writer.Write(export.FirstExportDependencyIndex);
        writer.Write(export.SerializeBeforeSerializeDependencies);
        writer.Write(export.CreateBeforeSerializeDependencies);
        writer.Write(export.SerializeBeforeCreateDependencies);
        writer.Write(export.CreateBeforeCreateDependencies);
        
        // Script serialization offsets - only for versioned packages
        // For unversioned (IsUnversioned=true), these are NOT written
    }

    private void WriteDataResource(BinaryWriter writer, LegacyDataResource res)
    {
        writer.Write(res.Flags);
        writer.Write(res.SerialOffset);
        writer.Write(res.DuplicateSerialOffset);
        writer.Write(res.SerialSize);
        writer.Write(res.RawSize);
        writer.Write(res.OuterIndex.Index);
        writer.Write(res.LegacyBulkDataFlags);
    }
}

/// <summary>
/// Resolved import info
/// </summary>
public class ResolvedZenImport : IEquatable<ResolvedZenImport>
{
    public string ClassPackage { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public int ObjectNameNumber { get; set; } = 0; // FName number (0=no suffix, >0 means _{N-1})
    public ResolvedZenImport? Outer { get; set; }

    public bool Equals(ResolvedZenImport? other)
    {
        if (other == null) return false;
        return ClassPackage == other.ClassPackage &&
               ClassName == other.ClassName &&
               ObjectName == other.ObjectName &&
               ObjectNameNumber == other.ObjectNameNumber &&
               Equals(Outer, other.Outer);
    }

    public override bool Equals(object? obj) => Equals(obj as ResolvedZenImport);
    
    public override int GetHashCode()
    {
        return HashCode.Combine(ClassPackage, ClassName, ObjectName, ObjectNameNumber, Outer?.GetHashCode() ?? 0);
    }
}

/// <summary>
/// Standalone export dependencies
/// </summary>
public class StandaloneExportDependencies
{
    public List<FPackageIndex> SerializeBeforeSerialize { get; } = new();
    public List<FPackageIndex> CreateBeforeSerialize { get; } = new();
    public List<FPackageIndex> SerializeBeforeCreate { get; } = new();
    public List<FPackageIndex> CreateBeforeCreate { get; } = new();
}

/// <summary>
/// Legacy package builder
/// </summary>
public class LegacyPackageBuilder
{
    public string PackageName { get; set; } = "";
    public uint PackageFlags { get; set; }
    public LegacyVersioningInfo VersioningInfo { get; set; } = new();
    public List<string> NameMap { get; set; } = new();
    public int NamesReferencedFromExportDataCount { get; set; }
    public List<LegacyObjectImport> Imports { get; set; } = new();
    public List<LegacyObjectExport> Exports { get; set; } = new();
    public List<FPackageIndex> PreloadDependencies { get; set; } = new();
    public List<LegacyDataResource> DataResources { get; set; } = new();
}

public class LegacyVersioningInfo
{
    public int FileVersionUE4 { get; set; }
    public int FileVersionUE5 { get; set; }
    public int LicenseeVersion { get; set; }
    public bool IsUnversioned { get; set; }
    public List<FCustomVersion> CustomVersions { get; set; } = new();
}

public class LegacyObjectImport
{
    public int ClassPackage { get; set; }
    public int ClassPackageNumber { get; set; }
    public int ClassName { get; set; }
    public int ClassNameNumber { get; set; }
    public FPackageIndex OuterIndex { get; set; } = FPackageIndex.CreateNull();
    public int ObjectName { get; set; }
    public int ObjectNameNumber { get; set; }
    public bool IsOptional { get; set; }
}

public class LegacyObjectExport
{
    public FPackageIndex ClassIndex { get; set; } = FPackageIndex.CreateNull();
    public FPackageIndex SuperIndex { get; set; } = FPackageIndex.CreateNull();
    public FPackageIndex TemplateIndex { get; set; } = FPackageIndex.CreateNull();
    public FPackageIndex OuterIndex { get; set; } = FPackageIndex.CreateNull();
    public int ObjectName { get; set; }
    public int ObjectNameNumber { get; set; }
    public uint ObjectFlags { get; set; }
    public long SerialSize { get; set; }
    public long SerialOffset { get; set; }
    public bool IsNotForClient { get; set; }
    public bool IsNotForServer { get; set; }
    public bool IsAsset { get; set; }
    public bool GeneratePublicHash { get; set; }
    public int FirstExportDependencyIndex { get; set; } = -1;
    public int SerializeBeforeSerializeDependencies { get; set; }
    public int CreateBeforeSerializeDependencies { get; set; }
    public int SerializeBeforeCreateDependencies { get; set; }
    public int CreateBeforeCreateDependencies { get; set; }
    public long ScriptSerializationStartOffset { get; set; }
    public long ScriptSerializationEndOffset { get; set; }
}

public class LegacyDataResource
{
    public uint Flags { get; set; }
    public long SerialOffset { get; set; }
    public long DuplicateSerialOffset { get; set; }
    public long SerialSize { get; set; }
    public long RawSize { get; set; }
    public FPackageIndex OuterIndex { get; set; } = FPackageIndex.CreateNull();
    public uint LegacyBulkDataFlags { get; set; }
}

/// <summary>
/// Output from conversion
/// </summary>
public class LegacyAssetBundle
{
    public byte[] AssetData { get; set; } = Array.Empty<byte>();
    public byte[] ExportsData { get; set; } = Array.Empty<byte>();
    public byte[]? BulkData { get; set; }
    public byte[]? OptionalBulkData { get; set; }
    public byte[]? MemoryMappedBulkData { get; set; }
}

