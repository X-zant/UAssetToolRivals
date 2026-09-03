using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetTool.IoStore;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Context for loading and caching Zen packages from IoStore containers.
/// Provides package lookup and cross-package import resolution.
/// Ported from retoc-rivals/src/lib.rs FZenPackageContext
/// </summary>
public class FZenPackageContext : IDisposable
{
    private readonly List<IoStoreReader> _containers = new();
    private readonly ConcurrentDictionary<ulong, CachedPackage> _packageCache = new();
    private readonly object _loadLock = new();
    private readonly Dictionary<ulong, (int ContainerIndex, FIoChunkId ChunkId)> _packageIdToChunk = new();
    /// Indices of containers loaded with priority, i.e. mod containers (see LoadContainerWithPriority).
    /// Used to keep a mod's bulk payload lookups from matching the game package it overrides.
    private readonly HashSet<int> _priorityContainers = new();
    private readonly Dictionary<ulong, string> _packageIdToPath = new(); // PackageId -> full package path
    private readonly Dictionary<ulong, string> _packageIdToContainerPath = new(); // PackageId -> path inside the container
    // Normalized VFS path (no extension) -> PackageId, for exact CUE4Parse-style lookups
    private readonly Dictionary<string, ulong> _vfsPathToPackageId = new(StringComparer.OrdinalIgnoreCase);
    // Mount ("/Game/", "/MarvelGAS/") -> cooked root ("Marvel/Content", "Marvel/Plugins/MarvelGAS/Content")
    private readonly Dictionary<string, string> _mountToVfsRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, List<ulong>> _packageExportHashes = new(); // PackageId -> list of public export hashes
    private readonly Dictionary<int, List<string>> _containerNameMaps = new(); // ContainerIndex -> global name map
    // Per-package export hash lookup: packageId -> (cityHash64(lowerName) -> exportIndex)
    // Built once on first use, eliminates O(N) linear scan in ResolvePackageImport
    private readonly ConcurrentDictionary<ulong, Dictionary<ulong, int>> _exportHashLookup = new();
    
    public ScriptObjectsDatabase? ScriptObjects { get; private set; }
    public EIoContainerHeaderVersion ContainerHeaderVersion { get; private set; } = EIoContainerHeaderVersion.NoExportInfo;
    
    public int PackageCount => _packageIdToChunk.Count;
    public int ContainerCount => _containers.Count;

    private byte[]? _aesKey;
    private readonly List<byte[]> _aesKeys = new();
    
    /// <summary>
    /// Set the AES key for decrypting encrypted containers.
    /// Also adds it to the multi-key list.
    /// </summary>
    public void SetAesKey(string hexKey)
    {
        if (string.IsNullOrEmpty(hexKey))
            return;
        hexKey = hexKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hexKey[2..] : hexKey;
        _aesKey = Convert.FromHexString(hexKey);
        // Add to multi-key list if not already present
        if (!_aesKeys.Any(k => k.SequenceEqual(_aesKey)))
            _aesKeys.Add(_aesKey);
    }
    
    /// <summary>
    /// Add an additional AES key for decrypting encrypted containers.
    /// Multiple keys are tried when opening each container.
    /// </summary>
    public void AddAesKey(string hexKey)
    {
        if (string.IsNullOrEmpty(hexKey))
            return;
        hexKey = hexKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hexKey[2..] : hexKey;
        var keyBytes = Convert.FromHexString(hexKey);
        if (!_aesKeys.Any(k => k.SequenceEqual(keyBytes)))
            _aesKeys.Add(keyBytes);
        // Set primary key if not yet set
        _aesKey ??= keyBytes;
    }
    
    /// <summary>
    /// Load an IoStore container and index all packages
    /// </summary>
    public void LoadContainer(string utocPath)
    {
        LoadContainerInternal(utocPath, overridePriority: false);
    }
    
    /// <summary>
    /// Load an IoStore container with priority - packages in this container will override
    /// any previously loaded packages with the same ID. Used for mod containers.
    /// Tries without encryption first; if the container is obfuscated (encrypted), 
    /// automatically retries with the AES key.
    /// </summary>
    public void LoadContainerWithPriority(string utocPath)
    {
        // First try without encryption (normal mods)
        var testReader = new IoStore.IoStoreReader(utocPath, (byte[]?)null);
        if (testReader.Toc.IsEncrypted && _aesKey != null)
        {
            // Obfuscated mod - reload with AES key for block decryption
            testReader.Dispose();
            Console.Error.WriteLine($"[Context] Detected obfuscated mod, loading with AES key...");
            LoadContainerInternal(utocPath, overridePriority: true, useEncryption: true);
        }
        else
        {
            // Normal unencrypted mod - use the already-opened reader
            testReader.Dispose();
            LoadContainerInternal(utocPath, overridePriority: true, useEncryption: false);
        }
    }
    
    private void LoadContainerInternal(string utocPath, bool overridePriority, bool useEncryption = true)
    {
        IoStoreReader reader;
        if (useEncryption && _aesKeys.Count > 1)
            reader = new IoStoreReader(utocPath, _aesKeys);
        else
            reader = new IoStoreReader(utocPath, useEncryption ? _aesKey : null);
        int containerIndex = _containers.Count;
        _containers.Add(reader);
        if (overridePriority)
            _priorityContainers.Add(containerIndex);
        
        // Determine container version from TOC version
        // Only update from non-priority (game) containers, since mod containers
        // may have been created with older tool versions that wrote a different TOC version
        // but still used NoExportInfo format for the actual zen package data.
        if (!overridePriority)
        {
            if (reader.Toc.Version >= EIoStoreTocVersion.PerfectHashWithOverflow)
                ContainerHeaderVersion = EIoContainerHeaderVersion.NoExportInfo;
            else if (reader.Toc.Version >= EIoStoreTocVersion.PartitionSize)
                ContainerHeaderVersion = EIoContainerHeaderVersion.LocalizedPackages;
            else
                ContainerHeaderVersion = EIoContainerHeaderVersion.Initial;
        }
        
        
        // Read ContainerHeader chunk to get global name map (for versions > Initial)
        if (ContainerHeaderVersion > EIoContainerHeaderVersion.Initial)
        {
            foreach (var chunk in reader.GetChunks())
            {
                if (chunk.GetChunkType() == EIoChunkType.ContainerHeader)
                {
                    try
                    {
                        byte[] headerData = reader.ReadChunk(chunk);
                        var nameMap = ParseContainerHeaderNameMap(headerData);
                        if (nameMap.Count > 0)
                        {
                            _containerNameMaps[containerIndex] = nameMap;
                        }
                    }
                    catch (Exception ex)
                    {
                    }
                    break;
                }
            }
        }
        
        int newPackages = 0;
        int overriddenPackages = 0;
        
        // Index all ExportBundleData chunks (actual packages)
        foreach (var chunk in reader.GetChunks())
        {
            if (chunk.GetChunkType() != EIoChunkType.ExportBundleData)
                continue;
            
            ulong packageId = chunk.Id;
            
            bool exists = _packageIdToChunk.ContainsKey(packageId);
            
            // Always allow later containers to override earlier ones
            // In IoStore, later-loaded containers have higher priority (matching game engine)
            {
                if (exists)
                {
                    // Clear cached data for overridden package
                    _packageCache.TryRemove(packageId, out _);
                    overriddenPackages++;
                }
                else
                {
                    newPackages++;
                }
                
                _packageIdToChunk[packageId] = (containerIndex, chunk);
                
                // Get full package path from TOC directory index
                string? chunkPath = reader.GetChunkPath(chunk);
                if (!string.IsNullOrEmpty(chunkPath))
                {
                    // Convert file path to package path (remove extension, mount aware)
                    string packagePath = ConvertFilePathToPackagePath(chunkPath);
                    _packageIdToPath[packageId] = packagePath;

                    // Keep the container path too. It is the only place the mount is spelled
                    // out in full - a package path names the plugin but not where the plugin
                    // lives, so /Niagara/X cannot be turned back into
                    // Engine/Plugins/FX/Niagara/Content/X without it.
                    string vfsPath = StripMountPrefix(chunkPath);
                    _packageIdToContainerPath[packageId] = vfsPath;
                    _vfsPathToPackageId[StripPackageExtension(vfsPath)] = packageId;
                    RecordMountRoot(packagePath, vfsPath);
                }
            }
        }
        
        string priorityStr = overridePriority ? " [PRIORITY]" : "";
        string overrideStr = overriddenPackages > 0 ? $", {overriddenPackages} overridden" : "";
    }
    
    /// <summary>
    /// Parse the name map from a ContainerHeader chunk
    /// Based on retoc-rivals FIoContainerHeader deserialization
    /// </summary>
    private static List<string> ParseContainerHeaderNameMap(byte[] data)
    {
        var names = new List<string>();
        
        // The name map in ContainerHeader uses hash_version = 0xC1640000
        // Search for this signature: count(4) + num_string_bytes(4) + hash_version(8)
        const ulong NAME_HASH_ALGORITHM_ID = 0x00000000C1640000UL; // Little endian
        byte[] signature = BitConverter.GetBytes(NAME_HASH_ALGORITHM_ID);
        
        // Search for the signature in the data
        int signaturePos = -1;
        for (int i = 8; i < data.Length - 8; i++)
        {
            if (data[i] == signature[0] && data[i + 1] == signature[1] &&
                data[i + 2] == signature[2] && data[i + 3] == signature[3] &&
                data[i + 4] == signature[4] && data[i + 5] == signature[5] &&
                data[i + 6] == signature[6] && data[i + 7] == signature[7])
            {
                signaturePos = i;
                break;
            }
        }
        
        if (signaturePos < 8)
            return names;
        
        // Read numNames and numStringBytes from before the signature
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        
        reader.BaseStream.Seek(signaturePos - 8, SeekOrigin.Begin);
        int numNames = reader.ReadInt32();
        int numStringBytes = reader.ReadInt32();
        ulong hashVersion = reader.ReadUInt64();
        
        if (numNames <= 0 || numNames > 100000 || hashVersion != NAME_HASH_ALGORITHM_ID)
            return names;
        
        // Skip hashes
        reader.BaseStream.Seek(numNames * 8, SeekOrigin.Current);
        
        // Read length headers (big endian i16)
        var lengths = new List<(int len, bool isWide)>();
        for (int i = 0; i < numNames; i++)
        {
            byte hi = reader.ReadByte();
            byte lo = reader.ReadByte();
            short len = (short)((hi << 8) | lo);
            bool isWide = len < 0;
            // For wide strings: charCount = |short.MinValue - len|
            // Encoding: len = charCount + short.MinValue (e.g., 5 chars -> 5 + (-32768) = -32763)
            int actualLen = isWide ? Math.Abs(short.MinValue - len) : len;
            lengths.Add((actualLen, isWide));
        }
        
        // Read string data - strings are stored consecutively WITHOUT alignment padding
        byte[] stringData = reader.ReadBytes(numStringBytes);
        
        // Parse strings - NO alignment padding in serialized format
        int currentOffset = 0;
        for (int i = 0; i < numNames; i++)
        {
            var (len, isWide) = lengths[i];
            if (isWide)
            {
                int byteLen = len * 2;
                if (currentOffset + byteLen <= stringData.Length)
                {
                    names.Add(System.Text.Encoding.Unicode.GetString(stringData, currentOffset, byteLen));
                    currentOffset += byteLen;
                }
                else
                {
                    names.Add($"__invalid_wide_{i}__");
                }
            }
            else
            {
                if (currentOffset + len <= stringData.Length)
                {
                    names.Add(System.Text.Encoding.UTF8.GetString(stringData, currentOffset, len));
                    currentOffset += len;
                }
                else
                {
                    names.Add($"__invalid_{i}__");
                }
            }
        }
        
        return names;
    }
    
    /// <summary>
    /// Get the global name map for a container
    /// </summary>
    public List<string>? GetContainerNameMap(int containerIndex)
    {
        return _containerNameMaps.TryGetValue(containerIndex, out var nameMap) ? nameMap : null;
    }
    
    /// <summary>
    /// Convert a file path like "../../../Marvel/Content/..." to package path like "/Game/Marvel/..."
    /// </summary>
    private static string ConvertFilePathToPackagePath(string filePath)
    {
        // Remove extension (.uasset, .uexp, etc.)
        string path = filePath;
        if (path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            path = path[..^7];
        else if (path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
            path = path[..^5];
        else if (path.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase))
            path = path[..^6];
        
        // Remove mount point prefix (e.g., "../../../"), including a repeated one
        path = VfsPath.StripMountPrefix(path);
        
        // Map the cooked location back onto the mount it came from. Rewriting every
        // "/Content/" to /Game/ collapsed three different mounts into one: an engine asset
        // became /Game/EngineMaterials/..., and a plugin asset became /Game/Marvel/... where
        // it could collide with a real /Game/ package. These names are handed out as import
        // names, and a package id is the hash of the name, so getting this wrong points an
        // import at a package that either does not exist or is the wrong one.

        // A plugin mounts at its own name: <root>/Plugins/[category/]<Name>/Content/X -> /<Name>/X.
        // The category folders are part of the layout on disk only, which is why this cannot
        // be run in reverse - see GetContainerPath.
        int pluginsIdx = path.IndexOf("/Plugins/", StringComparison.OrdinalIgnoreCase);
        if (pluginsIdx >= 0)
        {
            int pluginContentIdx = path.IndexOf("/Content/", pluginsIdx, StringComparison.OrdinalIgnoreCase);
            if (pluginContentIdx > pluginsIdx)
            {
                string between = path[(pluginsIdx + 9)..pluginContentIdx];
                int lastSlash = between.LastIndexOf('/');
                string pluginName = lastSlash >= 0 ? between[(lastSlash + 1)..] : between;
                if (pluginName.Length > 0)
                    return "/" + pluginName + path[(pluginContentIdx + 8)..];
            }
        }

        // Engine content mounts at /Engine.
        if (path.StartsWith("Engine/Content/", StringComparison.OrdinalIgnoreCase))
            return "/Engine/" + path["Engine/Content/".Length..];

        // Anything else under a Content folder is project content, which mounts at /Game.
        if (path.Contains("/Content/"))
        {
            int contentIdx = path.IndexOf("/Content/");
            string afterContent = path[(contentIdx + 9)..]; // Skip "/Content/"
            path = $"/Game/{afterContent}";
        }
        else if (!path.StartsWith("/"))
        {
            // No Content folder means the container indexes by mount, as mod containers do.
            path = path.Contains('/') ? "/" + path : "/Game/" + path;
        }

        return path;
    }

    /// <summary>
    /// Load script objects database for import resolution
    /// </summary>
    public void LoadScriptObjects(string scriptObjectsPath)
    {
        if (File.Exists(scriptObjectsPath))
        {
            ScriptObjects = ScriptObjectsDatabase.Load(scriptObjectsPath);
        }
    }

    /// <summary>
    /// Load script objects from a container's global chunk
    /// </summary>
    public void LoadScriptObjectsFromContainer(int containerIndex = 0)
    {
        if (containerIndex >= _containers.Count)
            return;
        
        var reader = _containers[containerIndex];
        
        // Look for ScriptObjects chunk
        foreach (var chunk in reader.GetChunks())
        {
            if (chunk.GetChunkType() == EIoChunkType.ScriptObjects)
            {
                try
                {
                    byte[] data = reader.ReadChunk(chunk);
                    ScriptObjects = ScriptObjectsDatabase.Load(data);
                    return;
                }
                catch (Exception ex)
                {
                }
            }
        }
    }

    /// <summary>
    /// Get a package by its ID (lazy loading with caching).
    /// Thread-safe: IoStoreReader.ReadChunk uses RandomAccess (no seek races).
    /// ConcurrentDictionary.GetOrAdd serializes duplicate loads for the same key.
    /// </summary>
    public FZenPackageHeader? GetPackage(ulong packageId)
    {
        // Fast path: already cached
        if (_packageCache.TryGetValue(packageId, out var cached))
            return cached.Header;
        
        if (!_packageIdToChunk.TryGetValue(packageId, out var location))
            return null;

        try
        {
            var result = _packageCache.GetOrAdd(packageId, _ =>
            {
                var reader = _containers[location.ContainerIndex];
                byte[] rawData = reader.ReadChunk(location.ChunkId);
                var header = FZenPackageHeader.Deserialize(rawData, ContainerHeaderVersion);
                var pkg = new CachedPackage { Header = header, RawData = rawData, PackageId = packageId };
                lock (_loadLock) { IndexPackageExports(packageId, header); }
                return pkg;
            });
            return result.Header;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Context] Failed to load package {packageId:X16}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get cached package with raw data
    /// </summary>
    public CachedPackage? GetCachedPackage(ulong packageId)
    {
        // Ensure it's loaded
        GetPackage(packageId);
        
        _packageCache.TryGetValue(packageId, out var cached);
        return cached;
    }

    /// <summary>
    /// Get package by path (computes package ID from path)
    /// </summary>
    public FZenPackageHeader? GetPackageByPath(string packagePath)
    {
        ulong packageId = FPackageId.FromName(packagePath);
        return GetPackage(packageId);
    }

    /// <summary>
    /// Check if a package exists
    /// </summary>
    public bool HasPackage(ulong packageId)
    {
        return _packageIdToChunk.ContainsKey(packageId);
    }
    
    /// <summary>
    /// Path of a package inside its container, mount prefix removed - for example
    /// Marvel/Plugins/MarvelGAS/Content/Marvel/AbilitySystem/1053/1053_Table/x.uasset.
    /// This is what the cooker laid down, so extracting to it round-trips exactly.
    /// </summary>
    public string? GetContainerPath(ulong packageId)
    {
        return _packageIdToContainerPath.TryGetValue(packageId, out string? path) ? path : null;
    }

    /// <summary>
    /// Drop the ../ segments a container mount point starts with.
    /// </summary>
    private static string StripMountPrefix(string path)
    {
        return VfsPath.StripMountPrefix(path);
    }

    /// <summary>
    /// Drop a package file extension, if present.
    /// </summary>
    private static string StripPackageExtension(string path)
    {
        foreach (var ext in new[] { ".uasset", ".umap", ".uexp", ".ubulk", ".uptnl" })
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return path[..^ext.Length];
        }
        return path;
    }

    /// <summary>
    /// Normalize to the VFS index key form: forward slashes, no leading ../ or /, no extension.
    /// </summary>
    public static string NormalizeVfsPath(string path)
    {
        return StripPackageExtension(StripMountPrefix(path.Replace('\\', '/'))).TrimEnd('/');
    }

    /// <summary>
    /// Learn a mount's cooked root by lining up a package's mount path against its VFS path.
    /// </summary>
    private void RecordMountRoot(string packagePath, string vfsPath)
    {
        if (!packagePath.StartsWith('/')) return;

        int secondSlash = packagePath.IndexOf('/', 1);
        if (secondSlash < 0) return;

        string mount = packagePath[..(secondSlash + 1)];
        if (_mountToVfsRoot.ContainsKey(mount)) return;

        string tail = packagePath[secondSlash..];
        string bare = StripPackageExtension(vfsPath);
        // Only a pair that actually lines up teaches anything.
        if (!bare.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return;

        // A cooked mount always ends at a Content folder.
        string root = bare[..^tail.Length];
        if (!root.EndsWith("Content", StringComparison.OrdinalIgnoreCase)) return;

        _mountToVfsRoot[mount] = root;
    }

    /// <summary>
    /// Map a mount path (/Game/X, /MarvelGAS/X) to its VFS path; null if the mount is unknown.
    /// </summary>
    public string? ResolveMountToVfsPath(string packagePath)
    {
        if (string.IsNullOrEmpty(packagePath) || !packagePath.StartsWith('/'))
            return null;

        int secondSlash = packagePath.IndexOf('/', 1);
        if (secondSlash < 0) return null;

        string mount = packagePath[..(secondSlash + 1)];
        if (!_mountToVfsRoot.TryGetValue(mount, out string? root))
            return null;

        return root + packagePath[secondSlash..];
    }

    /// <summary>
    /// Exact lookup by VFS path, with or without the extension and in either slash style.
    /// </summary>
    public ulong? FindPackageIdByVfsPath(string vfsPath)
    {
        if (string.IsNullOrEmpty(vfsPath)) return null;
        return _vfsPathToPackageId.TryGetValue(NormalizeVfsPath(vfsPath), out ulong id) ? id : null;
    }

    /// <summary>
    /// Substring filter tested against both the VFS path and the mount path.
    /// </summary>
    public bool PathMatchesFilter(ulong packageId, string pattern)
    {
        string needle = NormalizeVfsPath(pattern);
        if (needle.Length == 0) return false;

        if (_packageIdToContainerPath.TryGetValue(packageId, out string? vfs) &&
            vfs.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return true;

        return _packageIdToPath.TryGetValue(packageId, out string? pkg) &&
               (pkg.Contains(pattern.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                pkg.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get the full package path for a package ID
    /// </summary>
    public string? GetPackagePath(ulong packageId)
    {
        return _packageIdToPath.TryGetValue(packageId, out string? path) ? path : null;
    }

    /// <summary>
    /// Find package ID by searching indexed paths (case-insensitive partial match)
    /// </summary>
    public ulong? FindPackageIdByPath(string searchPath)
    {
        string searchLower = searchPath.ToLowerInvariant();
        foreach (var kvp in _packageIdToPath)
        {
            if (kvp.Value.ToLowerInvariant().Contains(searchLower))
            {
                return kvp.Key;
            }
        }
        return null;
    }

    /// <summary>
    /// Get all package IDs
    /// </summary>
    public IEnumerable<ulong> GetAllPackageIds()
    {
        return _packageIdToChunk.Keys;
    }
    
    /// <summary>
    /// Get package IDs from a specific container (by index)
    /// </summary>
    public IEnumerable<ulong> GetPackageIdsFromContainer(int containerIndex)
    {
        return _packageIdToChunk
            .Where(kvp => kvp.Value.ContainerIndex == containerIndex)
            .Select(kvp => kvp.Key);
    }
    
    /// <summary>
    /// Get the index of the last loaded container (typically the mod container)
    /// </summary>
    public int LastContainerIndex => _containers.Count - 1;
    
    /// <summary>
    /// Get the container index that owns the ExportBundleData for a package
    /// </summary>
    public int GetPackageContainerIndex(ulong packageId)
    {
        if (_packageIdToChunk.TryGetValue(packageId, out var location))
            return location.ContainerIndex;
        return -1;
    }
    
    /// <summary>
    /// Check if a specific container has BulkData chunks for a package
    /// </summary>
    public bool ContainerHasBulkData(ulong packageId, int containerIndex)
    {
        if (containerIndex < 0 || containerIndex >= _containers.Count)
            return false;
        var reader = _containers[containerIndex];
        foreach (var chunk in reader.GetChunks())
        {
            if (chunk.GetChunkType() == IoStore.EIoChunkType.BulkData && chunk.Id == packageId)
                return true;
        }
        return false;
    }
    
    /// <summary>
    /// Read bulk data chunk for a package from a specific container.
    /// If containerIndex is -1, uses the container that owns the ExportBundleData.
    /// </summary>
    public byte[]? ReadBulkData(ulong packageId, int containerIndex = -1)
    {
        if (containerIndex < 0)
        {
            // Fall back to the container that owns the ExportBundleData
            if (!_packageIdToChunk.TryGetValue(packageId, out var location))
                return null;
            containerIndex = location.ContainerIndex;
        }
        
        if (containerIndex >= _containers.Count)
            return null;
            
        var reader = _containers[containerIndex];
        
        // Collect all BulkData chunks for this package (may have multiple with different indices)
        var bulkChunks = new List<(ushort Index, byte[] Data)>();
        
        foreach (var chunk in reader.GetChunks())
        {
            // Check if this is a BulkData chunk for our package
            if (chunk.GetChunkType() == IoStore.EIoChunkType.BulkData && chunk.Id == packageId)
            {
                try
                {
                    byte[] data = reader.ReadChunk(chunk);
                    bulkChunks.Add((chunk.Index, data));
                }
                catch
                {
                    // Skip failed chunks
                }
            }
        }
        
        if (bulkChunks.Count == 0)
            return null;
            
        // Sort by index and concatenate
        bulkChunks.Sort((a, b) => a.Index.CompareTo(b.Index));
        
        if (bulkChunks.Count == 1)
            return bulkChunks[0].Data;
            
        // Concatenate all chunks
        int totalSize = bulkChunks.Sum(c => c.Data.Length);
        byte[] result = new byte[totalSize];
        int offset = 0;
        foreach (var (_, data) in bulkChunks)
        {
            Array.Copy(data, 0, result, offset, data.Length);
            offset += data.Length;
        }
        
        return result;
    }

    /// <summary>
    /// Read optional bulk data (high-res mips, e.g. the Marvel Rivals High-Res DLC) for a package.
    /// These live in *optional containers as bare OptionalBulkData chunks keyed by the SAME package id
    /// (no export bundle, no directory-index path), so we resolve by package id + chunk type across all
    /// containers first — exactly like ReadBulkData does for .ubulk. The legacy path-based lookup remains
    /// as a fallback for layouts that DO expose a .uptnl path entry.
    /// </summary>
    public byte[]? ReadOptionalBulkData(ulong packageId, int containerIndex = -1)
    {
        return ReadBulkDataById(packageId, IoStore.EIoChunkType.OptionalBulkData, containerIndex)
               ?? ReadBulkDataByPath(packageId, ".uptnl", containerIndex);
    }

    /// <summary>
    /// Read memory-mapped bulk data for a package. Resolves by package id + chunk type across all
    /// containers first (bare chunks have no path entry), falling back to path-based lookup.
    /// </summary>
    public byte[]? ReadMemoryMappedBulkData(ulong packageId, int containerIndex = -1)
    {
        return ReadBulkDataById(packageId, IoStore.EIoChunkType.MemoryMappedBulkData, containerIndex)
               ?? ReadBulkDataByPath(packageId, ".m.ubulk", containerIndex);
    }

    /// <summary>
    /// True when the payload lookup must not leave <paramref name="sourceContainerIndex"/>.
    ///
    /// A mod package overrides a game package and therefore carries the SAME package id. An
    /// unrestricted lookup would also match the game's chunks for that id, which both leaks the
    /// game's payload when the mod ships none and concatenates mod + game payloads when it does.
    /// Game containers keep the cross-container search, because an optional high-res payload
    /// legitimately lives in a separate *optional container from its ExportBundleData.
    /// </summary>
    private bool MustRestrictToSourceContainer(int sourceContainerIndex) =>
        sourceContainerIndex >= 0 && _priorityContainers.Contains(sourceContainerIndex);

    /// <summary>
    /// Find all chunks of a given type sharing a package id, sorted by chunk index and concatenated.
    /// Returns null if none exist. Used for bulk payloads that may live in a different container than
    /// the package's ExportBundleData (e.g. *optional High-Res DLC containers), so the search spans
    /// every loaded container — except for mod-sourced packages, which are pinned to their own
    /// container by MustRestrictToSourceContainer.
    /// </summary>
    private byte[]? ReadBulkDataById(ulong packageId, IoStore.EIoChunkType chunkType, int sourceContainerIndex = -1)
    {
        bool debug = Environment.GetEnvironmentVariable("DEBUG_BULK") == "1";
        bool restrict = MustRestrictToSourceContainer(sourceContainerIndex);
        var parts = new List<(int Container, ushort Index, byte[] Data)>();
        for (int ci = 0; ci < _containers.Count; ci++)
        {
            if (restrict && ci != sourceContainerIndex) continue;
            var reader = _containers[ci];
            foreach (var chunk in reader.GetChunks())
            {
                if (chunk.Id != packageId || chunk.GetChunkType() != chunkType) continue;
                try
                {
                    byte[] data = reader.ReadChunk(chunk);
                    if (data != null && data.Length > 0)
                        parts.Add((ci, chunk.Index, data));
                }
                catch (Exception ex)
                {
                    if (debug)
                        Console.Error.WriteLine($"[DEBUG_BULK]   {chunkType} read failed in container[{ci}] ({reader.ContainerName}): {ex.Message}");
                }
            }
        }
        if (parts.Count == 0) return null;

        parts.Sort((a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Container.CompareTo(b.Container));
        if (debug)
            Console.Error.WriteLine($"[DEBUG_BULK]   {chunkType} for 0x{packageId:X16}: {parts.Count} chunk(s), {parts.Sum(p => p.Data.Length)} bytes (by id)");
        if (parts.Count == 1) return parts[0].Data;

        int total = parts.Sum(p => p.Data.Length);
        byte[] result = new byte[total];
        int offset = 0;
        foreach (var (_, _, data) in parts)
        {
            Array.Copy(data, 0, result, offset, data.Length);
            offset += data.Length;
        }
        return result;
    }
    
    /// <summary>
    /// Read bulk data by resolving the package's file path and changing the extension.
    /// Optional/MemoryMapped containers use different chunk IDs than the main container,
    /// so we must resolve by directory index path rather than package ID.
    /// </summary>
    private byte[]? ReadBulkDataByPath(ulong packageId, string extension, int containerIndex)
    {
        bool debug = Environment.GetEnvironmentVariable("DEBUG_BULK") == "1";
        
        // Step 1: Find the ExportBundleData chunk and get its file path from the directory index
        if (!_packageIdToChunk.TryGetValue(packageId, out var location))
            return null;
        
        int sourceCI = containerIndex >= 0 ? containerIndex : location.ContainerIndex;
        if (sourceCI < 0 || sourceCI >= _containers.Count)
            return null;
        
        var sourceReader = _containers[sourceCI];
        string? chunkPath = sourceReader.GetChunkPath(location.ChunkId);
        if (string.IsNullOrEmpty(chunkPath))
        {
            if (debug)
                Console.Error.WriteLine($"[DEBUG_BULK] ReadBulkDataByPath: no path for packageId=0x{packageId:X16}");
            return null;
        }
        
        // Step 2: Change the extension. GetChunkPath returns the directory index key, mount
        // point included, and that is exactly how every container's FileMap is keyed - so it is
        // used as-is. This used to strip a mount prefix here, which was only correct because
        // GetChunkPath applied the mount a second time; both sides are fixed together.
        string relativePath = chunkPath;
        
        // Change extension: .uasset -> .uptnl or .m.ubulk
        string bulkRelPath;
        if (extension == ".m.ubulk")
        {
            // .m.ubulk is a double extension
            string baseName = Path.ChangeExtension(relativePath, null);
            bulkRelPath = baseName + ".m.ubulk";
        }
        else
        {
            bulkRelPath = Path.ChangeExtension(relativePath, extension);
        }
        // Normalize to forward slashes
        bulkRelPath = bulkRelPath.Replace('\\', '/');
        
        if (debug)
            Console.Error.WriteLine($"[DEBUG_BULK] ReadBulkDataByPath: looking for '{bulkRelPath}' across {_containers.Count} containers");
        
        // Step 3: Search containers for this file path. A mod-sourced package stays inside its own
        // container (see MustRestrictToSourceContainer); game packages search everywhere.
        bool restrict = MustRestrictToSourceContainer(containerIndex);
        for (int ci = 0; ci < _containers.Count; ci++)
        {
            if (restrict && ci != containerIndex) continue;
            var reader = _containers[ci];
            try
            {
                byte[]? data = reader.ReadChunkByPath(bulkRelPath);
                if (data != null && data.Length > 0)
                {
                    if (debug)
                        Console.Error.WriteLine($"[DEBUG_BULK]   Found in container[{ci}] ({reader.ContainerName}): {data.Length} bytes");
                    return data;
                }
            }
            catch (Exception ex)
            {
                if (debug)
                    Console.Error.WriteLine($"[DEBUG_BULK]   Error in container[{ci}] ({reader.ContainerName}): {ex.Message}");
            }
        }
        
        if (debug)
            Console.Error.WriteLine($"[DEBUG_BULK]   Not found in any container");
        return null;
    }
    
    /// <summary>
    /// Resolve a package import to the actual export in the referenced package
    /// </summary>
    public (FZenPackageHeader? Package, FExportMapEntry? Export) ResolvePackageImport(
        FZenPackageHeader sourcePackage, 
        FPackageObjectIndex importIndex)
    {
        if (!importIndex.IsPackageImport())
            return (null, null);
        
        var (packageIndex, exportHashIndex) = importIndex.GetPackageImport();
        
        // Get the imported package ID
        if (packageIndex >= sourcePackage.ImportedPackages.Count)
            return (null, null);
        
        ulong importedPackageId = sourcePackage.ImportedPackages[packageIndex];
        
        // Load the target package
        var targetPackage = GetPackage(importedPackageId);
        if (targetPackage == null)
            return (null, null);
        
        // Find the export by its public hash
        if (exportHashIndex >= sourcePackage.ImportedPublicExportHashes.Count)
            return (targetPackage, null);
        
        ulong exportHash = sourcePackage.ImportedPublicExportHashes[exportHashIndex];
        
        // Search for export with matching hash
        // In UE5.3+ (NoExportInfo), FExportMapEntry.PublicExportHash is a GlobalImportIndex,
        // not a CityHash64 hash. ImportedPublicExportHashes contains CityHash64 of export names.
        // So we must also try CityHash64 of each export's name.
        foreach (var export in targetPackage.ExportMap)
        {
            if (export.PublicExportHash == exportHash)
            {
                return (targetPackage, export);
            }
            // Fallback: compute CityHash64 of export name for UE5.3+ matching
            string expName = targetPackage.GetName(export.ObjectName);
            ulong computedHash = IoStore.CityHash.CityHash64(expName.ToLowerInvariant());
            if (computedHash == exportHash)
            {
                return (targetPackage, export);
            }
        }
        
        return (targetPackage, null);
    }

    /// <summary>
    /// Resolve a script import using the script objects database
    /// </summary>
    public ScriptObjectEntry? ResolveScriptImport(FPackageObjectIndex importIndex)
    {
        if (ScriptObjects == null || !importIndex.IsScriptImport())
            return null;
        
        return ScriptObjects.GetScriptObject(importIndex);
    }

    /// <summary>
    /// Index public export hashes for a package
    /// </summary>
    private void IndexPackageExports(ulong packageId, FZenPackageHeader header)
    {
        if (_packageExportHashes.ContainsKey(packageId))
            return;
        
        var hashes = new List<ulong>();
        foreach (var export in header.ExportMap)
        {
            if (export.PublicExportHash != 0)
            {
                hashes.Add(export.PublicExportHash);
            }
        }
        _packageExportHashes[packageId] = hashes;
    }

    /// <summary>
    /// Preload all packages (useful for batch operations)
    /// </summary>
    public void PreloadAllPackages(IProgress<int>? progress = null)
    {
        int count = 0;
        int total = _packageIdToChunk.Count;
        
        foreach (var packageId in _packageIdToChunk.Keys)
        {
            GetPackage(packageId);
            count++;
            progress?.Report((count * 100) / total);
        }
        
    }

    /// <summary>
    /// Get the export index in a package by its CityHash64(lowerName) hash.
    /// Builds and caches a hash→index dictionary on first call per package.
    /// Returns -1 if not found.
    /// </summary>
    public int GetExportIndexByHash(ulong packageId, ulong exportHash, ScriptObjectsDatabase? scriptObjects)
    {
        var lookup = _exportHashLookup.GetOrAdd(packageId, pkgId =>
        {
            if (!_packageCache.TryGetValue(pkgId, out var cached))
                return new Dictionary<ulong, int>();
            var map = new Dictionary<ulong, int>(cached.Header.ExportMap.Count);
            for (int i = 0; i < cached.Header.ExportMap.Count; i++)
            {
                var exp = cached.Header.ExportMap[i];
                // Direct hash stored on export (older UE5)
                if (exp.PublicExportHash != 0)
                    map.TryAdd(exp.PublicExportHash, i);
                // CityHash64 of lowercase name (UE5.3+ NoExportInfo)
                string name = cached.Header.GetName(exp.ObjectName, scriptObjects);
                ulong computed = IoStore.CityHash.CityHash64(name.ToLowerInvariant());
                map.TryAdd(computed, i);
            }
            return map;
        });
        return lookup.TryGetValue(exportHash, out int idx) ? idx : -1;
    }

    /// <summary>
    /// Clear the package cache to free memory
    /// </summary>
    public void ClearCache()
    {
        _packageCache.Clear();
        _packageExportHashes.Clear();
        _exportHashLookup.Clear();
    }

    public void Dispose()
    {
        foreach (var container in _containers)
        {
            container.Dispose();
        }
        _containers.Clear();
        _packageCache.Clear();
        _packageExportHashes.Clear();
        _exportHashLookup.Clear();
    }
}

/// <summary>
/// Cached package data
/// </summary>
public class CachedPackage
{
    public FZenPackageHeader Header { get; set; } = null!;
    public byte[] RawData { get; set; } = Array.Empty<byte>();
    public ulong PackageId { get; set; }
}
