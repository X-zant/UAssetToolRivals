using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Converts between Legacy (.uasset/.uexp) and Zen (.uzenasset) package formats
/// with full control over SerialSize and offset calculations
/// </summary>
public class ZenConverter
{
    private const uint PACKAGE_FILE_TAG = 0x9E2A83C1;
    
    // Static script objects database - loaded once and reused
    private static ScriptObjectsDatabase? _scriptObjectsDb;
    private static readonly object _scriptObjectsLock = new();
    
    // When true, reads MaterialTagAssetUserData from SkeletalMesh packages
    // and injects per-slot FGameplayTagContainer data into FSkeletalMaterial during Zen conversion.
    // This does NOT override the original empty-tag padding logic — it runs as an additional step.
    // Enabled by default. Disable via --no-material-tags CLI flag if needed.
    private static bool _enableMaterialTags = true;
    
    /// <summary>
    /// Enable or disable MaterialTagAssetUserData reading.
    /// When enabled, SkeletalMesh packages with MaterialTagAssetUserData will have
    /// per-slot gameplay tags injected into FSkeletalMaterial during Zen conversion.
    /// Enabled by default.
    /// </summary>
    public static void SetMaterialTagsEnabled(bool enabled)
    {
        _enableMaterialTags = enabled;
        if (!enabled)
            Console.Error.WriteLine("[ZenConverter] MaterialTags injection DISABLED");
    }
    
    /// <summary>
    /// Set the script objects database to use for resolving class hashes
    /// </summary>
    public static void SetScriptObjectsDatabase(ScriptObjectsDatabase db)
    {
        lock (_scriptObjectsLock)
        {
            _scriptObjectsDb = db;
        }
    }
    
    /// <summary>
    /// Try to load script objects database from default location
    /// </summary>
    public static bool TryLoadScriptObjectsDatabase(string? path = null)
    {
        string dbPath = path ?? Path.Combine(AppContext.BaseDirectory, "ScriptObjects.bin");
        if (File.Exists(dbPath))
        {
            try
            {
                var db = ScriptObjectsDatabase.Load(dbPath);
                SetScriptObjectsDatabase(db);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ZenConverter] Failed to load script objects database: {ex.Message}");
            }
        }
        return false;
    }
    
    /// <summary>
    /// Convert Legacy package to Zen format and return the package path from the asset
    /// </summary>
    public static (byte[] ZenData, string PackagePath) ConvertLegacyToZenWithPath(
        string uassetPath,
        EIoContainerHeaderVersion containerVersion = EIoContainerHeaderVersion.NoExportInfo)
    {
        var zenData = ConvertLegacyToZenInternal(uassetPath, containerVersion, out string packagePath, out _, out _);
        return (zenData, packagePath);
    }
    
    /// <summary>
    /// Convert Legacy package to Zen format and return the package path and FZenPackage object
    /// </summary>
    public static (byte[] ZenData, string PackagePath, string GamePath, FZenPackage ZenPackage) ConvertLegacyToZenFull(
        string uassetPath,
        EIoContainerHeaderVersion containerVersion = EIoContainerHeaderVersion.NoExportInfo,
        string? inputRoot = null)
    {
        var zenData = ConvertLegacyToZenInternal(uassetPath, containerVersion, out string packagePath, out FZenPackage zenPackage, out string gamePath, inputRoot);
        return (zenData, packagePath, gamePath, zenPackage);
    }
    
    /// <summary>
    /// Convert Legacy package to Zen format with recalculated SerialSize from actual export data
    /// </summary>
    public static byte[] ConvertLegacyToZen(
        string uassetPath,
        EIoContainerHeaderVersion containerVersion = EIoContainerHeaderVersion.NoExportInfo)
    {
        return ConvertLegacyToZenInternal(uassetPath, containerVersion, out _, out _, out _);
    }

    private static byte[] ConvertLegacyToZenInternal(
        string uassetPath,
        EIoContainerHeaderVersion containerVersion,
        out string packagePath,
        out FZenPackage zenPackageOut,
        out string gamePathOut,
        string? inputRoot = null)
    {
        // Try to load script objects database if not already loaded
        lock (_scriptObjectsLock)
        {
            if (_scriptObjectsDb == null)
            {
                TryLoadScriptObjectsDatabase();
            }
        }
        
        // Load the legacy asset - always skipParsing: true first for safe header-only read.
        // Export class type is available from the header, so we can detect SkeletalMesh etc.
        // Full parsing (skipParsing: false) is only done for SkeletalMesh when material tags need injection.
        // This avoids unnecessary full parsing of textures/materials which can fail intermittently
        // due to shared Usmap state when running in Parallel.ForEach.
        var asset = LoadAsset(uassetPath, skipParsing: true);
        asset.UseSeparateBulkDataFiles = true;

        // Log whether asset uses versioned or unversioned properties
        // The Zen package flags will be set to match the source asset's format
        // Verbose logging disabled for parallel performance

        // Extract package path from asset's FolderName
        string folderName = asset.FolderName?.Value ?? "";
        string assetName = Path.GetFileNameWithoutExtension(uassetPath);
        
        // Determine the game path from FolderName
        string gamePath;
        string normalizedFolder = NormalizePath(folderName);
        if (normalizedFolder.StartsWith("/Game/"))
        {
            gamePath = normalizedFolder;
        }
        else if (normalizedFolder.StartsWith("/Marvel/Content/"))
        {
            gamePath = "/Game" + normalizedFolder.Substring("/Marvel/Content".Length);
        }
        else if (normalizedFolder.Contains("/"))
        {
            // Has path separators, treat as a path
            gamePath = normalizedFolder;
        }
        else
        {
            // FolderName is just the asset name or empty — no valid path
            gamePath = "/Game/" + assetName;
        }
        
        // Ensure gamePath ends with asset name
        if (!gamePath.EndsWith("/" + assetName, StringComparison.OrdinalIgnoreCase) && !gamePath.EndsWith(assetName))
        {
            gamePath = gamePath.TrimEnd('/') + "/" + assetName;
        }
        
        gamePathOut = gamePath;

        // Convert /Game/X to Marvel/Content/X for packagePath (used in UTOC directory index)
        if (gamePath.StartsWith("/Game/"))
        {
            packagePath = "Marvel/Content" + gamePath.Substring(5);
        }
        else
        {
            packagePath = gamePath.TrimStart('/');
        }

        // A package path names the mount but not where the mount lives, so it cannot say that
        // /MarvelGAS/X belongs at Marvel/Plugins/MarvelGAS/Content/X. When the caller knows the
        // root the assets were laid out under, the location on disk answers that directly -
        // extraction mirrors the container, so the relative path is the directory index entry.
        if (!string.IsNullOrEmpty(inputRoot))
        {
            string fullAssetPath = Path.GetFullPath(uassetPath);
            string fullRoot = Path.GetFullPath(inputRoot);
            if (fullAssetPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                string relative = fullAssetPath.Substring(fullRoot.Length)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .TrimStart('/');
                if (relative.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                    relative = relative[..^7];
                if (relative.Length > 0)
                    packagePath = relative;
            }
        }
        
        string uexpPath = Path.ChangeExtension(uassetPath, ".uexp");
        if (!File.Exists(uexpPath))
        {
            throw new FileNotFoundException($"No .uexp file found: {uexpPath}");
        }

        byte[] uexpData = File.ReadAllBytes(uexpPath);
        long headerSize = asset.Exports.Min(e => e.SerialOffset);

        // Check if this is a SkeletalMesh - needed for material parsing and tag injection
        // Export class type is available from header even with skipParsing: true
        bool isSkeletalMesh = asset.Exports.Any(e => 
            e.GetExportClassType()?.Value?.Value == "SkeletalMesh" ||
            e.GetExportClassType()?.Value?.Value?.Contains("SkeletalMesh") == true);

        UAssetAPI.ExportTypes.SkeletalMeshExport? skeletalExport = null;
        if (isSkeletalMesh && _enableMaterialTags)
        {
            // Reload with full parsing for SkeletalMesh material tag injection.
            // Only SkeletalMesh assets need this; textures/materials/blueprints stay skipParsing: true.
            asset = LoadAsset(uassetPath, skipParsing: false);
            asset.UseSeparateBulkDataFiles = true;
            // Recalculate headerSize after reload (full-parsed asset may differ)
            headerSize = asset.Exports.Min(e => e.SerialOffset);
            skeletalExport = asset.Exports.FirstOrDefault(e => e is UAssetAPI.ExportTypes.SkeletalMeshExport)
                as UAssetAPI.ExportTypes.SkeletalMeshExport;
            skeletalExport?.EnsureExtraDataParsed();
            if (skeletalExport?.Materials == null || skeletalExport.Materials.Count == 0)
                skeletalExport = null;
        }
        else if (isSkeletalMesh)
        {
            // Material tags disabled but still SkeletalMesh - no full parse needed
        }

        // MaterialTags: read tags and inject into materials.
        // The MaterialTagAssetUserData export is kept in the package (can't strip it —
        // Extras raw binary has FPackageIndex referencing MorphTargets by export index).
        // Instead, its class import is remapped to /Script/Engine.AssetUserData in BuildImportMap.
        if (_enableMaterialTags && skeletalExport != null && skeletalExport.Materials != null && skeletalExport.Materials.Count > 0)
        {
            try
            {
                var tagResult = MaterialTagReader.ReadFromAsset(asset);
                if (tagResult.FoundUserData)
                {
                    int injected = MaterialTagReader.ApplyTagsToMaterials(
                        skeletalExport.Materials, tagResult, asset);
                    
                    string meshName = System.IO.Path.GetFileNameWithoutExtension(asset.FilePath ?? "");
                    Console.Error.WriteLine($"[MaterialTags] {meshName}: Found UserData with {tagResult.TotalTagCount} tag(s) across {tagResult.SlotTags.Count} slot(s), injected into {injected} material(s)");
                    
                    if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                    {
                        foreach (var diag in tagResult.Diagnostics)
                            Console.Error.WriteLine($"[MaterialTags]   {diag}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MaterialTags] WARNING: Failed to read MaterialTagAssetUserData: {ex.Message}");
            }
        }

        // DefaultHiddenMaterials: read per-LOD bool[] from the AssetUserData carrier
        // and inject as a TArray<bool> property into each FSkeletalMeshLODInfo struct.
        // This is the proprietary Marvel Rivals field at offset 0x0018 inside FSkeletalMeshLODInfo;
        // missing it causes deserialization access violations on SkeletalMesh load.
        // Reuses the same plugin/import-remap pattern as MaterialTags.
        if (_enableMaterialTags && skeletalExport != null)
        {
            try
            {
                var hiddenResult = HiddenMaterialsReader.ReadFromAsset(asset);
                if (hiddenResult.FoundUserData)
                {
                    int injected = HiddenMaterialsReader.InjectIntoLodInfo(
                        skeletalExport, hiddenResult, asset);

                    string meshName = System.IO.Path.GetFileNameWithoutExtension(asset.FilePath ?? "");
                    Console.Error.WriteLine($"[HiddenMaterials] {meshName}: Found UserData with {hiddenResult.PerLodFlags.Count} LOD list(s), injected DefaultHiddenMaterials into {injected} LOD struct(s)");

                    if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                    {
                        foreach (var diag in hiddenResult.Diagnostics)
                            Console.Error.WriteLine($"[HiddenMaterials]   {diag}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HiddenMaterials] WARNING: Failed to inject DefaultHiddenMaterials: {ex.Message}");
            }
        }

        // Check if this is a StaticMesh or StringTable
        bool isStaticMesh = asset.Exports.Any(e => 
            e.GetExportClassType()?.Value?.Value == "StaticMesh");
        
        bool isStringTable = asset.Exports.Any(e => 
            e.GetExportClassType()?.Value?.Value == "StringTable" ||
            e.GetExportClassType()?.Value?.Value?.EndsWith("StringTable") == true);
        
        int materialPaddingToAdd = 0;
        int stringTablePaddingToAdd = 0;
        if (isSkeletalMesh && skeletalExport != null)
        {
                // CRITICAL: If source already had FGameplayTagContainer (extracted from game),
                // DO NOT re-serialize! Re-serialization would add 4-byte ObjectGuid booleans to
                // inner objects like MorphTargets, causing serial size mismatches.
                // Files extracted from IoStore already have correct format - pass through raw data.
                //
                // Also check for single-material meshes where detection failed:
                // If _originalMaterialsByteLength > 4 + (40 * materialCount), padding already exists.
                bool hasGameplayTags = skeletalExport.SourceHadGameplayTags;
                if (!hasGameplayTags && skeletalExport.Materials?.Count > 0)
                {
                    int expectedLenNoTags = 4 + (40 * skeletalExport.Materials.Count);  // count + 40 bytes per mat
                    int expectedLenWithTags = 4 + (44 * skeletalExport.Materials.Count); // count + 44 bytes per mat
                    int actualLen = skeletalExport.GetOriginalMaterialsByteLength();

                    // If actual length matches or exceeds the "with tags" size, padding is present
                    if (actualLen >= expectedLenWithTags)
                    {
                        hasGameplayTags = true;
                    }
                }
                
                if (hasGameplayTags)
                {
                    // Source already has correct format - no re-serialization needed
                    // Just use the original uexpData as-is
                }
                else
                {
                    // Source needs FGameplayTagContainer padding - re-serialize
                    // If MaterialTags injection ran above, the containers now hold actual tags.
                    // If not, they remain empty (count=0, 4 bytes each) — the original behavior.
                    skeletalExport.IncludeGameplayTags = true;
                    var reserializedData = ReserializeExportData(asset);
                    if (reserializedData != null && reserializedData.Length > 0)
                    {
                        int sizeDiff = reserializedData.Length - uexpData.Length;
                        // Verbose logging disabled for parallel performance
                        uexpData = reserializedData;
                        // WriteData() already updated SerialSize on exports, so no extra padding needed.
                        // materialPaddingToAdd stays 0 — SerialSize is already correct.
                        // WriteData() recalculates SerialOffset values based on a new header size.
                        headerSize = asset.Exports.Min(e => e.SerialOffset);
                    }
                }
        }
        else if (isSkeletalMesh)
        {
                // Materials weren't parsed by SkeletalMeshExport
                // This could mean:
                // 1. The file already has FGameplayTagContainer (extracted from game) - no padding needed
                // 2. Parsing failed for some other reason
                // For files extracted from the game, they already have the correct format
                // Verbose logging disabled for parallel performance
        }
        else if (isStaticMesh)
        {
            // NOTE: StaticMesh unversioned property conversion is DISABLED.
            // UAssetAPI's usmap schema produces different global property indices than
            // the game's runtime class hierarchy (e.g. mod indices 17,18,19 vs game indices 2,3,5).
            // This causes "Bad export index" crashes because the game misreads property data.
            // Standard UE5 versioned (tagged) properties are compatible with the game's loader
            // as long as the binary Extras (FStaticMeshRenderData) are correctly formatted.
            if (!asset.HasUnversionedProperties)
            {
                Console.Error.WriteLine("[ZenConverter] SM: keeping versioned (tagged) properties — unversioned conversion disabled due to schema index mismatch");
            }

            // Step 1: Expand ScreenSize from standard UE5 format (64 bytes) to Marvel Rivals format (128 bytes).
            // Marvel Rivals uses 16-byte ScreenSize entries (bCooked+Default+PerPlatformCount+PlatformValue)
            // Default UE5 uses 8-byte entries (bCooked+Default). We must expand 64→128 bytes.
            var patchedStaticMesh = PatchStaticMeshScreenSize(uexpData);
            if (patchedStaticMesh != null)
            {
                int sizeDiff = patchedStaticMesh.Length - uexpData.Length;
                uexpData = patchedStaticMesh;
                materialPaddingToAdd = sizeDiff;
                headerSize = asset.Exports.Min(e => e.SerialOffset);
                Console.Error.WriteLine($"[ZenConverter] StaticMesh ScreenSize expanded: +{sizeDiff} bytes");
            }
        }
        
        if (isStringTable)
        {
            // For StringTable, use UAssetAPI's proper serialization which automatically includes
            // FGameplayTagContainer padding via StringTableExport.Write()
            // Re-serialize the asset to get properly formatted export data
            var reserializedData = ReserializeExportData(asset);
            if (reserializedData != null && reserializedData.Length > 0)
            {
                int sizeDiff = reserializedData.Length - uexpData.Length;
                if (sizeDiff != 0)
                {
                    // Verbose logging disabled for parallel performance
                    uexpData = reserializedData;
                    // WriteData() already updated SerialSize on exports, so no extra padding needed.
                    // stringTablePaddingToAdd stays 0 — SerialSize is already correct.
                    headerSize = asset.Exports.Min(e => e.SerialOffset);
                }
            }
        }
        
        // materialPaddingToAdd is only set for binary-patched data (e.g. StaticMesh ScreenSize expansion)
        // where SerialSize was NOT updated by WriteData(). For re-serialized data (SkeletalMesh, StringTable),
        // WriteData() already updated SerialSize, so their padding stays 0.
        int totalPaddingToAdd = materialPaddingToAdd + stringTablePaddingToAdd;

        // Build Zen package
        var zenPackage = new FZenPackage
        {
            ContainerVersion = containerVersion,
            Summary = new FZenPackageSummary(),
            NameMap = new List<string>(),
            ImportMap = new List<FPackageObjectIndex>(),
            ExportMap = new List<FExportMapEntry>(),
            ExportBundleHeaders = new List<FExportBundleHeader>(),
            ExportBundleEntries = new List<FExportBundleEntry>(),
            DependencyBundleHeaders = new List<FDependencyBundleHeader>(),
            DependencyBundleEntries = new List<FDependencyBundleEntry>()
        };

        // Build name map from legacy asset (includes package name)
        BuildNameMap(asset, zenPackage, gamePath);

        // Set package name and flags from legacy asset
        SetPackageSummary(asset, zenPackage);

        // Build import map with proper remapping
        BuildImportMap(asset, zenPackage);

        // Build export map with RECALCULATED SerialSize from actual data
        BuildExportMapWithRecalculatedSizes(asset, zenPackage, uexpData, headerSize, totalPaddingToAdd);

        // Build export bundles (required for UE5 to load assets)
        BuildExportBundles(asset, zenPackage);

        // Build dependency bundles (for UE5.3+ NoExportInfo version)
        if (containerVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            BuildDependencyBundles(asset, zenPackage);
        }

        // Write Zen package to memory
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        WriteZenPackage(asset, writer, zenPackage, uexpData, headerSize);

        zenPackageOut = zenPackage;
        return ms.ToArray();
    }

    /// <summary>
    /// Build export map with SerialSize recalculated from actual export data in .uexp
    /// This ensures the size matches the real data, not what the header claims
    /// </summary>
    private static void BuildExportMapWithRecalculatedSizes(
        UAsset asset,
        FZenPackage zenPackage,
        byte[] uexpData,
        long headerSize,
        int materialPaddingToAdd = 0)
    {
        // Calculate export sizes from the actual data
        // For non-last exports, use the gap between SerialOffsets (these are relative positions)
        // For the last export, use the remaining data length
        var exportSizes = new Dictionary<Export, long>();
        var sortedByOffset = asset.Exports.OrderBy(e => e.SerialOffset).ToList();
        
        // For non-last exports, use the gap between SerialOffsets (these don't change during re-serialization)
        // For the last export, give it all remaining data (which includes any padding added)
        long sizeOfOtherExports = 0;
        for (int i = 0; i < sortedByOffset.Count - 1; i++)
        {
            var export = sortedByOffset[i];
            var nextExport = sortedByOffset[i + 1];
            // Use offset gap for non-last exports (more reliable than SerialSize)
            long size = nextExport.SerialOffset - export.SerialOffset;
            exportSizes[export] = size;
            sizeOfOtherExports += size;
        }
        
        // Last export gets its original SerialSize plus any padding added by binary patching
        // (e.g. StaticMesh ScreenSize expansion)
        // For re-serialized data (WriteData path), SerialSize is already updated by UAssetAPI.
        if (sortedByOffset.Count > 0)
        {
            var lastExport = sortedByOffset[sortedByOffset.Count - 1];
            exportSizes[lastExport] = lastExport.SerialSize + materialPaddingToAdd;
        }
        
        // Keep original export order - reordering corrupts the data
        // The asset type icon in FModel is cosmetic, data integrity is critical
        var exportsToProcess = asset.Exports.ToList();

        // Note: .ubulk bulk data is stored in a SEPARATE IoStore chunk, NOT added to export size
        // The CookedSerialSize should only include the .uexp data, not bulk data
        string ubulkPath = Path.ChangeExtension(asset.FilePath, ".ubulk");
        if (File.Exists(ubulkPath))
        {
            long ubulkSize = new FileInfo(ubulkPath).Length;
            // Verbose logging disabled for parallel performance
        }

        // Get package name for public export hash calculation
        string packageName = Path.GetFileNameWithoutExtension(asset.FilePath);

        // Process exports in reordered list (main asset last)
        for (int i = 0; i < exportsToProcess.Count; i++)
        {
            var export = exportsToProcess[i];
            
            // Get pre-calculated size (padding already included for last export)
            long actualSize = exportSizes[export];
            // Verbose logging disabled for parallel performance

            // Remap indices from legacy FPackageIndex to Zen FPackageObjectIndex
            // Look up the import map which was already built with correct script import hashes
            var outerIndex = RemapLegacyPackageIndex(export.OuterIndex, zenPackage);
            var classIndex = RemapLegacyPackageIndex(export.ClassIndex, zenPackage);
            var superIndex = RemapLegacyPackageIndex(export.SuperIndex, zenPackage);
            var templateIndex = RemapLegacyPackageIndex(export.TemplateIndex, zenPackage);
            
            // Debug: show remapped indices for first few exports
            // Verbose logging disabled for parallel performance

            // Calculate public export hash for public exports
            // RF_Public = 0x00000001
            // Also check GeneratePublicHash: in UE5.3+ NoExportInfo format, the game's zen exports
            // may not have RF_Public in ObjectFlags but ARE public (tracked via GeneratePublicHash).
            // Our zen→legacy extraction sets this flag, so we must honor it here.
            bool isPublic = (export.ObjectFlags & UAssetAPI.UnrealTypes.EObjectFlags.RF_Public) != 0
                            || export.GeneratePublicHash;
            ulong publicExportHash = 0;
            if (isPublic)
            {
                // Hash the export's path within the package, not just its leaf name. For a
                // top-level export the two are the same, but a nested export is addressed as
                // "Outer/Inner", and that is what other packages hash when they import it (see
                // BuildImportMap, which already resolves imports this way).
                //
                // Hashing the leaf alone gave every nested export a hash no importer could
                // match, so a repacked package broke every reference into it and the game
                // crashed on load. Blueprints were worst hit because almost all of their
                // exports are nested: components, SCS nodes and _GEN_VARIABLE objects.
                publicExportHash = CalculatePublicExportHash(BuildExportPath(asset, export));
            }

            // Preserve FilterFlags from legacy export - retoc preserves these and the game expects them
            EExportFilterFlags filterFlags = EExportFilterFlags.None;
            if (export.bNotForClient)
                filterFlags = EExportFilterFlags.NotForClient;
            else if (export.bNotForServer)
                filterFlags = EExportFilterFlags.NotForServer;

            // Create Zen export entry with ACTUAL calculated size
            // CookedSerialOffset is relative to CookedHeaderSize (which equals HeaderSize in Zen packages)
            // We store the offset relative to the start of export data (after header)
            long exportOffset = export.SerialOffset - headerSize;
            var zenExport = new FExportMapEntry
            {
                CookedSerialOffset = (ulong)exportOffset,
                CookedSerialSize = (ulong)actualSize,
                ObjectName = MapNameToZen(zenPackage, export.ObjectName?.Value?.Value, export.ObjectName?.Number ?? 0),
                ObjectFlags = (uint)export.ObjectFlags,
                FilterFlags = filterFlags,
                OuterIndex = outerIndex,
                ClassIndex = classIndex,
                SuperIndex = superIndex,
                TemplateIndex = templateIndex,
                PublicExportHash = publicExportHash
            };
            

            // Log if size differs from header
            // Verbose logging disabled for parallel performance

            zenPackage.ExportMap.Add(zenExport);
        }
    }

    /// <summary>
    /// Remap a legacy UAssetAPI FPackageIndex to a Zen FPackageObjectIndex
    /// </summary>
    private static FPackageObjectIndex RemapLegacyPackageIndex(UAssetAPI.UnrealTypes.FPackageIndex legacyIndex, FZenPackage zenPackage)
    {
        if (legacyIndex.Index == 0)
            return FPackageObjectIndex.CreateNull();
        
        if (legacyIndex.IsExport())
            return FPackageObjectIndex.CreateExport((uint)(legacyIndex.Index - 1));
        
        if (legacyIndex.IsImport())
        {
            // For imports, we need to return the FPackageObjectIndex that's stored in the import map
            // The import map already contains the correct FPackageObjectIndex values (script hashes or package imports)
            int importIndex = -legacyIndex.Index - 1;
            if (importIndex >= 0 && importIndex < zenPackage.ImportMap.Count)
            {
                // Return the FPackageObjectIndex stored in the import map
                // This is either a script object hash or a package import reference
                return zenPackage.ImportMap[importIndex];
            }
            // Fallback - should not happen if import map is built correctly
            // Warning logging disabled for parallel performance
            return FPackageObjectIndex.CreateNull();
        }
        
        return FPackageObjectIndex.CreateNull();
    }
    
    /// <summary>
    /// Get the display form of an Import's ObjectName, applying the FName Number suffix.
    /// UE5 FName with Number > 0 renders as "BaseName_{Number-1}" (no zero-padding).
    /// This MUST be applied before hashing/path-building, otherwise JSON files where
    /// a name was stored as base+Number (e.g. retoc's "/Script/Hero" + Number=1023)
    /// will silently produce a different path than JSON files where the same name
    /// was stored with the number baked into the base (e.g. "/Script/Hero_1022" + 0).
    /// </summary>
    private static string GetImportObjectNameWithNumber(Import import)
    {
        string baseName = import.ObjectName?.Value?.Value ?? "None";
        int number = import.ObjectName?.Number ?? 0;
        if (number > 0)
            return baseName + "_" + (number - 1);
        return baseName;
    }

    private static string GetExportObjectNameWithNumber(Export export)
    {
        string baseName = export.ObjectName?.Value?.Value ?? "None";
        int number = export.ObjectName?.Number ?? 0;
        // UE FName with Number > 0 displays as "BaseName_N" where N = Number - 1
        if (number > 0)
            return baseName + "_" + (number - 1);
        return baseName;
    }

    /// <summary>
    /// Build an export's path within its own package: "Outer/Inner" for a nested export, or just
    /// the name for a top-level one. This is the string other packages hash in order to import
    /// the export, so it has to match how BuildImportMap resolves imports.
    ///
    /// Verified against the game's own containers: for 1021001_ChildBP, hashing
    /// "Default__1021001_ChildBP_C/DeathAnimComponent" reproduces the shipped hash
    /// 0xDECDE2461CFEE5D7 exactly, while hashing the bare leaf name yields 0xEC1DB5896CAB5944,
    /// which is what this converter used to emit.
    /// </summary>
    private static string BuildExportPath(UAsset asset, Export export)
    {
        var parts = new List<string>();
        var current = export;

        // Bounded so a malformed outer chain cannot spin here.
        for (int depth = 0; current != null && depth < 64; depth++)
        {
            parts.Insert(0, GetExportObjectNameWithNumber(current));

            var outer = current.OuterIndex;
            // Stop at the package boundary: an outer that is not an export is the package
            // itself (an import) or null, and the path is relative to the package.
            if (outer == null || !outer.IsExport())
                break;

            int outerIndex = outer.Index - 1;
            current = (outerIndex >= 0 && outerIndex < asset.Exports.Count)
                ? asset.Exports[outerIndex]
                : null;
        }

        return string.Join("/", parts);
    }

    /// <summary>
    /// Build the full object path for a script import (e.g., "/Script/Engine/StaticMesh")
    /// </summary>
    private static string BuildScriptObjectPath(UAsset asset, Import import)
    {
        string objectName = GetImportObjectNameWithNumber(import);

        // For script objects, the path is typically: /Script/Package/ClassName
        // If this is a top-level import (outer is null), it's the package itself
        if (import.OuterIndex.Index == 0)
        {
            // Top-level import - this is the package itself (e.g., "/Script/Engine")
            return objectName;
        }

        // Build the path by walking up the outer chain
        var pathParts = new List<string>();
        pathParts.Add(objectName);

        var currentIndex = import.OuterIndex;
        while (currentIndex.Index != 0 && currentIndex.IsImport())
        {
            int outerImportIndex = -currentIndex.Index - 1;
            if (outerImportIndex >= 0 && outerImportIndex < asset.Imports.Count)
            {
                var outerImport = asset.Imports[outerImportIndex];
                string outerName = GetImportObjectNameWithNumber(outerImport);
                pathParts.Insert(0, outerName);
                currentIndex = outerImport.OuterIndex;
            }
            else
            {
                break;
            }
        }

        // The first part should be the package path (e.g., "/Script/Engine")
        // which already starts with /, so just join with /
        return string.Join("/", pathParts);
    }

    /// <summary>
    /// Calculate public export hash using CityHash64 of the export name
    /// Uses GetPublicExportHash which calls hash_helper.exe for correct cityhasher output
    /// </summary>
    private static ulong CalculatePublicExportHash(string exportName)
    {
        // Use the same hash function as GetPublicExportHash for consistency
        // The export name is the path used for hashing (lowercase)
        return GetPublicExportHash(exportName);
    }

    /// <summary>
    /// Find the index of the main asset export (SkeletalMesh, StaticMesh, Texture2D, etc.)
    /// Returns -1 if not found or already at index 0
    /// </summary>
    private static int FindMainAssetExportIndex(UAsset asset)
    {
        // Main asset types that should be at index 0
        var mainAssetTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SkeletalMesh",
            "StaticMesh",
            "Texture2D",
            "Material",
            "MaterialInstance",
            "MaterialInstanceConstant",
            "AnimSequence",
            "AnimMontage",
            "Blueprint",
            "WidgetBlueprint",
            "SoundWave",
            "SoundCue",
            "ParticleSystem",
            "NiagaraSystem",
            "PhysicsAsset",
            "Skeleton"
        };
        
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            
            // Get the class name from ClassIndex
            string? className = null;
            if (export.ClassIndex.IsImport())
            {
                int importIdx = -export.ClassIndex.Index - 1;
                if (importIdx >= 0 && importIdx < asset.Imports.Count)
                {
                    className = asset.Imports[importIdx].ObjectName?.Value?.Value;
                }
            }
            
            if (className != null && mainAssetTypes.Contains(className))
            {
                return i;
            }
        }
        
        return -1; // Not found
    }

    private static void BuildNameMap(UAsset asset, FZenPackage zenPackage, string gamePath)
    {
        // Copy the names export data can reference, and only those.
        //
        // Names cannot be *filtered* here: export data addresses them by index, so removing an
        // entry from the middle would repoint every later reference. Truncating the tail is a
        // different operation and is safe. The legacy name map is laid out as
        // [0, NamesReferencedFromExportDataCount) = referenced by export data, followed by
        // entries that exist purely to name the legacy import table. Zen stores imports as
        // hashes and keeps imported package names in their own region, so those trailing entries
        // have no reader on this side and only inflate the header, pushing every subsequent
        // offset out relative to what the game ships.
        //
        // Safe for edited assets too: UAsset.AddNameReference extends
        // NamesReferencedFromExportDataCount whenever a name is added, so anything a modder
        // introduces falls inside the boundary rather than being cut off.
        var legacyNames = asset.GetNameMapIndexList();
        int nameCount = legacyNames.Count;
        int referencedCount = asset.NamesReferencedFromExportDataCount;
        if (referencedCount > 0 && referencedCount < nameCount)
            nameCount = referencedCount;

        for (int i = 0; i < nameCount; i++)
        {
            string nameStr = legacyNames[i]?.Value ?? "None";
            zenPackage.NameMap.Add(nameStr);
        }
        
        // gamePath is the authoritative /Game/... package path computed by ConvertLegacyToZenInternal
        // from either the file's relative position in the input directory (overrideGamePath)
        // or from asset.FolderName. It always includes the asset name at the end.
        string packageName = gamePath;
        string assetName = Path.GetFileNameWithoutExtension(asset.FilePath ?? "Unknown");
        
        // The Zen summary Name field indexes into the NameMap.
        // CUE4Parse (FModel) uses Summary.Name to compute FPackageId.FromName(Name) and look up
        // the store entry in the container header. If Summary.Name resolves to a SHORT name
        // (e.g. "SK_1044_1044001") instead of the full path ("/Game/.../SK_1044_1044001"),
        // the PackageId won't match the container header's PackageId → no imported packages
        // → blank materials in FModel's mesh preview.
        //
        // Always use the full package path for Summary.Name.
        // If already in the NameMap, reuse it. Otherwise add it.
        int packageNameIndex = zenPackage.NameMap.IndexOf(packageName);
        uint packageNameNumber = 0;
        if (packageNameIndex < 0)
        {
            // Full path not in NameMap — add it
            packageNameIndex = zenPackage.NameMap.Count;
            zenPackage.NameMap.Add(packageName);
        }
        
        zenPackage.PackageName = packageName;
        zenPackage.PackageNameIndex = packageNameIndex;
        zenPackage.PackageNameNumber = packageNameNumber;
        
        // Verbose logging disabled for parallel performance
    }

    private static void SetPackageSummary(UAsset asset, FZenPackage zenPackage)
    {
        // Set package name index and FName number from the name map
        // When the NameMap contains the FName base (e.g. "MI_102993_Trail_13"), Number must be
        // set to suffix+1 (e.g. 302 for _301) to reconstruct the correct package name.
        // When the NameMap contains the full name, Number is 0.
        zenPackage.Summary.Name = new FMappedName((uint)zenPackage.PackageNameIndex, zenPackage.PackageNameNumber);
        
        // Copy package flags from legacy asset
        // PKG_Cooked (0x00000200) + PKG_FilterEditorOnly (0x80000000) + PKG_UnversionedProperties (0x00002000)
        // The PKG_UnversionedProperties flag must match the actual serialization format of the source asset
        uint packageFlags = 0x80000200; // PKG_FilterEditorOnly | PKG_Cooked (base flags)
        
        // Merge any additional flags from the legacy asset
        packageFlags |= (uint)asset.PackageFlags;
        
        // Only set PKG_UnversionedProperties if the source asset uses unversioned serialization
        if (asset.HasUnversionedProperties)
        {
            packageFlags |= 0x00002000; // PKG_UnversionedProperties
        }
        
        zenPackage.Summary.PackageFlags = packageFlags;
        
        // Verbose logging disabled for parallel performance
        
        // Set cooked header size from legacy asset
        zenPackage.Summary.CookedHeaderSize = (uint)asset.Exports.Min(e => e.SerialOffset);
        
        // Verbose logging disabled for parallel performance
    }

    private static void BuildImportMap(UAsset asset, FZenPackage zenPackage)
    {
        // Determine this asset's own package path to prevent self-references
        // FolderName is the full package path (e.g., /Game/Marvel/VFX/.../MI_Environment_13_304)
        string assetName = Path.GetFileNameWithoutExtension(asset.FilePath);
        string folderName = asset.FolderName?.Value ?? "";
        string ownPackagePath = "";
        string normalizedFolder = NormalizePath(folderName);
        if (normalizedFolder.StartsWith("/Game/"))
            ownPackagePath = normalizedFolder.TrimEnd('/');
        else if (!string.IsNullOrEmpty(normalizedFolder))
            ownPackagePath = "/Game/" + normalizedFolder.TrimStart('/').TrimEnd('/');
        
        bool debugMode = Environment.GetEnvironmentVariable("DEBUG") == "1";
        if (debugMode)
            Console.Error.WriteLine($"[BuildImportMap] ownPackagePath={ownPackagePath}, folderName={folderName}, normalizedFolder={normalizedFolder}");
        
        // Build import map from legacy imports
        // For script imports (from /Script/), use pre-computed hashes from game's script objects database
        // For package imports (from other packages), create package import indices
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            // Apply FName Number suffix so retoc-style JSON (base+Number) matches UAT-style
            // JSON (number baked into base) when looking up script objects.
            string objectName = GetImportObjectNameWithNumber(import);

            // Build the full object path to determine the package
            string objectPath = BuildScriptObjectPath(asset, import);
            
            // MaterialTags: replace /Script/MaterialTagPlugin imports with
            // /Script/Engine equivalents. The game doesn't know about MaterialTagPlugin —
            // we can't strip the export (Extras raw binary has FPackageIndex we can't remap)
            // so instead we remap the class to the engine-native base class AssetUserData.
            if (_enableMaterialTags && (objectPath.Contains("/MaterialTagPlugin") || objectPath.Contains("/RivalsMeshMaterialManager")))
            {
                string replacedPath;
                if (objectPath.Contains("Default__"))
                    replacedPath = "/Script/Engine.Default__AssetUserData";
                else if (objectPath.Contains("MaterialTagAssetUserData") || objectPath.Contains("HiddenMaterialsAssetUserData"))
                    replacedPath = "/Script/Engine.AssetUserData";
                else
                    replacedPath = "/Script/Engine"; // package itself

                FPackageObjectIndex replacedImport;
                if (_scriptObjectsDb != null && _scriptObjectsDb.TryGetGlobalIndexByPath(replacedPath, out ulong replacedGlobalIndex))
                {
                    replacedImport = FPackageObjectIndex.CreateFromRaw(replacedGlobalIndex);
                }
                else
                {
                    replacedImport = FPackageObjectIndex.CreateScriptImport(replacedPath);
                }
                
                Console.Error.WriteLine($"[MaterialTags] Remapped import[{i}]: {objectPath} -> {replacedPath}");
                zenPackage.ImportMap.Add(replacedImport);
                continue;
            }

            // Check if this import is from a /Script/ package (based on the object path, not class package)
            if (objectPath.StartsWith("/Script/"))
            {
                // Try to look up the pre-computed hash from the game's script objects database
                // Use the FULL PATH for lookup to avoid ambiguity with objects that have the same name
                FPackageObjectIndex scriptImport;
                if (_scriptObjectsDb != null && _scriptObjectsDb.TryGetGlobalIndexByPath(objectPath, out ulong globalIndex))
                {
                    // Use the pre-computed hash from the game (found by full path)
                    scriptImport = FPackageObjectIndex.CreateFromRaw(globalIndex);
                }
                else if (_scriptObjectsDb != null && _scriptObjectsDb.TryGetGlobalIndex(objectName, out globalIndex))
                {
                    // Fallback to simple name lookup
                    scriptImport = FPackageObjectIndex.CreateFromRaw(globalIndex);
                    // Warning logging disabled for parallel performance
                }
                else
                {
                    // Fallback to generating hash from path
                    scriptImport = FPackageObjectIndex.CreateScriptImport(objectPath);
                    // Warning logging disabled for parallel performance
                }
                zenPackage.ImportMap.Add(scriptImport);
            }
            else
            {
                // For non-script imports:
                // OuterIndex == 0 means this IS a package reference (like /Game/Marvel/VFX/.../SomePackage)
                // OuterIndex < 0 means this is an export FROM a package (the outer points to the package import)
                // 
                // In Zen format, package references themselves are Null - the package is implicit
                // in the FPackageImportReference that points to exports from that package.
                
                if (import.OuterIndex.Index == 0)
                {
                    // Package reference - use Null (package is implicit in Zen format)
                    zenPackage.ImportMap.Add(FPackageObjectIndex.CreateNull());
                }
                else
                {
                    // Regular package import - need FPackageImportReference with package ID and export hash
                    // Get the package path from the import chain
                    string packagePath = GetImportPackagePath(asset, import);
                    string exportPath = GetImportExportPath(asset, import);
                    
                    // Skip self-references: if the import's package path matches our own package, use Null
                    if (debugMode)
                        Console.Error.WriteLine($"[BuildImportMap] Import[{i}]: packagePath={packagePath}, exportPath={exportPath}, objectName={objectName}");
                    
                    if (!string.IsNullOrEmpty(packagePath) && !string.IsNullOrEmpty(ownPackagePath) && 
                        packagePath.Equals(ownPackagePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (debugMode)
                            Console.Error.WriteLine($"[BuildImportMap] SKIPPING self-reference: {packagePath}");
                        zenPackage.ImportMap.Add(FPackageObjectIndex.CreateNull());
                    }
                    else if (!string.IsNullOrEmpty(packagePath) && !string.IsNullOrEmpty(exportPath))
                    {
                        // Calculate package ID from the package path
                        ulong packageId = IoStore.FPackageId.FromName(packagePath).Value;
                        
                        // Calculate public export hash from the export path (lowercase, without package prefix)
                        ulong exportHash = GetPublicExportHash(exportPath.ToLowerInvariant());
                        
                        // Add to imported packages if not already present
                        int packageIndex = zenPackage.ImportedPackages.IndexOf(packageId);
                        if (packageIndex == -1)
                        {
                            packageIndex = zenPackage.ImportedPackages.Count;
                            zenPackage.ImportedPackages.Add(packageId);
                            zenPackage.ImportedPackageNames.Add(packagePath);
                        }
                        
                        // Add to imported public export hashes if not already present
                        int hashIndex = zenPackage.ImportedPublicExportHashes.IndexOf(exportHash);
                        if (hashIndex == -1)
                        {
                            hashIndex = zenPackage.ImportedPublicExportHashes.Count;
                            zenPackage.ImportedPublicExportHashes.Add(exportHash);
                        }
                        
                        // Create FPackageImportReference and convert to FPackageObjectIndex
                        var importRef = FPackageObjectIndex.CreatePackageImport((uint)packageIndex, (uint)hashIndex);
                        zenPackage.ImportMap.Add(importRef);
                        
                        string lowerExportPath = exportPath.ToLowerInvariant();
                        // Verbose logging disabled for parallel performance
                    }
                    else
                    {
                        // Fallback to Null if we can't resolve the package
                        zenPackage.ImportMap.Add(FPackageObjectIndex.CreateNull());
                        // Warning logging disabled for parallel performance
                    }
                }
            }
        }
    }

    private static FMappedName MapNameToZen(FZenPackage zenPackage, string? name, int number = 0)
    {
        string nameStr = name ?? "None";
        int index = zenPackage.NameMap.IndexOf(nameStr);
        if (index == -1)
        {
            index = zenPackage.NameMap.Count;
            zenPackage.NameMap.Add(nameStr);
            // Verbose logging disabled for parallel performance
        }
        return new FMappedName((uint)index, (uint)number);
    }
    
    /// <summary>
    /// Get the package path for an import by traversing the outer chain to find the root package
    /// </summary>
    private static string GetImportPackagePath(UAsset asset, Import import)
    {
        // Traverse up the outer chain to find the package (import with OuterIndex == 0)
        var current = import;
        while (current.OuterIndex.Index != 0)
        {
            int outerIdx = -current.OuterIndex.Index - 1; // Convert to 0-based import index
            if (outerIdx < 0 || outerIdx >= asset.Imports.Count)
                return "";
            current = asset.Imports[outerIdx];
        }
        // The root import's ObjectName is the package path
        // FName Number > 0 means suffix _{Number-1} (matching UE5 FName::ToString, no zero-padding)
        string basePath = current.ObjectName?.Value?.Value ?? "";
        int number = current.ObjectName?.Number ?? 0;
        if (number > 0)
        {
            return basePath + "_" + (number - 1);
        }
        return basePath;
    }
    
    /// <summary>
    /// Get the export path (object name within package) for an import
    /// </summary>
    private static string GetImportExportPath(UAsset asset, Import import)
    {
        // Build the path from the import up to (but not including) the package
        var parts = new List<string>();
        var current = import;
        
        while (current.OuterIndex.Index != 0)
        {
            // Convert FName number to _0, _1, _2 format (matching UE5 FName::ToString)
            string baseName = current.ObjectName?.Value?.Value ?? "";
            int number = current.ObjectName?.Number ?? 0;
            if (number > 0)
            {
                parts.Add(baseName + "_" + (number - 1));
            }
            else
            {
                parts.Add(baseName);
            }
            int outerIdx = -current.OuterIndex.Index - 1;
            if (outerIdx < 0 || outerIdx >= asset.Imports.Count)
                break;
            current = asset.Imports[outerIdx];
        }
        
        // Reverse to get the path from package to object
        // Note: Rust uses path WITHOUT leading slash for hash calculation
        parts.Reverse();
        return string.Join("/", parts);
    }
    
    /// <summary>
    /// Calculate public export hash from export path using CityHash64
    /// Matches Rust: get_public_export_hash (uses cityhasher crate)
    /// Now uses native C# implementation that matches cityhasher exactly.
    /// </summary>
    private static ulong GetPublicExportHash(string exportPath)
    {
        // Use native C# CityHash64 implementation (matches cityhasher 0.1.0)
        return IoStore.CityHash.CityHash64(exportPath.ToLowerInvariant());
    }

    private const ulong FNAME_HASH_ALGORITHM_ID = 0xC1640000;

    /// <summary>
    /// Write a name batch in UE format.
    /// Format: count (u32), if count > 0: string_bytes (u32), hash_algo_id (u64), hashes (u64[]), headers (i16 BE[]), strings
    /// 
    /// Header i16 (big-endian):
    ///   positive = ASCII byte length
    ///   negative = -(UTF-16LE code unit count) — string data is UTF-16LE
    /// 
    /// Hash: CityHash64 of lowercase bytes (ASCII for ASCII names, UTF-16LE for non-ASCII names)
    /// </summary>
    private static void WriteNameBatch(BinaryWriter writer, List<string> names)
    {
        writer.Write((uint)names.Count);
        if (names.Count == 0)
            return;

        // Pre-compute encoded bytes and whether each name is ASCII
        var encodedNames = new List<(byte[] bytes, bool isAscii)>(names.Count);
        uint totalStringBytes = 0;
        foreach (var name in names)
        {
            bool ascii = IsAscii(name);
            byte[] bytes = ascii
                ? Encoding.ASCII.GetBytes(name)
                : Encoding.Unicode.GetBytes(name); // UTF-16LE
            encodedNames.Add((bytes, ascii));
            totalStringBytes += (uint)bytes.Length;
        }
        writer.Write(totalStringBytes);

        // Hash algorithm ID
        writer.Write(FNAME_HASH_ALGORITHM_ID);

        // Write hashes (CityHash64 of lowercase encoded bytes)
        for (int i = 0; i < names.Count; i++)
        {
            string lower = names[i].ToLowerInvariant();
            byte[] hashBytes = encodedNames[i].isAscii
                ? Encoding.ASCII.GetBytes(lower)
                : Encoding.Unicode.GetBytes(lower); // UTF-16LE
            ulong hash = IoStore.CityHash.CityHash64(hashBytes, 0, hashBytes.Length);
            writer.Write(hash);
        }

        // Write headers (i16 big-endian)
        for (int i = 0; i < names.Count; i++)
        {
            short headerVal;
            if (encodedNames[i].isAscii)
            {
                // Positive = ASCII byte length
                headerVal = (short)encodedNames[i].bytes.Length;
            }
            else
            {
                // Wide string: headerVal = short.MinValue + charCount
                // Reader decodes: charCount = Math.Abs(short.MinValue - headerVal)
                headerVal = (short)(short.MinValue + names[i].Length);
            }
            // Write as big-endian
            writer.Write((byte)(headerVal >> 8));
            writer.Write((byte)(headerVal & 0xFF));
        }

        // Write string data - consecutive, encoding per name
        for (int i = 0; i < names.Count; i++)
        {
            writer.Write(encodedNames[i].bytes);
        }
    }

    private static bool IsAscii(string s)
    {
        foreach (char c in s)
            if (c > 127) return false;
        return true;
    }
    
    /// <summary>
    /// Normalize a path by resolving .. segments
    /// Example: /Game/Marvel/../../../Marvel/Content/Marvel/Characters -> /Marvel/Content/Marvel/Characters
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.Contains(".."))
            return path;
        
        var parts = path.Split('/');
        var stack = new List<string>();
        
        foreach (var part in parts)
        {
            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
            }
            else if (!string.IsNullOrEmpty(part) && part != ".")
            {
                stack.Add(part);
            }
        }
        
        string result = string.Join("/", stack);
        if (path.StartsWith("/"))
            result = "/" + result;
        return result;
    }

    /// <summary>
    /// Build export bundles - groups of exports that are loaded together
    /// For UE5.3+ (NoExportInfo), all exports go into a single bundle
    /// The order of entries is determined by export dependencies (topological sort)
    /// </summary>
    private static void BuildExportBundles(UAsset asset, FZenPackage zenPackage)
    {
        // For UE5.3+ NoExportInfo version, we create a single export bundle containing all exports
        // Each export gets two entries: Create and Serialize
        // The order must respect dependencies - exports that depend on others must come after

        // The bundle is a single sequence over 2N nodes: a Create and a Serialize node for each
        // export. UE's four preload dependency lists constrain the two commands independently,
        // which is why a package's Create order and Serialize order genuinely differ (a class
        // must finish serializing before its CDO can be created). Sorting the N exports once and
        // emitting that order twice cannot express that, so build the real graph.
        int exportCount = asset.Exports.Count;
        int CreateNode(int i) => i;
        int SerializeNode(int i) => exportCount + i;

        var prereq = new List<int>[exportCount * 2];
        for (int i = 0; i < prereq.Length; i++) prereq[i] = new List<int>();

        void AddEdge(int node, UAssetAPI.UnrealTypes.FPackageIndex dep, bool depIsSerialize)
        {
            if (dep == null || !dep.IsExport()) return;
            int d = dep.Index - 1;
            if (d < 0 || d >= exportCount) return;
            prereq[node].Add(depIsSerialize ? SerializeNode(d) : CreateNode(d));
        }

        for (int i = 0; i < exportCount; i++)
        {
            var export = asset.Exports[i];

            // An object cannot be created before its outer exists.
            if (export.OuterIndex != null && export.OuterIndex.Index > 0)
                prereq[CreateNode(i)].Add(CreateNode(export.OuterIndex.Index - 1));

            if (export.CreateBeforeCreateDependencies != null)
                foreach (var d in export.CreateBeforeCreateDependencies) AddEdge(CreateNode(i), d, false);
            if (export.SerializationBeforeCreateDependencies != null)
                foreach (var d in export.SerializationBeforeCreateDependencies) AddEdge(CreateNode(i), d, true);
            if (export.CreateBeforeSerializationDependencies != null)
                foreach (var d in export.CreateBeforeSerializationDependencies) AddEdge(SerializeNode(i), d, false);
            if (export.SerializationBeforeSerializationDependencies != null)
                foreach (var d in export.SerializationBeforeSerializationDependencies) AddEdge(SerializeNode(i), d, true);

            // An export must exist before it can be serialized.
            prereq[SerializeNode(i)].Add(CreateNode(i));
        }

        var bundleOrder = KahnSort(prereq);

        // Create a single bundle header
        var bundleHeader = new FExportBundleHeader
        {
            SerialOffset = 0,
            FirstEntryIndex = 0,
            EntryCount = (uint)(zenPackage.ExportMap.Count * 2) // Create + Serialize for each export
        };
        zenPackage.ExportBundleHeaders.Add(bundleHeader);

        foreach (int node in bundleOrder)
        {
            if (node < exportCount)
                zenPackage.ExportBundleEntries.Add(new FExportBundleEntry((uint)node, EExportCommandType.Create));
            else
                zenPackage.ExportBundleEntries.Add(new FExportBundleEntry((uint)(node - exportCount), EExportCommandType.Serialize));
        }
    }

    /// <summary>
    /// Kahn topological sort over the export bundle graph.
    ///
    /// Deliberately iterative and cycle-aware. The previous DFS marked nodes visited on entry, so
    /// on a dependency cycle it emitted a node before its own prerequisites — blueprints always
    /// cycle (class -> InheritableComponentHandler -> class), which produced bundles that told the
    /// engine to create an object before its outer existed.
    ///
    /// Any node still constrained when the queue empties is part of a cycle; those are appended in
    /// index order so the bundle stays complete rather than silently losing exports.
    /// </summary>
    private static List<int> KahnSort(List<int>[] prereq)
    {
        int count = prereq.Length;
        var indegree = new int[count];
        var dependents = new List<int>[count];
        for (int i = 0; i < count; i++) dependents[i] = new List<int>();

        for (int node = 0; node < count; node++)
        {
            foreach (int p in prereq[node])
            {
                dependents[p].Add(node);
                indegree[node]++;
            }
        }

        // Sorted set keeps the result deterministic: same asset in, same bundle out.
        var ready = new SortedSet<int>();
        for (int node = 0; node < count; node++)
            if (indegree[node] == 0) ready.Add(node);

        var order = new List<int>(count);
        while (ready.Count > 0)
        {
            int node = ready.Min;
            ready.Remove(node);
            order.Add(node);

            foreach (int m in dependents[node])
                if (--indegree[m] == 0) ready.Add(m);
        }

        if (order.Count != count)
        {
            var emitted = new HashSet<int>(order);
            for (int node = 0; node < count; node++)
                if (!emitted.Contains(node)) order.Add(node);
        }

        return order;
    }

    /// <summary>
    /// Build dependency bundles - defines load order dependencies between exports
    /// For UE5.3+ (NoExportInfo), this replaces the legacy graph data
    /// </summary>
    private static void BuildDependencyBundles(UAsset asset, FZenPackage zenPackage)
    {
        bool debugMode = Environment.GetEnvironmentVariable("DEBUG") == "1";
        
        // For each export, create a dependency bundle header
        // Preserve actual preload dependencies from the legacy asset
        
        int currentEntryIndex = 0;
        
        for (int i = 0; i < zenPackage.ExportMap.Count; i++)
        {
            var export = asset.Exports[i];
            
            // Collect dependencies from all 4 preload dependency arrays
            var createBeforeCreate = new List<FPackageIndex>();
            var serializeBeforeCreate = new List<FPackageIndex>();
            var createBeforeSerialize = new List<FPackageIndex>();
            var serializeBeforeSerialize = new List<FPackageIndex>();
            
            // Preserve actual preload dependencies from legacy asset
            if (export.CreateBeforeCreateDependencies != null)
            {
                foreach (var dep in export.CreateBeforeCreateDependencies)
                    createBeforeCreate.Add(new FPackageIndex(dep.Index));
            }
            if (export.SerializationBeforeCreateDependencies != null)
            {
                foreach (var dep in export.SerializationBeforeCreateDependencies)
                    serializeBeforeCreate.Add(new FPackageIndex(dep.Index));
            }
            if (export.CreateBeforeSerializationDependencies != null)
            {
                foreach (var dep in export.CreateBeforeSerializationDependencies)
                    createBeforeSerialize.Add(new FPackageIndex(dep.Index));
            }
            if (export.SerializationBeforeSerializationDependencies != null)
            {
                foreach (var dep in export.SerializationBeforeSerializationDependencies)
                    serializeBeforeSerialize.Add(new FPackageIndex(dep.Index));
            }
            
            if (debugMode)
            {
                Console.Error.WriteLine($"[BuildDepBundles] Export[{i}]: CBC={createBeforeCreate.Count}, SBC={serializeBeforeCreate.Count}, CBS={createBeforeSerialize.Count}, SBS={serializeBeforeSerialize.Count}");
            }
            
            // Create dependency header for this export
            var depHeader = new FDependencyBundleHeader
            {
                FirstEntryIndex = currentEntryIndex,
                CreateBeforeCreateDependencies = (uint)createBeforeCreate.Count,
                SerializeBeforeCreateDependencies = (uint)serializeBeforeCreate.Count,
                CreateBeforeSerializeDependencies = (uint)createBeforeSerialize.Count,
                SerializeBeforeSerializeDependencies = (uint)serializeBeforeSerialize.Count
            };
            
            // Add dependency entries in order: CBC, SBC, CBS, SBS
            foreach (var dep in createBeforeCreate)
            {
                zenPackage.DependencyBundleEntries.Add(new FDependencyBundleEntry(dep));
                currentEntryIndex++;
            }
            foreach (var dep in serializeBeforeCreate)
            {
                zenPackage.DependencyBundleEntries.Add(new FDependencyBundleEntry(dep));
                currentEntryIndex++;
            }
            foreach (var dep in createBeforeSerialize)
            {
                zenPackage.DependencyBundleEntries.Add(new FDependencyBundleEntry(dep));
                currentEntryIndex++;
            }
            foreach (var dep in serializeBeforeSerialize)
            {
                zenPackage.DependencyBundleEntries.Add(new FDependencyBundleEntry(dep));
                currentEntryIndex++;
            }
            
            zenPackage.DependencyBundleHeaders.Add(depHeader);
        }
    }

    private static void WriteZenPackage(
        UAsset asset,
        BinaryWriter writer,
        FZenPackage zenPackage,
        byte[] uexpData,
        long headerSize)
    {
        var containerVersion = zenPackage.ContainerVersion;
        
        // Write summary (placeholder, will update later)
        long summaryOffset = writer.BaseStream.Position;
        zenPackage.Summary.Write(writer, containerVersion);
        
        // Write name map using standard UE name batch format
        // (ASCII for ASCII-only names, UTF-16LE for non-ASCII names)
        int nameMapNamesOffset = (int)writer.BaseStream.Position;
        WriteNameBatch(writer, zenPackage.NameMap);
        int nameMapNamesSize = (int)writer.BaseStream.Position - nameMapNamesOffset;
        
        // For UE5.3+ format, hashes are included in the name batch, so no separate hash section
        int nameMapHashesOffset = (int)writer.BaseStream.Position;

        // Write bulk data map entries from legacy asset's DataResources
        // UE5.3+ expects this field after the name map for packages with DataResources support
        // Each FBulkDataMapEntry is 32 bytes: serial_offset(8) + duplicate_serial_offset(8) + serial_size(8) + flags(4) + pad(4)
        // 
        // IMPORTANT: If we have a .ubulk file, we need to validate/fix the offsets to match the actual file
        long ubulkSize = 0;
        string ubulkPath = Path.ChangeExtension(asset.FilePath, ".ubulk");
        if (File.Exists(ubulkPath))
        {
            ubulkSize = new FileInfo(ubulkPath).Length;
        }
        
        if (asset.DataResources != null && asset.DataResources.Count > 0 && ubulkSize > 0)
        {
            // Validate that bulk data map entries fit within the .ubulk file
            // If they don't, create a single entry covering the entire .ubulk
            // Only resources that actually live in the .ubulk file should be bounds-checked against it.
            // Optional (.uptnl, OptionalPayload 0x800) and inline (.uexp, ForceInlinePayload 0x40) resources
            // have offsets in OTHER files, so checking them against ubulkSize would falsely fail.
            bool entriesValid = true;
            foreach (var resource in asset.DataResources)
            {
                bool inUbulk = (resource.LegacyBulkDataFlags & 0x100) != 0   // PayloadInSeperateFile
                               && (resource.LegacyBulkDataFlags & 0x800) == 0 // not OptionalPayload (.uptnl)
                               && (resource.LegacyBulkDataFlags & 0x40) == 0; // not ForceInlinePayload (.uexp)
                if (inUbulk && resource.SerialOffset + resource.SerialSize > ubulkSize)
                {
                    entriesValid = false;
                    break;
                }
            }
            
            if (entriesValid)
            {
                // Use existing entries
                long bulkDataMapSize = asset.DataResources.Count * 32;
                writer.Write(bulkDataMapSize);
                
                // Verbose logging disabled for parallel performance
                int idx = 0;
                foreach (var resource in asset.DataResources)
                {
                    writer.Write(resource.SerialOffset);
                    writer.Write(resource.DuplicateSerialOffset);
                    writer.Write(resource.SerialSize);
                    writer.Write((uint)resource.LegacyBulkDataFlags);
                    writer.Write((uint)0); // padding
                    idx++;
                }
            }
            else
            {
                // Entries don't match .ubulk size - create single entry for entire file
                // Verbose logging disabled for parallel performance
                writer.Write((long)32); // 1 entry * 32 bytes
                writer.Write((long)0); // SerialOffset = 0
                writer.Write((long)-1); // DuplicateSerialOffset = -1 (none)
                writer.Write(ubulkSize); // SerialSize = entire file
                writer.Write((uint)0x00010501); // Flags: PayloadAtEndOfFile | PayloadInSeperateFile | SingleUse
                writer.Write((uint)0); // padding
                // Verbose logging disabled for parallel performance
            }
        }
        else if (asset.DataResources != null && asset.DataResources.Count > 0)
        {
            // No .ubulk file but we have DataResources (inline texture data).
            // Write offsets as-is from legacy DataResources (same as retoc).
            // SerialOffset is relative to export data start, which is the same
            // coordinate system used by both legacy and zen formats.
            // The game engine reads inline bulk data sequentially from the export
            // stream and does NOT use BulkDataMap.SerialOffset to seek.
            long bulkDataMapSize = asset.DataResources.Count * 32;
            writer.Write(bulkDataMapSize);
            
            foreach (var resource in asset.DataResources)
            {
                writer.Write(resource.SerialOffset);
                writer.Write(resource.DuplicateSerialOffset);
                writer.Write(resource.SerialSize);
                writer.Write((uint)resource.LegacyBulkDataFlags);
                writer.Write((uint)0); // padding
            }
        }
        else
        {
            writer.Write((long)0);  // bulk_data_map_size = 0
        }

        // For UE5.0+, write imported public export hashes
        // This section contains hashes of public exports from imported packages
        int importedPublicExportHashesOffset = (int)writer.BaseStream.Position;
        foreach (var hash in zenPackage.ImportedPublicExportHashes)
        {
            writer.Write(hash);
        }
        // Verbose logging disabled for parallel performance

        // Write import map
        int importMapOffset = (int)writer.BaseStream.Position;
        foreach (var import in zenPackage.ImportMap)
        {
            import.Write(writer);
        }

        // Write export map
        int exportMapOffset = (int)writer.BaseStream.Position;
        foreach (var export in zenPackage.ExportMap)
        {
            export.Write(writer);
        }

        // Write export bundle entries
        int exportBundleEntriesOffset = (int)writer.BaseStream.Position;
        foreach (var entry in zenPackage.ExportBundleEntries)
        {
            entry.Write(writer);
        }

        // For UE5.3+ NoExportInfo, write dependency bundle headers and entries
        int dependencyBundleHeadersOffset = -1;
        int dependencyBundleEntriesOffset = -1;
        int importedPackageNamesOffset = -1;
        
        if (containerVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            dependencyBundleHeadersOffset = (int)writer.BaseStream.Position;
            foreach (var header in zenPackage.DependencyBundleHeaders)
            {
                header.Write(writer, containerVersion);
            }
            
            dependencyBundleEntriesOffset = (int)writer.BaseStream.Position;
            foreach (var entry in zenPackage.DependencyBundleEntries)
            {
                entry.Write(writer);
            }
            
            // Write imported package names in FZenPackageImportedPackageNamesContainer format
            // Format: name_batch + imported_package_name_numbers (i32[])
            importedPackageNamesOffset = (int)writer.BaseStream.Position;
            WriteNameBatch(writer, zenPackage.ImportedPackageNames);
            
            // Write imported_package_name_numbers (i32 array, one per package name)
            // These are the numeric suffixes from names like "Package_0" -> 0
            // For most package names without numeric suffix, this is 0
            foreach (var _ in zenPackage.ImportedPackageNames)
            {
                writer.Write((int)0); // No numeric suffix for standard package names
            }
        }

        // Record header size before writing export data
        int zenHeaderSize = (int)writer.BaseStream.Position;

        // CookedHeaderSize = the legacy .uasset file size
        // This is what retoc uses and what the engine expects for serial offset calculations
        int cookedHeaderSize = (int)new FileInfo(asset.FilePath).Length;

        // Write the full .uexp data as export data (do NOT strip trailing PACKAGE_FILE_TAG)
        // retoc preserves the complete .uexp contents including the 4-byte tag
        int exportDataLength = uexpData.Length;
        writer.Write(uexpData, 0, exportDataLength);

        // Update summary with calculated offsets
        zenPackage.Summary.HeaderSize = (uint)zenHeaderSize;
        zenPackage.Summary.CookedHeaderSize = (uint)cookedHeaderSize;
        zenPackage.Summary.ImportedPublicExportHashesOffset = importedPublicExportHashesOffset;
        zenPackage.Summary.ImportMapOffset = importMapOffset;
        zenPackage.Summary.ExportMapOffset = exportMapOffset;
        zenPackage.Summary.ExportBundleEntriesOffset = exportBundleEntriesOffset;
        
        if (containerVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            zenPackage.Summary.DependencyBundleHeadersOffset = dependencyBundleHeadersOffset;
            zenPackage.Summary.DependencyBundleEntriesOffset = dependencyBundleEntriesOffset;
            zenPackage.Summary.ImportedPackageNamesOffset = importedPackageNamesOffset;
        }

        // Go back and write updated summary
        long endPosition = writer.BaseStream.Position;
        writer.BaseStream.Seek(summaryOffset, SeekOrigin.Begin);
        zenPackage.Summary.Write(writer, containerVersion);
        writer.BaseStream.Seek(endPosition, SeekOrigin.Begin);
        
        // Verbose logging disabled for parallel performance
    }

    private static UAsset LoadAsset(string filePath, bool skipParsing = true)
    {
        // No usmap needed for Zen conversion:
        // - skipParsing: true → exports read as raw bytes (textures, materials, blueprints, StaticMesh)
        // - skipParsing: false → SkeletalMesh material tag injection uses binary Extras parsing,
        //   not unversioned property deserialization, so usmap is not required.
        var flags = skipParsing 
            ? (CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading)
            : CustomSerializationFlags.None;
        var asset = new UAsset(filePath, EngineVersion.VER_UE5_3, null, flags);
        asset.UseSeparateBulkDataFiles = true;
        return asset;
    }
    
    /// <summary>
    /// [EXPERIMENTAL] Strip MaterialTagAssetUserData export from the legacy asset.
    /// The game doesn't know about /Script/MaterialTagPlugin — leaving this export in
    /// causes an Access Violation at runtime. After tags have been read and injected into
    /// FSkeletalMaterial, the UserData export is no longer needed.
    /// 
    /// This method:
    /// 1. Finds and removes the MaterialTagAssetUserData export from asset.Exports
    /// 2. Excises its bytes from uexpData
    /// 3. Remaps all FPackageIndex export references on remaining exports
    /// 4. Removes orphaned imports (MaterialTagPlugin class/CDO/package)
    /// </summary>
    /// <returns>Updated uexpData with the export's bytes removed, or original if nothing stripped</returns>
    private static byte[] StripMaterialTagExport(UAsset asset, byte[] uexpData, long headerSize)
    {
        // Find the MaterialTagAssetUserData export
        int removeIdx = -1;
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            string? className = null;
            try { className = asset.Exports[i].GetExportClassType()?.Value?.Value; } catch { }
            if (className != null && className.Contains("MaterialTagAssetUserData", StringComparison.OrdinalIgnoreCase))
            {
                removeIdx = i;
                break;
            }
        }

        if (removeIdx < 0)
            return uexpData; // Nothing to strip

        var removedExport = asset.Exports[removeIdx];
        long removedOffset = removedExport.SerialOffset - headerSize; // offset within uexpData
        long removedSize = removedExport.SerialSize;

        Console.Error.WriteLine($"[MaterialTags] Stripping MaterialTagAssetUserData export (index {removeIdx}, offset {removedOffset}, size {removedSize})");

        // 1. Excise the export's bytes from uexpData
        byte[] newUexpData = new byte[uexpData.Length - removedSize];
        Array.Copy(uexpData, 0, newUexpData, 0, (int)removedOffset);
        long afterRemoved = removedOffset + removedSize;
        if (afterRemoved < uexpData.Length)
        {
            Array.Copy(uexpData, (int)afterRemoved, newUexpData, (int)removedOffset, (int)(uexpData.Length - afterRemoved));
        }

        // 2. Collect import indices used by the removed export (class, template, etc.)
        //    These may be orphaned after removal (MaterialTagPlugin class/CDO/package)
        var removedImportIndices = new HashSet<int>();
        void CollectImportIndex(UAssetAPI.UnrealTypes.FPackageIndex idx)
        {
            if (idx != null && idx.IsImport())
            {
                int importIdx = -idx.Index - 1;
                removedImportIndices.Add(importIdx);
            }
        }
        CollectImportIndex(removedExport.ClassIndex);
        CollectImportIndex(removedExport.TemplateIndex);
        CollectImportIndex(removedExport.SuperIndex);

        // Also collect the outer package import for MaterialTagPlugin classes
        // Walk up the import chain to find /Script/MaterialTagPlugin package
        foreach (int impIdx in removedImportIndices.ToList())
        {
            if (impIdx >= 0 && impIdx < asset.Imports.Count)
            {
                var imp = asset.Imports[impIdx];
                if (imp.OuterIndex != null && imp.OuterIndex.IsImport())
                {
                    int outerIdx = -imp.OuterIndex.Index - 1;
                    removedImportIndices.Add(outerIdx);
                }
            }
        }

        // Check if any remaining export still references these imports — if so, don't remove them
        var usedImportIndices = new HashSet<int>();
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            if (i == removeIdx) continue;
            var exp = asset.Exports[i];
            void MarkUsed(UAssetAPI.UnrealTypes.FPackageIndex idx)
            {
                if (idx != null && idx.IsImport())
                    usedImportIndices.Add(-idx.Index - 1);
            }
            MarkUsed(exp.ClassIndex);
            MarkUsed(exp.TemplateIndex);
            MarkUsed(exp.SuperIndex);
            MarkUsed(exp.OuterIndex);
        }
        // Only remove imports that are truly orphaned
        removedImportIndices.ExceptWith(usedImportIndices);

        // 3. Remove the export from asset.Exports
        asset.Exports.RemoveAt(removeIdx);

        // 4. Remap FPackageIndex export references on all remaining exports
        // Export FPackageIndex: Index = arrayIndex + 1 (1-based)
        // Removed export had Index = removeIdx + 1
        int removedPkgIndex = removeIdx + 1; // 1-based FPackageIndex for the removed export

        void RemapIndex(ref UAssetAPI.UnrealTypes.FPackageIndex idx)
        {
            if (idx == null || idx.Index == 0) return;
            if (idx.IsExport())
            {
                if (idx.Index == removedPkgIndex)
                {
                    // Points to removed export — null it out
                    idx = new UAssetAPI.UnrealTypes.FPackageIndex(0);
                }
                else if (idx.Index > removedPkgIndex)
                {
                    // Shift down by 1
                    idx = new UAssetAPI.UnrealTypes.FPackageIndex(idx.Index - 1);
                }
            }
            // Import indices are unaffected (we don't actually remove imports from the array
            // because that would require remapping all import indices everywhere including
            // in serialized binary data — too risky. Orphaned imports are harmless.)
        }

        foreach (var exp in asset.Exports)
        {
            RemapIndex(ref exp.OuterIndex);
            RemapIndex(ref exp.ClassIndex);
            RemapIndex(ref exp.SuperIndex);
            RemapIndex(ref exp.TemplateIndex);

            // Remap dependency arrays
            void RemapDepArray(List<UAssetAPI.UnrealTypes.FPackageIndex>? deps)
            {
                if (deps == null) return;
                for (int d = deps.Count - 1; d >= 0; d--)
                {
                    var dep = deps[d];
                    if (dep.IsExport())
                    {
                        if (dep.Index == removedPkgIndex)
                        {
                            deps.RemoveAt(d); // Remove dependency on stripped export
                        }
                        else if (dep.Index > removedPkgIndex)
                        {
                            deps[d] = new UAssetAPI.UnrealTypes.FPackageIndex(dep.Index - 1);
                        }
                    }
                }
            }
            RemapDepArray(exp.CreateBeforeCreateDependencies);
            RemapDepArray(exp.SerializationBeforeCreateDependencies);
            RemapDepArray(exp.CreateBeforeSerializationDependencies);
            RemapDepArray(exp.SerializationBeforeSerializationDependencies);

            // Fix SerialOffset for exports that came after the removed one
            if (exp.SerialOffset > removedExport.SerialOffset)
            {
                exp.SerialOffset -= removedSize;
            }
        }

        Console.Error.WriteLine($"[MaterialTags] Stripped export: {asset.Exports.Count} exports remain, uexp {uexpData.Length} -> {newUexpData.Length} bytes");
        return newUexpData;
    }

    /// <summary>
    /// Re-serialize the asset's export data using UAssetAPI's proper serialization.
    /// This ensures types like StringTable get proper FGameplayTagContainer padding automatically.
    /// Returns just the export data portion (equivalent to .uexp content) and updates asset's SerialSize values.
    /// </summary>
    private static byte[]? ReserializeExportData(UAsset asset)
    {
        try
        {
            // Store original offsets before re-serialization
            long originalExportStart = asset.Exports.Min(e => e.SerialOffset);
            
            // Use UAssetAPI's WriteData to get properly serialized data
            // This also updates the asset's SerialOffset and SerialSize values
            using var fullStream = asset.WriteData();
            
            if (fullStream == null || fullStream.Length == 0)
                return null;
            
            // After WriteData, the asset's SerialOffset values are updated
            // Find where export data starts in the new serialization
            long newExportStart = asset.Exports.Min(e => e.SerialOffset);
            
            // Export data goes from exportStart to end of stream
            long exportDataLength = fullStream.Length - newExportStart;
            
            if (exportDataLength <= 0)
                return null;
            
            // Extract just the export data portion
            byte[] exportData = new byte[exportDataLength];
            fullStream.Seek(newExportStart, SeekOrigin.Begin);
            fullStream.Read(exportData, 0, (int)exportDataLength);
            
            Console.Error.WriteLine($"[ZenConverter] Re-serialized export data: {exportDataLength} bytes (from offset {newExportStart})");
            return exportData;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ZenConverter] Failed to re-serialize export data: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Re-serialize all exports in-place, preserving the asset's current name map.
    /// This converts property format (e.g. versioned → unversioned) while keeping
    /// binary Extras (FStaticMeshRenderData, etc.) unchanged.
    /// </summary>
    private static byte[]? ReserializeExportsInPlace(UAsset asset, long headerSize)
    {
        try
        {
            using var newStream = new MemoryStream();

            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var export = asset.Exports[i];
                long exportStart = newStream.Position;

                using var exportMs = new MemoryStream();
                using var exportWriter = new AssetBinaryWriter(exportMs, asset);
                export.Write(exportWriter);
                exportWriter.Write(export.Extras);

                byte[] newExportData = exportMs.ToArray();
                newStream.Write(newExportData, 0, newExportData.Length);

                export.SerialOffset = headerSize + exportStart;
                export.SerialSize = newExportData.Length;
            }

            // Append trailing PACKAGE_FILE_TAG (0x9E2A83C1)
            using var tagWriter = new BinaryWriter(newStream);
            tagWriter.Write(0xC1832A9Eu);

            byte[] result = newStream.ToArray();
            Console.Error.WriteLine($"[ZenConverter] Re-serialized exports in-place: {result.Length} bytes (from offset {headerSize})");
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ZenConverter] ERROR: ReserializeExportsInPlace failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Patch StaticMesh ScreenSize entries from standard UE5 format (8 bytes each, 64 total)
    /// to Marvel Rivals format (16 bytes each, 128 total).
    /// 
    /// Standard UE5:     8 entries × (bCooked int32 + Default float) = 64 bytes
    /// Marvel Rivals:    8 entries × (bCooked int32 + Default float + PerPlatformCount int32 + PlatformValue float) = 128 bytes
    /// 
    /// Detection strategy (validated across 182+ game meshes):
    /// 1. Find material array near end (count + N × 36-byte FStaticMaterial)
    /// 2. SpeedTree = 4 bytes before materials (always 0)
    /// 3. ScreenSize = 64 or 128 bytes before SpeedTree
    /// 4. Verify bLODsShareStaticLighting (0 or 1) before ScreenSize
    /// </summary>
    private static byte[]? PatchStaticMeshScreenSize(byte[] uexpData)
    {
        const int SCREEN_SIZE_ENTRIES = 8;
        const int STANDARD_ENTRY_SIZE = 8;   // bCooked(4) + Default(4)
        const int MARVEL_ENTRY_SIZE = 16;     // bCooked(4) + Default(4) + PerPlatformCount(4) + PlatformValue(4)
        const int STANDARD_SCREEN_SIZE = SCREEN_SIZE_ENTRIES * STANDARD_ENTRY_SIZE;   // 64
        const int MARVEL_SCREEN_SIZE = SCREEN_SIZE_ENTRIES * MARVEL_ENTRY_SIZE;        // 128
        const int EXPANSION = MARVEL_SCREEN_SIZE - STANDARD_SCREEN_SIZE;              // 64
        const int MATERIAL_STRUCT_SIZE = 36;
        const int MAX_MATERIAL_COUNT = 50;
        // Sentinel value for unused ScreenSize PlatformValue (0x55000000)
        const uint SENTINEL_VALUE = 0x55000000;

        int dataLen = uexpData.Length;

        // Step 1: Find material array near end of file
        // Materials can reference imports (negative) OR exports/null (0 or positive)
        int matCountOffset = -1;
        int matCount = 0;

        for (int i = dataLen - 40; i > Math.Max(0, dataLen - 2000); i--)
        {
            int mc = BitConverter.ToInt32(uexpData, i);
            if (mc < 1 || mc > MAX_MATERIAL_COUNT)
                continue;

            int expectedEnd = i + 4 + (mc * MATERIAL_STRUCT_SIZE);
            if (Math.Abs(expectedEnd - dataLen) > 4)
                continue;

            // Verify first material has a reasonable FPackageIndex
            int firstPkg = BitConverter.ToInt32(uexpData, i + 4);
            // Allow negative (imports), zero (null/WorldGridMaterial), or small positive (exports)
            if (firstPkg < -1000 || firstPkg > 1000)
                continue;

            // Verify FName index after FPackageIndex looks reasonable
            int fnameIdx = BitConverter.ToInt32(uexpData, i + 8);
            if (fnameIdx < 0 || fnameIdx > 10000)
                continue;

            matCountOffset = i;
            matCount = mc;
            break;
        }

        if (matCountOffset < 0)
        {
            Console.Error.WriteLine("[ZenConverter] StaticMesh: Could not locate material array");
            return null;
        }

        // Step 2: SpeedTree is 4 bytes before material count
        int speedTreeOffset = matCountOffset - 4;
        if (speedTreeOffset < 0)
            return null;

        int speedTree = BitConverter.ToInt32(uexpData, speedTreeOffset);
        // SpeedTree should be 0 (false) or 1 (true)
        if (speedTree != 0 && speedTree != 1)
        {
            Console.Error.WriteLine($"[ZenConverter] StaticMesh: Unexpected SpeedTree value {speedTree} at 0x{speedTreeOffset:X}");
            return null;
        }

        // Step 3: Check if ScreenSize is already Marvel Rivals format (128 bytes) or standard (64 bytes)
        // Try Marvel Rivals format first (already expanded = no-op)
        int marvelSSStart = speedTreeOffset - MARVEL_SCREEN_SIZE;
        if (marvelSSStart >= 4)
        {
            bool isMarvelFormat = true;
            for (int e = 0; e < SCREEN_SIZE_ENTRIES && isMarvelFormat; e++)
            {
                int off = marvelSSStart + e * MARVEL_ENTRY_SIZE;
                int bCooked = BitConverter.ToInt32(uexpData, off);
                int ppCount = BitConverter.ToInt32(uexpData, off + 8);
                if (bCooked != 1 || ppCount != 1)
                    isMarvelFormat = false;
            }
            if (isMarvelFormat)
            {
                int bLod = BitConverter.ToInt32(uexpData, marvelSSStart - 4);
                if (bLod == 0 || bLod == 1)
                {
                    // Already in Marvel Rivals format — no expansion needed
                    return null;
                }
            }
        }

        // Try standard UE5 format (64 bytes)
        int stdSSStart = speedTreeOffset - STANDARD_SCREEN_SIZE;
        if (stdSSStart < 4)
        {
            Console.Error.WriteLine("[ZenConverter] StaticMesh: Not enough data before SpeedTree for ScreenSize");
            return null;
        }

        // Verify standard format: all 8 entries should have bCooked=1
        bool isStandardFormat = true;
        for (int e = 0; e < SCREEN_SIZE_ENTRIES; e++)
        {
            int off = stdSSStart + e * STANDARD_ENTRY_SIZE;
            int bCooked = BitConverter.ToInt32(uexpData, off);
            if (bCooked != 1)
            {
                isStandardFormat = false;
                break;
            }
        }

        if (!isStandardFormat)
        {
            Console.Error.WriteLine("[ZenConverter] StaticMesh: ScreenSize does not match standard UE5 format");
            return null;
        }

        // Verify bLODsShareStaticLighting before ScreenSize
        int bLodOffset = stdSSStart - 4;
        int bLodVal = BitConverter.ToInt32(uexpData, bLodOffset);
        if (bLodVal != 0 && bLodVal != 1)
        {
            Console.Error.WriteLine($"[ZenConverter] StaticMesh: bLODsShareStaticLighting={bLodVal} (expected 0 or 1)");
            return null;
        }

        // Step 4: Expand ScreenSize from 64 to 128 bytes
        byte[] newData = new byte[dataLen + EXPANSION];

        // Copy everything before ScreenSize
        Array.Copy(uexpData, 0, newData, 0, stdSSStart);

        // Write expanded ScreenSize entries
        int dstOff = stdSSStart;
        for (int e = 0; e < SCREEN_SIZE_ENTRIES; e++)
        {
            int srcOff = stdSSStart + e * STANDARD_ENTRY_SIZE;
            int bCooked = BitConverter.ToInt32(uexpData, srcOff);
            float defaultVal = BitConverter.ToSingle(uexpData, srcOff + 4);

            // bCooked (int32)
            Array.Copy(BitConverter.GetBytes(bCooked), 0, newData, dstOff, 4);
            dstOff += 4;

            // Default (float)
            Array.Copy(BitConverter.GetBytes(defaultVal), 0, newData, dstOff, 4);
            dstOff += 4;

            // PerPlatformCount (int32) - always 1
            Array.Copy(BitConverter.GetBytes((int)1), 0, newData, dstOff, 4);
            dstOff += 4;

            // PlatformValue (float) - 0.0 for active LOD 0, sentinel for unused
            if (e == 0 && defaultVal > 0)
            {
                // Active LOD 0 gets PlatformValue = 0.0
                Array.Copy(BitConverter.GetBytes(0.0f), 0, newData, dstOff, 4);
            }
            else if (defaultVal > 0 && defaultVal < 100)
            {
                // Active LOD with screen size > 0 — use sentinel as safe default
                Array.Copy(BitConverter.GetBytes(SENTINEL_VALUE), 0, newData, dstOff, 4);
            }
            else
            {
                // Unused LOD — sentinel value
                Array.Copy(BitConverter.GetBytes(SENTINEL_VALUE), 0, newData, dstOff, 4);
            }
            dstOff += 4;
        }

        // Copy everything after ScreenSize (SpeedTree + Materials + any remaining data)
        int afterSS = stdSSStart + STANDARD_SCREEN_SIZE;
        int remaining = dataLen - afterSS;
        if (remaining > 0)
        {
            Array.Copy(uexpData, afterSS, newData, dstOff, remaining);
        }

        Console.Error.WriteLine($"[ZenConverter] StaticMesh ScreenSize: expanded {STANDARD_SCREEN_SIZE} -> {MARVEL_SCREEN_SIZE} bytes (+{EXPANSION}), matCount={matCount}");
        return newData;
    }

    /// <summary>
    /// Calculate how much padding will be needed for a StringTable.
    /// Each string table entry (Key + Value) needs 4 bytes (FGameplayTagContainer count=0).
    /// Plus 4 bytes trailing padding at the end.
    /// 
    /// StringTable structure in .uexp:
    /// - FString TableNamespace
    /// - int32 NumEntries
    /// - For each entry: FString Key, FString Value, [FGameplayTagContainer - needs to be added]
    /// - [FGameplayTagContainer trailing - needs to be added]
    /// </summary>
    private static int CalculateStringTablePadding(byte[] uexpData)
    {
        const int PADDING_SIZE = 4; // Empty FGameplayTagContainer = int32 count of 0
        int dataLength = uexpData.Length;
        
        // StringTable has a simple structure:
        // 1. TableNamespace (FString: int32 length + chars + null)
        // 2. NumEntries (int32)
        // 3. For each entry: Key FString + Value FString
        
        // Search for the entry count - look for a reasonable int32 followed by FString patterns
        // FString format: int32 length (positive, reasonable size) followed by ASCII/UTF-8 chars
        
        for (int i = 0; i < dataLength - 20; i++)
        {
            // First, try to find the TableNamespace FString
            int nsLen = BitConverter.ToInt32(uexpData, i);
            if (nsLen <= 0 || nsLen > 256)
                continue;
            
            // Check if the string content looks valid (printable ASCII or null terminator)
            bool validString = true;
            for (int j = 0; j < Math.Min(nsLen, 10); j++)
            {
                byte b = uexpData[i + 4 + j];
                if (b != 0 && (b < 32 || b > 126))
                {
                    validString = false;
                    break;
                }
            }
            if (!validString)
                continue;
            
            // After the namespace string, we should have the entry count
            int entryCountOffset = i + 4 + nsLen;
            if (entryCountOffset + 4 > dataLength)
                continue;
            
            int entryCount = BitConverter.ToInt32(uexpData, entryCountOffset);
            if (entryCount <= 0 || entryCount > 10000)
                continue;
            
            // Validate by checking if next bytes look like an FString (key)
            int keyOffset = entryCountOffset + 4;
            if (keyOffset + 4 > dataLength)
                continue;
            
            int keyLen = BitConverter.ToInt32(uexpData, keyOffset);
            if (keyLen <= 0 || keyLen > 1024)
                continue;
            
            // Looks like a valid StringTable structure
            // Each entry needs 4 bytes padding + 4 bytes trailing
            int totalPadding = (entryCount * PADDING_SIZE) + PADDING_SIZE;
            Console.Error.WriteLine($"[ZenConverter] Found StringTable: namespace at 0x{i:X}, {entryCount} entries, will add {totalPadding} bytes padding");
            return totalPadding;
        }
        
        return 0;
    }
    
    /// <summary>
    /// Patch StringTable .uexp data by adding 4-byte FGameplayTagContainer padding after each entry.
    /// Marvel Rivals expects an FGameplayTagContainer (empty = 4 bytes of zeros) after each Key+Value pair.
    /// Plus a trailing FGameplayTagContainer at the end.
    /// </summary>
    private static (byte[]? patchedData, int entryCount, int paddingAdded) PatchStringTableEntries(byte[] uexpData, int dataLength)
    {
        const int PADDING_SIZE = 4; // Empty FGameplayTagContainer = int32 count of 0
        
        // Find the StringTable structure
        int namespaceOffset = -1;
        int namespaceLen = 0;
        int entryCountOffset = -1;
        int entryCount = 0;
        int firstEntryOffset = -1;
        
        for (int i = 0; i < dataLength - 20; i++)
        {
            int nsLen = BitConverter.ToInt32(uexpData, i);
            if (nsLen <= 0 || nsLen > 256)
                continue;
            
            bool validString = true;
            for (int j = 0; j < Math.Min(nsLen, 10); j++)
            {
                byte b = uexpData[i + 4 + j];
                if (b != 0 && (b < 32 || b > 126))
                {
                    validString = false;
                    break;
                }
            }
            if (!validString)
                continue;
            
            int ecOffset = i + 4 + nsLen;
            if (ecOffset + 4 > dataLength)
                continue;
            
            int ec = BitConverter.ToInt32(uexpData, ecOffset);
            if (ec <= 0 || ec > 10000)
                continue;
            
            int keyOffset = ecOffset + 4;
            if (keyOffset + 4 > dataLength)
                continue;
            
            int keyLen = BitConverter.ToInt32(uexpData, keyOffset);
            if (keyLen <= 0 || keyLen > 1024)
                continue;
            
            namespaceOffset = i;
            namespaceLen = nsLen;
            entryCountOffset = ecOffset;
            entryCount = ec;
            firstEntryOffset = keyOffset;
            Console.Error.WriteLine($"[ZenConverter] Found StringTable for patching: namespace=\"{System.Text.Encoding.UTF8.GetString(uexpData, i + 4, Math.Min(nsLen - 1, 50))}\", {entryCount} entries");
            break;
        }
        
        if (entryCount == 0 || firstEntryOffset < 0)
        {
            Console.Error.WriteLine($"[ZenConverter] No StringTable structure found to patch");
            return (null, 0, 0);
        }
        
        // Calculate new size: original + (entryCount * 4) + 4 trailing
        int paddingTotal = (entryCount * PADDING_SIZE) + PADDING_SIZE;
        int newLength = dataLength + paddingTotal;
        byte[] patchedData = new byte[newLength];
        
        // Copy data up to first entry
        Array.Copy(uexpData, 0, patchedData, 0, firstEntryOffset);
        
        int srcOffset = firstEntryOffset;
        int dstOffset = firstEntryOffset;
        
        // For each entry, copy Key + Value, then add 4 bytes padding
        for (int e = 0; e < entryCount; e++)
        {
            // Read and copy Key FString
            if (srcOffset + 4 > dataLength)
                break;
            int keyLen = BitConverter.ToInt32(uexpData, srcOffset);
            int keyTotalLen = 4 + keyLen; // length field + string bytes
            if (srcOffset + keyTotalLen > dataLength)
                break;
            Array.Copy(uexpData, srcOffset, patchedData, dstOffset, keyTotalLen);
            srcOffset += keyTotalLen;
            dstOffset += keyTotalLen;
            
            // Read and copy Value FString
            if (srcOffset + 4 > dataLength)
                break;
            int valLen = BitConverter.ToInt32(uexpData, srcOffset);
            int valTotalLen = 4 + valLen;
            if (srcOffset + valTotalLen > dataLength)
                break;
            Array.Copy(uexpData, srcOffset, patchedData, dstOffset, valTotalLen);
            srcOffset += valTotalLen;
            dstOffset += valTotalLen;
            
            // Add 4 bytes padding (empty FGameplayTagContainer: count = 0)
            patchedData[dstOffset] = 0x00;
            patchedData[dstOffset + 1] = 0x00;
            patchedData[dstOffset + 2] = 0x00;
            patchedData[dstOffset + 3] = 0x00;
            dstOffset += PADDING_SIZE;
        }
        
        // Add trailing 4 bytes padding
        patchedData[dstOffset] = 0x00;
        patchedData[dstOffset + 1] = 0x00;
        patchedData[dstOffset + 2] = 0x00;
        patchedData[dstOffset + 3] = 0x00;
        dstOffset += PADDING_SIZE;
        
        // Copy remaining data after StringTable entries
        int remainingBytes = dataLength - srcOffset;
        if (remainingBytes > 0)
        {
            Array.Copy(uexpData, srcOffset, patchedData, dstOffset, remainingBytes);
        }
        
        Console.Error.WriteLine($"[ZenConverter] Patched StringTable: {entryCount} entries with FGameplayTagContainer padding (+{paddingTotal} bytes)");
        return (patchedData, entryCount, paddingTotal);
    }
}

/// <summary>
/// Represents a complete Zen package
/// </summary>
public class FZenPackage
{
    public EIoContainerHeaderVersion ContainerVersion { get; set; }
    public FZenPackageSummary Summary { get; set; }
    public List<string> NameMap { get; set; }
    public List<FPackageObjectIndex> ImportMap { get; set; }
    public List<FExportMapEntry> ExportMap { get; set; }
    public List<FExportBundleHeader> ExportBundleHeaders { get; set; }
    public List<FExportBundleEntry> ExportBundleEntries { get; set; }
    public List<FDependencyBundleHeader> DependencyBundleHeaders { get; set; }
    public List<FDependencyBundleEntry> DependencyBundleEntries { get; set; }
    public string PackageName { get; set; } = "";
    public int PackageNameIndex { get; set; }
    public uint PackageNameNumber { get; set; } // FName Number for summary Name (e.g. 302 for _301)
    
    // For UE5.0+ package imports
    public List<ulong> ImportedPackages { get; set; }
    public List<string> ImportedPackageNames { get; set; }
    public List<ulong> ImportedPublicExportHashes { get; set; }
    
    // Export reorder mapping: old index -> new index
    public Dictionary<int, int> ExportReorderMap { get; set; }

    public FZenPackage()
    {
        Summary = new FZenPackageSummary();
        NameMap = new List<string>();
        ImportMap = new List<FPackageObjectIndex>();
        ExportMap = new List<FExportMapEntry>();
        ExportBundleHeaders = new List<FExportBundleHeader>();
        ExportBundleEntries = new List<FExportBundleEntry>();
        DependencyBundleHeaders = new List<FDependencyBundleHeader>();
        DependencyBundleEntries = new List<FDependencyBundleEntry>();
        ImportedPackages = new List<ulong>();
        ImportedPackageNames = new List<string>();
        ImportedPublicExportHashes = new List<ulong>();
        ExportReorderMap = new Dictionary<int, int>();
    }
}
