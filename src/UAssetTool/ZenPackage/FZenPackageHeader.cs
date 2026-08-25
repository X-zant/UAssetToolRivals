using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Full Zen package header - contains all sections needed for Zen-to-legacy conversion
/// Ported from retoc-rivals/src/zen.rs
/// </summary>
public class FZenPackageHeader
{
    public FZenPackageSummary Summary { get; set; }
    public EIoContainerHeaderVersion ContainerHeaderVersion { get; set; }
    public bool IsUnversioned { get; set; }
    public FZenPackageVersioningInfo VersioningInfo { get; set; }
    
    // Name map
    public List<string> NameMap { get; set; }
    
    // Import/Export maps
    public List<FPackageObjectIndex> ImportMap { get; set; }
    public List<FExportMapEntry> ExportMap { get; set; }
    
    // Export bundles
    public List<FExportBundleEntry> ExportBundleEntries { get; set; }
    public List<FExportBundleHeader> ExportBundleHeaders { get; set; }
    
    // Dependencies (UE5.3+)
    public List<FDependencyBundleHeader> DependencyBundleHeaders { get; set; }
    public List<FDependencyBundleEntry> DependencyBundleEntries { get; set; }
    
    // Imported packages
    public List<ulong> ImportedPackages { get; set; } // Package IDs
    public List<string> ImportedPackageNames { get; set; }
    public List<int> ImportedPackageNameNumbers { get; set; } // FName numbers for ImportedPackageNames
    public List<ulong> ImportedPublicExportHashes { get; set; }
    
    // Bulk data
    public List<FBulkDataMapEntry> BulkData { get; set; }
    
    // Graph data (legacy UE4/early UE5)
    public List<FInternalDependencyArc> InternalDependencyArcs { get; set; }
    public List<ExternalPackageDependency> ExternalPackageDependencies { get; set; }

    public FZenPackageHeader()
    {
        Summary = new FZenPackageSummary();
        VersioningInfo = new FZenPackageVersioningInfo();
        NameMap = new List<string>();
        ImportMap = new List<FPackageObjectIndex>();
        ExportMap = new List<FExportMapEntry>();
        ExportBundleEntries = new List<FExportBundleEntry>();
        ExportBundleHeaders = new List<FExportBundleHeader>();
        DependencyBundleHeaders = new List<FDependencyBundleHeader>();
        DependencyBundleEntries = new List<FDependencyBundleEntry>();
        ImportedPackages = new List<ulong>();
        ImportedPackageNames = new List<string>();
        ImportedPackageNameNumbers = new List<int>();
        ImportedPublicExportHashes = new List<ulong>();
        BulkData = new List<FBulkDataMapEntry>();
        InternalDependencyArcs = new List<FInternalDependencyArc>();
        ExternalPackageDependencies = new List<ExternalPackageDependency>();
    }

    public string PackageName()
    {
        return GetName(Summary.Name);
    }

    public string SourcePackageName()
    {
        if (ContainerHeaderVersion <= EIoContainerHeaderVersion.Initial)
            return GetName(Summary.SourceName);
        return PackageName();
    }

    public string GetName(FMappedName mappedName)
    {
        // Only use package name map for Package type names
        // Global/Container type names should be resolved via ScriptObjects or ContainerHeader
        string baseName;
        if (mappedName.Type == EMappedNameType.Package)
        {
            if (mappedName.Index < NameMap.Count)
                baseName = NameMap[(int)mappedName.Index];
            else
                baseName = $"__UNKNOWN_NAME_{mappedName.Type}_{mappedName.Index}__";
        }
        else
        {
            baseName = $"__UNKNOWN_NAME_{mappedName.Type}_{mappedName.Index}__";
        }
        
        // Append number suffix if present (Number > 0 means _N where N = Number - 1)
        if (mappedName.Number > 0)
            return $"{baseName}_{mappedName.Number - 1}";
        
        return baseName;
    }
    
    /// <summary>
    /// Get name with fallback to global name map for Global type names
    /// </summary>
    public string GetName(FMappedName mappedName, ScriptObjectsDatabase? scriptObjects)
    {
        string baseName;
        if (mappedName.Type == EMappedNameType.Package)
        {
            if (mappedName.Index < NameMap.Count)
                baseName = NameMap[(int)mappedName.Index];
            else
                baseName = $"__UNKNOWN_NAME_{mappedName.Type}_{mappedName.Index}__";
        }
        else if (mappedName.Type == EMappedNameType.Global && scriptObjects != null)
        {
            // ScriptObjectsDatabase.GetName already handles the number suffix
            return scriptObjects.GetName(mappedName);
        }
        else
        {
            baseName = $"__UNKNOWN_NAME_{mappedName.Type}_{mappedName.Index}__";
        }
        
        // Append number suffix if present (Number > 0 means _N where N = Number - 1)
        if (mappedName.Number > 0)
            return $"{baseName}_{mappedName.Number - 1}";
        
        return baseName;
    }

    /// <summary>
    /// Read only the export map out of a package header.
    ///
    /// Deserialize also walks the name map, bulk data map, imported hashes, import map, bundle
    /// entries, dependency arcs and imported package names. A caller that only needs the
    /// exports - the asset type scan - pays a string allocation per name and several list
    /// allocations for data it never looks at. Reading the summary and seeking straight to the
    /// export map skips all of it.
    ///
    /// Returns null when the data does not reach the end of the export map, which tells the
    /// caller it was handed too short a prefix.
    /// </summary>
    public static List<FExportMapEntry>? ReadExportMapOnly(byte[] data, EIoContainerHeaderVersion containerHeaderVersion)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var summary = new FZenPackageSummary();
        summary.Read(reader, containerHeaderVersion);

        int start = summary.ExportMapOffset;
        int end = summary.ExportBundleEntriesOffset;
        if (start <= 0 || end <= start || end > data.Length) return null;

        const int ExportEntrySize = 72;
        int count = (end - start) / ExportEntrySize;
        var exports = new List<FExportMapEntry>(count);

        ms.Seek(start, SeekOrigin.Begin);
        for (int i = 0; i < count; i++)
        {
            var entry = new FExportMapEntry();
            entry.Read(reader);
            exports.Add(entry);
        }

        return exports;
    }

    public static FZenPackageHeader Deserialize(byte[] data, EIoContainerHeaderVersion containerHeaderVersion)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        return Deserialize(reader, containerHeaderVersion);
    }

    public static FZenPackageHeader Deserialize(BinaryReader reader, EIoContainerHeaderVersion containerHeaderVersion)
    {
        var header = new FZenPackageHeader();
        header.ContainerHeaderVersion = containerHeaderVersion;

        long startPos = reader.BaseStream.Position;

        // Read summary
        header.Summary.Read(reader, containerHeaderVersion);

        // Read versioning info if present
        if (header.Summary.HasVersioningInfo != 0)
        {
            header.VersioningInfo.Read(reader);
            header.IsUnversioned = false;
        }
        else
        {
            header.IsUnversioned = true;
            // Use heuristics to determine version (similar to Rust code)
            header.VersioningInfo = FZenPackageVersioningInfo.CreateDefault();
        }

        // Read name map
        header.ReadNameMap(reader, startPos, containerHeaderVersion);

        // Read bulk data map (UE5.2+) - comes after name map, before imported public export hashes
        // For UE5.3 (NoExportInfo container version), bulk data exists
        if (containerHeaderVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            // Read bulk_data_map_size (i64)
            long bulkDataMapSize = reader.ReadInt64();
            if (bulkDataMapSize > 0)
            {
                // FBulkDataMapEntry is 32 bytes: serial_offset(8) + duplicate_offset(8) + serial_size(8) + flags(4) + pad(4)
                int bulkDataCount = (int)(bulkDataMapSize / 32);
                for (int i = 0; i < bulkDataCount; i++)
                {
                    var entry = new FBulkDataMapEntry
                    {
                        SerialOffset = reader.ReadInt64(),
                        DuplicateSerialOffset = reader.ReadInt64(),
                        SerialSize = reader.ReadInt64(),
                        Flags = reader.ReadUInt32()
                    };
                    reader.ReadUInt32(); // padding
                    header.BulkData.Add(entry);
                }
            }
        }

        // Read imported public export hashes (UE5+)
        if (containerHeaderVersion > EIoContainerHeaderVersion.Initial && header.Summary.ImportedPublicExportHashesOffset >= 0)
        {
            reader.BaseStream.Seek(startPos + header.Summary.ImportedPublicExportHashesOffset, SeekOrigin.Begin);
            int hashCount = (header.Summary.ImportMapOffset - header.Summary.ImportedPublicExportHashesOffset) / 8;
            for (int i = 0; i < hashCount; i++)
            {
                header.ImportedPublicExportHashes.Add(reader.ReadUInt64());
            }
        }

        // Read import map
        reader.BaseStream.Seek(startPos + header.Summary.ImportMapOffset, SeekOrigin.Begin);
        int importCount = (header.Summary.ExportMapOffset - header.Summary.ImportMapOffset) / 8;
        for (int i = 0; i < importCount; i++)
        {
            var idx = new FPackageObjectIndex();
            idx.Read(reader);
            header.ImportMap.Add(idx);
        }

        // Read export map
        reader.BaseStream.Seek(startPos + header.Summary.ExportMapOffset, SeekOrigin.Begin);
        int exportEntrySize = 72; // FExportMapEntry is 72 bytes
        int exportCount = (header.Summary.ExportBundleEntriesOffset - header.Summary.ExportMapOffset) / exportEntrySize;
        for (int i = 0; i < exportCount; i++)
        {
            var entry = new FExportMapEntry();
            entry.Read(reader);
            header.ExportMap.Add(entry);
        }

        // Read export bundle entries
        reader.BaseStream.Seek(startPos + header.Summary.ExportBundleEntriesOffset, SeekOrigin.Begin);
        int bundleEntriesEndOffset;
        if (containerHeaderVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            bundleEntriesEndOffset = header.Summary.DependencyBundleHeadersOffset;
        }
        else
        {
            bundleEntriesEndOffset = header.Summary.GraphDataOffset;
        }
        int bundleEntryCount = (bundleEntriesEndOffset - header.Summary.ExportBundleEntriesOffset) / 8;
        for (int i = 0; i < bundleEntryCount; i++)
        {
            var entry = new FExportBundleEntry();
            entry.Read(reader);
            header.ExportBundleEntries.Add(entry);
        }

        // Generate export bundle headers from entries (UE5.3+ doesn't have explicit headers)
        header.GenerateExportBundleHeaders();

        // Read dependency bundles (UE5.3+)
        if (containerHeaderVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            // Read dependency bundle headers
            reader.BaseStream.Seek(startPos + header.Summary.DependencyBundleHeadersOffset, SeekOrigin.Begin);
            int depHeaderSize = FDependencyBundleHeader.GetSize(containerHeaderVersion);
            int depHeaderCount = (header.Summary.DependencyBundleEntriesOffset - header.Summary.DependencyBundleHeadersOffset) / depHeaderSize;
            for (int i = 0; i < depHeaderCount; i++)
            {
                var depHeader = new FDependencyBundleHeader();
                depHeader.Read(reader, containerHeaderVersion);
                header.DependencyBundleHeaders.Add(depHeader);
            }

            // Read dependency bundle entries
            reader.BaseStream.Seek(startPos + header.Summary.DependencyBundleEntriesOffset, SeekOrigin.Begin);
            int depEntryCount = (header.Summary.ImportedPackageNamesOffset - header.Summary.DependencyBundleEntriesOffset) / FDependencyBundleEntry.Size;
            for (int i = 0; i < depEntryCount; i++)
            {
                var depEntry = new FDependencyBundleEntry();
                depEntry.Read(reader);
                header.DependencyBundleEntries.Add(depEntry);
            }

            // Read imported package names
            reader.BaseStream.Seek(startPos + header.Summary.ImportedPackageNamesOffset, SeekOrigin.Begin);
            int remainingBytes = (int)(header.Summary.HeaderSize - header.Summary.ImportedPackageNamesOffset);
            if (remainingBytes > 0)
            {
                header.ReadImportedPackageNames(reader, remainingBytes);
            }
        }
        else if (header.Summary.GraphDataOffset >= 0)
        {
            // Read graph data (legacy format)
            reader.BaseStream.Seek(startPos + header.Summary.GraphDataOffset, SeekOrigin.Begin);
            header.ReadGraphData(reader);
        }

        return header;
    }

    private void ReadNameMap(BinaryReader reader, long startPos, EIoContainerHeaderVersion containerHeaderVersion)
    {
        if (containerHeaderVersion <= EIoContainerHeaderVersion.Initial)
        {
            // Legacy name map format
            reader.BaseStream.Seek(startPos + Summary.NameMapNamesOffset, SeekOrigin.Begin);
            ReadNameBatch(reader, Summary.NameMapNamesSize);
        }
        else
        {
            // New name map format - right after versioning info
            // Format: numNames(4) + numStringBytes(4) + hashVersion(8) + hashes(numNames*8) + lengths(numNames*2) + strings
            long nameMapPos = reader.BaseStream.Position;
            int numNames = reader.ReadInt32();
            int numStringBytes = reader.ReadInt32();
            ulong hashVersion = reader.ReadUInt64(); // Changed to ulong - this is 8 bytes, not 4
            
            if (Environment.GetEnvironmentVariable("DEBUG") == "1" && (numNames <= 0 || numNames > 10000 || numStringBytes < 0))
                Console.Error.WriteLine($"[ZenPackage] NameMap SUSPICIOUS: numNames={numNames}, numStringBytes={numStringBytes}, hashVersion=0x{hashVersion:X16}, pos={nameMapPos}");

            // Skip hashes
            reader.BaseStream.Seek(numNames * 8, SeekOrigin.Current);

            // Read string data
            ReadNameBatchStrings(reader, numNames, numStringBytes);
        }
    }

    private void ReadNameBatch(BinaryReader reader, int size)
    {
        long endPos = reader.BaseStream.Position + size;
        while (reader.BaseStream.Position < endPos)
        {
            int strLen = reader.ReadInt32();
            if (strLen <= 0 || strLen > 1024) break;
            
            byte[] strBytes = reader.ReadBytes(strLen);
            string name = Encoding.UTF8.GetString(strBytes).TrimEnd('\0');
            NameMap.Add(name);
        }
    }

    private void ReadNameBatchStrings(BinaryReader reader, int numNames, int numStringBytes)
    {
        bool debugMode = Environment.GetEnvironmentVariable("DEBUG") == "1";
        long headersStartPos = reader.BaseStream.Position;
        
        // Read headers first - lengths are in BIG ENDIAN format
        var headers = new List<(int length, bool isWide)>();
        for (int i = 0; i < numNames; i++)
        {
            // Read as big endian i16
            byte hi = reader.ReadByte();
            byte lo = reader.ReadByte();
            short len = (short)((hi << 8) | lo);
            
            bool isWide = len < 0;
            int actualLen;
            if (isWide)
            {
                // For wide strings: charCount = |short.MinValue - len|
                // Encoding: len = charCount + short.MinValue (e.g., 5 chars -> 5 + (-32768) = -32763)
                // Decoding: short.MinValue - len = -32768 - (-32763) = -5, then |result| = 5
                actualLen = Math.Abs(short.MinValue - len);
            }
            else
            {
                actualLen = len;
            }
            headers.Add((actualLen, isWide));
        }

        long stringsStartPos = reader.BaseStream.Position;
        
        // Read string data - strings are stored consecutively WITHOUT alignment padding in the file
        // Alignment is only for runtime memory layout, not serialization
        byte[] stringData = reader.ReadBytes(numStringBytes);
        
        if (debugMode)
        {
            Console.Error.WriteLine($"[ZenPackage] NameMap: {numNames} names, headersStart={headersStartPos}, stringsStart={stringsStartPos}, stringDataLen={stringData.Length}");
        }

        // Parse strings - NO alignment padding in serialized format, strings are consecutive
        int currentOffset = 0;
        for (int i = 0; i < numNames; i++)
        {
            var (length, isWide) = headers[i];
            string name;
            
            if (isWide)
            {
                // Wide strings: length is char count, byte count = length * 2
                // NO alignment padding in serialized format
                int byteLen = length * 2;
                if (currentOffset + byteLen <= stringData.Length)
                {
                    name = Encoding.Unicode.GetString(stringData, currentOffset, byteLen);
                    currentOffset += byteLen;
                }
                else
                {
                    if (debugMode)
                        Console.Error.WriteLine($"[ZenPackage] Name {i}: INVALID WIDE - offset={currentOffset}, byteLen={byteLen}, dataLen={stringData.Length}");
                    name = $"__invalid_wide_{i}__";
                }
            }
            else
            {
                if (currentOffset + length <= stringData.Length)
                {
                    name = Encoding.UTF8.GetString(stringData, currentOffset, length);
                    currentOffset += length;
                }
                else
                {
                    if (debugMode)
                        Console.Error.WriteLine($"[ZenPackage] Name {i}: INVALID - offset={currentOffset}, length={length}, dataLen={stringData.Length}");
                    name = $"__invalid_{i}__";
                }
            }
            
            if (debugMode && i < 20)
            {
                Console.Error.WriteLine($"[ZenPackage] Name {i}: \"{name}\" (len={length}, wide={isWide})");
            }
            
            NameMap.Add(name);
        }
        
        if (debugMode)
        {
            Console.Error.WriteLine($"[ZenPackage] NameMap complete: {NameMap.Count} names, finalOffset={currentOffset}");
        }
    }

    private void ReadImportedPackageNames(BinaryReader reader, int maxBytes)
    {
        // Read name batch format (FZenPackageImportedPackageNamesContainer)
        // Format: name_batch + imported_package_name_numbers (i32[])
        
        // Read name batch
        var names = ReadNameBatch(reader);
        
        // Read name numbers (i32 for each name)
        var nameNumbers = new int[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            nameNumbers[i] = reader.ReadInt32();
        }
        
        // Apply name numbers to create full names for PackageId computation,
        // but store base names (without number) for legacy import resolution.
        // Package paths in the legacy format use the base name, with the FName
        // number stored separately in the import entry.
        for (int i = 0; i < names.Count; i++)
        {
            string baseName = names[i];
            // Compute full FName (with number suffix) for PackageId hash
            string fullNameForHash = baseName;
            if (nameNumbers[i] != 0)
            {
                fullNameForHash = $"{baseName}_{nameNumbers[i] - 1}";
            }
            // Store the base name for legacy import (matching retoc)
            ImportedPackageNames.Add(baseName);
            ImportedPackageNameNumbers.Add(nameNumbers[i]);
            
            // Extract package ID from the FULL name (including number)
            ulong packageId = FPackageId.FromName(fullNameForHash);
            ImportedPackages.Add(packageId);
        }
    }
    
    private const ulong FNAME_HASH_ALGORITHM_ID = 0xC1640000;
    
    private List<string> ReadNameBatch(BinaryReader reader)
    {
        var names = new List<string>();
        
        uint num = reader.ReadUInt32();
        if (num == 0)
            return names;
        
        uint numStringBytes = reader.ReadUInt32();
        ulong hashVersion = reader.ReadUInt64();
        // Skip hash bytes (8 bytes per name)
        reader.ReadBytes((int)(num * 8));
        
        // Read lengths (i16 BE per name)
        var lengths = new short[num];
        for (int i = 0; i < num; i++)
        {
            // Read as big-endian i16
            byte b1 = reader.ReadByte();
            byte b2 = reader.ReadByte();
            lengths[i] = (short)((b1 << 8) | b2);
        }
        
        // Read names
        for (int i = 0; i < num; i++)
        {
            short len = lengths[i];
            bool isUtf16 = len < 0;
            if (isUtf16)
            {
                len = (short)(short.MinValue - len);
                // Read UTF-16 string
                byte[] bytes = reader.ReadBytes(len * 2);
                names.Add(Encoding.Unicode.GetString(bytes));
            }
            else
            {
                // Read ASCII/UTF-8 string
                byte[] bytes = reader.ReadBytes(len);
                names.Add(Encoding.UTF8.GetString(bytes));
            }
        }
        
        return names;
    }

    private void ReadGraphData(BinaryReader reader)
    {
        // Read imported package count
        int importedPackageCount = reader.ReadInt32();
        
        for (int i = 0; i < importedPackageCount; i++)
        {
            ulong packageId = reader.ReadUInt64();
            ImportedPackages.Add(packageId);
        }

        // Read arcs count
        int numArcs = reader.ReadInt32();
        
        // Read external arcs
        for (int i = 0; i < numArcs; i++)
        {
            // This is simplified - full implementation would parse FExternalDependencyArc
            int fromImportIndex = reader.ReadInt32();
            int toExportBundleIndex = reader.ReadInt32();
            // ... more fields
        }
    }

    private void GenerateExportBundleHeaders()
    {
        // For UE5.3+ the export bundle structure is:
        // Each export gets 2 entries: Create and Serialize
        // We generate headers based on this pattern
        if (ExportBundleEntries.Count == 0) return;

        // Group entries by export bundle
        // In UE5.3+, each export typically has its own bundle
        int currentBundleStart = 0;
        uint currentExportIndex = ExportBundleEntries[0].LocalExportIndex;

        for (int i = 0; i < ExportBundleEntries.Count; i++)
        {
            var entry = ExportBundleEntries[i];
            
            // Check if we're starting a new bundle
            bool isNewBundle = entry.LocalExportIndex != currentExportIndex || 
                              (i > 0 && entry.CommandType == EExportCommandType.Create);

            if (isNewBundle && i > currentBundleStart)
            {
                // Finish previous bundle
                var bundleHeader = new FExportBundleHeader
                {
                    FirstEntryIndex = (uint)currentBundleStart,
                    EntryCount = (uint)(i - currentBundleStart),
                    SerialOffset = ulong.MaxValue // Will be calculated from entries
                };
                ExportBundleHeaders.Add(bundleHeader);
                
                currentBundleStart = i;
                currentExportIndex = entry.LocalExportIndex;
            }
        }

        // Add last bundle
        if (currentBundleStart < ExportBundleEntries.Count)
        {
            var bundleHeader = new FExportBundleHeader
            {
                FirstEntryIndex = (uint)currentBundleStart,
                EntryCount = (uint)(ExportBundleEntries.Count - currentBundleStart),
                SerialOffset = ulong.MaxValue
            };
            ExportBundleHeaders.Add(bundleHeader);
        }

        // If we have no headers but have exports, create one header per export
        if (ExportBundleHeaders.Count == 0 && ExportMap.Count > 0)
        {
            // Fallback: assume each export is in order
            for (int i = 0; i < ExportMap.Count; i++)
            {
                int firstEntry = i * 2; // Create + Serialize
                if (firstEntry >= ExportBundleEntries.Count) break;

                var bundleHeader = new FExportBundleHeader
                {
                    FirstEntryIndex = (uint)firstEntry,
                    EntryCount = 2, // Create + Serialize
                    SerialOffset = ExportMap[i].CookedSerialOffset
                };
                ExportBundleHeaders.Add(bundleHeader);
            }
        }
    }
}

/// <summary>
/// Zen package versioning info
/// </summary>
public class FZenPackageVersioningInfo
{
    public int ZenVersion { get; set; }
    public int FileVersionUE4 { get; set; }
    public int FileVersionUE5 { get; set; }
    public int LicenseeVersion { get; set; }
    public List<FCustomVersion> CustomVersions { get; set; }

    public FZenPackageVersioningInfo()
    {
        CustomVersions = new List<FCustomVersion>();
    }

    public void Read(BinaryReader reader)
    {
        ZenVersion = reader.ReadInt32();
        FileVersionUE4 = reader.ReadInt32();
        FileVersionUE5 = reader.ReadInt32();
        LicenseeVersion = reader.ReadInt32();

        int numCustomVersions = reader.ReadInt32();
        for (int i = 0; i < numCustomVersions; i++)
        {
            var cv = new FCustomVersion();
            cv.Read(reader);
            CustomVersions.Add(cv);
        }
    }

    public static FZenPackageVersioningInfo CreateDefault()
    {
        return new FZenPackageVersioningInfo
        {
            ZenVersion = 3,
            FileVersionUE4 = 522,
            FileVersionUE5 = 1008,
            LicenseeVersion = 0
        };
    }
}

/// <summary>
/// Custom version entry
/// </summary>
public class FCustomVersion
{
    public Guid Key { get; set; }
    public int Version { get; set; }

    public void Read(BinaryReader reader)
    {
        byte[] guidBytes = reader.ReadBytes(16);
        Key = new Guid(guidBytes);
        Version = reader.ReadInt32();
    }
}

/// <summary>
/// Bulk data map entry
/// </summary>
public class FBulkDataMapEntry
{
    public long SerialOffset { get; set; }
    public long DuplicateSerialOffset { get; set; }
    public long SerialSize { get; set; }
    public uint Flags { get; set; }

    public void Read(BinaryReader reader)
    {
        SerialOffset = reader.ReadInt64();
        DuplicateSerialOffset = reader.ReadInt64();
        SerialSize = reader.ReadInt64();
        Flags = reader.ReadUInt32();
        reader.ReadUInt32(); // padding
    }
}


/// <summary>
/// Package ID calculation (CityHash64 of lowercase path)
/// </summary>
public static class FPackageId
{
    public static ulong FromName(string name)
    {
        string normalized = name.ToLowerInvariant();
        return IoStore.CityHash.CityHash64(normalized);
    }
}
