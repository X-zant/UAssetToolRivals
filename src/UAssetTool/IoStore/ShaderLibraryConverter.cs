using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;

namespace UAssetTool.IoStore;

/// <summary>
/// Converts legacy .ushaderbytecode files to IoStore ShaderCodeLibrary + ShaderCode chunks.
/// Reference: retoc/src/shader_library.rs write_io_store_library()
/// </summary>
public static class ShaderLibraryConverter
{
    private const int MAX_SHADER_GROUP_SIZE = 1024 * 1024; // 1MB max uncompressed group size (UE default)
    private const int SHADER_LIBRARY_VERSION = 2; // GShaderArchiveVersion, UE4.24+

    #region Legacy Format Structures

    /// <summary>
    /// Legacy shader map entry (16 bytes).
    /// Reference: retoc/src/shader_library.rs FShaderMapEntry
    /// </summary>
    private struct FShaderMapEntry
    {
        public uint ShaderIndicesOffset;
        public uint NumShaders;
        public uint FirstPreloadIndex;
        public uint NumPreloadEntries;
    }

    /// <summary>
    /// Legacy shader code entry (17 bytes).
    /// Reference: retoc/src/shader_library.rs FShaderCodeEntry
    /// </summary>
    private struct FShaderCodeEntry
    {
        public ulong Offset;          // Relative to end of header
        public uint Size;             // Compressed size
        public uint UncompressedSize;
        public byte Frequency;
    }

    /// <summary>
    /// Legacy preload entry (16 bytes).
    /// Reference: retoc/src/shader_library.rs FFileCachePreloadEntry
    /// </summary>
    private struct FFileCachePreloadEntry
    {
        public long Offset;
        public long Size;
    }

    /// <summary>
    /// Parsed legacy shader library header.
    /// </summary>
    private class FShaderLibraryHeader
    {
        public byte[][] ShaderMapHashes = Array.Empty<byte[]>(); // 20 bytes each (SHA1)
        public byte[][] ShaderHashes = Array.Empty<byte[]>();    // 20 bytes each (SHA1)
        public FShaderMapEntry[] ShaderMapEntries = Array.Empty<FShaderMapEntry>();
        public FShaderCodeEntry[] ShaderEntries = Array.Empty<FShaderCodeEntry>();
        public FFileCachePreloadEntry[] PreloadEntries = Array.Empty<FFileCachePreloadEntry>();
        public uint[] ShaderIndices = Array.Empty<uint>();
    }

    #endregion

    #region IoStore Format Structures

    /// <summary>
    /// IoStore shader map entry (8 bytes).
    /// Reference: retoc/src/shader_library.rs FIoStoreShaderMapEntry
    /// </summary>
    private struct FIoStoreShaderMapEntry
    {
        public uint ShaderIndicesOffset;
        public uint NumShaders;
    }

    /// <summary>
    /// IoStore shader code entry (packed u64).
    /// Reference: retoc/src/shader_library.rs FIoStoreShaderCodeEntry
    /// </summary>
    private struct FIoStoreShaderCodeEntry
    {
        public ulong Packed;

        private const int FREQUENCY_BITS = 4;
        private const int FREQUENCY_SHIFT = 0;
        private const ulong FREQUENCY_MASK = (1UL << FREQUENCY_BITS) - 1;

        private const int GROUP_INDEX_SHIFT = FREQUENCY_SHIFT + FREQUENCY_BITS;
        private const int GROUP_INDEX_BITS = 30;
        private const ulong GROUP_INDEX_MASK = (1UL << GROUP_INDEX_BITS) - 1;

        private const int OFFSET_SHIFT = GROUP_INDEX_SHIFT + GROUP_INDEX_BITS;
        private const int OFFSET_BITS = 30;
        private const ulong OFFSET_MASK = (1UL << OFFSET_BITS) - 1;

        public byte ShaderFrequency => (byte)((Packed >> FREQUENCY_SHIFT) & FREQUENCY_MASK);
        public int ShaderGroupIndex => (int)((Packed >> GROUP_INDEX_SHIFT) & GROUP_INDEX_MASK);
        public int ShaderUncompressedOffsetInGroup => (int)((Packed >> OFFSET_SHIFT) & OFFSET_MASK);

        public static FIoStoreShaderCodeEntry Create(int groupIndex, int offsetInGroup, byte frequency)
        {
            ulong packed = ((ulong)(frequency & FREQUENCY_MASK) << FREQUENCY_SHIFT)
                | (((ulong)groupIndex & GROUP_INDEX_MASK) << GROUP_INDEX_SHIFT)
                | (((ulong)offsetInGroup & OFFSET_MASK) << OFFSET_SHIFT);
            return new FIoStoreShaderCodeEntry { Packed = packed };
        }
    }

    /// <summary>
    /// IoStore shader group entry (16 bytes).
    /// Reference: retoc/src/shader_library.rs FIoStoreShaderGroupEntry
    /// </summary>
    private struct FIoStoreShaderGroupEntry
    {
        public uint ShaderIndicesOffset;
        public uint NumShaders;
        public uint UncompressedSize;
        public uint CompressedSize;
    }

    #endregion

    #region Reading Legacy Format

    private static byte[][] ReadHashArray(BinaryReader reader)
    {
        uint count = reader.ReadUInt32();
        var hashes = new byte[count][];
        for (int i = 0; i < count; i++)
            hashes[i] = reader.ReadBytes(20);
        return hashes;
    }

    private static FShaderMapEntry[] ReadShaderMapEntries(BinaryReader reader)
    {
        uint count = reader.ReadUInt32();
        var entries = new FShaderMapEntry[count];
        for (int i = 0; i < count; i++)
        {
            entries[i] = new FShaderMapEntry
            {
                ShaderIndicesOffset = reader.ReadUInt32(),
                NumShaders = reader.ReadUInt32(),
                FirstPreloadIndex = reader.ReadUInt32(),
                NumPreloadEntries = reader.ReadUInt32()
            };
        }
        return entries;
    }

    private static FShaderCodeEntry[] ReadShaderCodeEntries(BinaryReader reader)
    {
        uint count = reader.ReadUInt32();
        var entries = new FShaderCodeEntry[count];
        for (int i = 0; i < count; i++)
        {
            entries[i] = new FShaderCodeEntry
            {
                Offset = reader.ReadUInt64(),
                Size = reader.ReadUInt32(),
                UncompressedSize = reader.ReadUInt32(),
                Frequency = reader.ReadByte()
            };
        }
        return entries;
    }

    private static FFileCachePreloadEntry[] ReadPreloadEntries(BinaryReader reader)
    {
        uint count = reader.ReadUInt32();
        var entries = new FFileCachePreloadEntry[count];
        for (int i = 0; i < count; i++)
        {
            entries[i] = new FFileCachePreloadEntry
            {
                Offset = reader.ReadInt64(),
                Size = reader.ReadInt64()
            };
        }
        return entries;
    }

    private static uint[] ReadShaderIndices(BinaryReader reader)
    {
        uint count = reader.ReadUInt32();
        var indices = new uint[count];
        for (int i = 0; i < count; i++)
            indices[i] = reader.ReadUInt32();
        return indices;
    }

    private static FShaderLibraryHeader ReadLegacyHeader(BinaryReader reader)
    {
        return new FShaderLibraryHeader
        {
            ShaderMapHashes = ReadHashArray(reader),
            ShaderHashes = ReadHashArray(reader),
            ShaderMapEntries = ReadShaderMapEntries(reader),
            ShaderEntries = ReadShaderCodeEntries(reader),
            PreloadEntries = ReadPreloadEntries(reader),
            ShaderIndices = ReadShaderIndices(reader)
        };
    }

    /// <summary>
    /// The same library restricted to <paramref name="keepShaderMaps"/> (hex shader map hashes) and the
    /// shaders they use. Shader entries keep their offsets into the original code section; shader
    /// indices are renumbered. A cooked library holds every shader map of the cook (the whole project,
    /// engine content included), while a mod only needs the ones its own packages reference.
    /// </summary>
    private static FShaderLibraryHeader FilterHeader(FShaderLibraryHeader h, ISet<string> keepShaderMaps)
    {
        var keptMaps = new List<int>();
        for (int m = 0; m < h.ShaderMapHashes.Length; m++)
            if (keepShaderMaps.Contains(Convert.ToHexString(h.ShaderMapHashes[m]))) keptMaps.Add(m);

        var newIndexOf = new Dictionary<uint, uint>();
        var keptShaders = new List<uint>();
        var indices = new List<uint>();
        var mapEntries = new List<FShaderMapEntry>();
        foreach (int m in keptMaps)
        {
            var e = h.ShaderMapEntries[m];
            uint start = (uint)indices.Count;
            for (uint i = 0; i < e.NumShaders; i++)
            {
                uint old = h.ShaderIndices[e.ShaderIndicesOffset + i];
                if (!newIndexOf.TryGetValue(old, out uint idx))
                {
                    idx = (uint)keptShaders.Count;
                    newIndexOf[old] = idx;
                    keptShaders.Add(old);
                }
                indices.Add(idx);
            }
            mapEntries.Add(new FShaderMapEntry { ShaderIndicesOffset = start, NumShaders = e.NumShaders });
        }
        return new FShaderLibraryHeader
        {
            ShaderMapHashes = keptMaps.Select(m => h.ShaderMapHashes[m]).ToArray(),
            ShaderHashes = keptShaders.Select(s => h.ShaderHashes[s]).ToArray(),
            ShaderMapEntries = mapEntries.ToArray(),
            ShaderEntries = keptShaders.Select(s => h.ShaderEntries[s]).ToArray(),
            PreloadEntries = Array.Empty<FFileCachePreloadEntry>(),   // not used by the IoStore format
            ShaderIndices = indices.ToArray(),
        };
    }

    #endregion

    #region Shader Grouping (retoc algorithm)

    /// <summary>
    /// Groups shaders by which shader maps reference them, then splits groups by size.
    /// Reference: retoc/src/shader_library.rs build_io_store_shader_code_archive_header()
    /// </summary>
    private static List<List<int>> BuildShaderGroups(FShaderLibraryHeader header)
    {
        int totalShaders = header.ShaderHashes.Length;

        // Track which shader maps reference each shader
        var shaderToMaps = new Dictionary<int, List<int>>();
        for (int mapIdx = 0; mapIdx < header.ShaderMapEntries.Length; mapIdx++)
        {
            var mapEntry = header.ShaderMapEntries[mapIdx];
            for (uint i = 0; i < mapEntry.NumShaders; i++)
            {
                int indicesIdx = (int)(mapEntry.ShaderIndicesOffset + i);
                int shaderIdx = (int)header.ShaderIndices[indicesIdx];
                if (!shaderToMaps.ContainsKey(shaderIdx))
                    shaderToMaps[shaderIdx] = new List<int>();
                shaderToMaps[shaderIdx].Add(mapIdx);
            }
        }

        // Add empty lists for shaders not referenced by any map
        for (int i = 0; i < totalShaders; i++)
        {
            if (!shaderToMaps.ContainsKey(i))
                shaderToMaps[i] = new List<int>();
        }

        // Sort each shader's map list
        foreach (var kv in shaderToMaps)
            kv.Value.Sort();

        // Sort shaders by: map count, then map IDs, then shader index
        var sortedShaders = shaderToMaps.Keys.ToList();
        sortedShaders.Sort((a, b) =>
        {
            var mapsA = shaderToMaps[a];
            var mapsB = shaderToMaps[b];
            if (mapsA.Count != mapsB.Count)
                return mapsA.Count.CompareTo(mapsB.Count);
            for (int i = 0; i < Math.Min(mapsA.Count, mapsB.Count); i++)
            {
                if (mapsA[i] != mapsB[i])
                    return mapsA[i].CompareTo(mapsB[i]);
            }
            return a.CompareTo(b);
        });

        // Split into streaks of shaders referenced by the same set of maps
        var groups = new List<List<int>>();
        var currentGroup = new List<int>();
        List<int>? lastMapSet = null;

        foreach (int shaderIdx in sortedShaders)
        {
            var maps = shaderToMaps[shaderIdx];
            if (currentGroup.Count == 0)
            {
                currentGroup.Add(shaderIdx);
                lastMapSet = maps;
            }
            else if (!maps.SequenceEqual(lastMapSet!))
            {
                groups.Add(currentGroup);
                currentGroup = new List<int> { shaderIdx };
                lastMapSet = maps;
            }
            else
            {
                currentGroup.Add(shaderIdx);
            }
        }
        if (currentGroup.Count > 0)
            groups.Add(currentGroup);

        // Split large groups by size
        var finalGroups = new List<List<int>>();
        foreach (var group in groups)
        {
            long groupSize = group.Sum(i => (long)header.ShaderEntries[i].UncompressedSize);
            if (groupSize <= MAX_SHADER_GROUP_SIZE || group.Count == 1)
            {
                finalGroups.Add(group);
                continue;
            }

            int numNewGroups = (int)Math.Min(groupSize / MAX_SHADER_GROUP_SIZE + 1, group.Count);

            // Sort descending by uncompressed size for bin-packing
            var sorted = group.OrderByDescending(i => header.ShaderEntries[i].UncompressedSize).ToList();
            var newGroups = new List<int>[numNewGroups];
            var newGroupSizes = new long[numNewGroups];
            for (int i = 0; i < numNewGroups; i++)
                newGroups[i] = new List<int>();

            foreach (int shaderIdx in sorted)
            {
                // Find smallest group
                int smallest = 0;
                for (int i = 1; i < numNewGroups; i++)
                {
                    if (newGroupSizes[i] < newGroupSizes[smallest])
                        smallest = i;
                }
                newGroups[smallest].Add(shaderIdx);
                newGroupSizes[smallest] += header.ShaderEntries[shaderIdx].UncompressedSize;
            }

            foreach (var ng in newGroups)
            {
                if (ng.Count > 0)
                    finalGroups.Add(ng);
            }
        }

        // Sort shaders within each group ascending by (uncompressedSize, size, frequency, offset)
        foreach (var group in finalGroups)
        {
            group.Sort((a, b) =>
            {
                var ea = header.ShaderEntries[a];
                var eb = header.ShaderEntries[b];
                int c = ea.UncompressedSize.CompareTo(eb.UncompressedSize);
                if (c != 0) return c;
                c = ea.Size.CompareTo(eb.Size);
                if (c != 0) return c;
                c = ea.Frequency.CompareTo(eb.Frequency);
                if (c != 0) return c;
                return ea.Offset.CompareTo(eb.Offset);
            });
        }

        return finalGroups;
    }

    /// <summary>
    /// Find or add a shader index sequence in the shader indices array.
    /// Reference: retoc/src/shader_library.rs find_or_add_sequence_in_shader_indices()
    /// </summary>
    private static int FindOrAddSequence(List<uint> shaderIndices, List<int> groupIndices)
    {
        int first = groupIndices[0];
        int seqLen = groupIndices.Count;
        int maxStart = shaderIndices.Count - seqLen + 1;

        for (int start = 0; start < maxStart; start++)
        {
            if ((int)shaderIndices[start] != first) continue;
            bool match = true;
            for (int j = 1; j < seqLen; j++)
            {
                if ((int)shaderIndices[start + j] != groupIndices[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return start;
        }

        // Add new sequence
        int newStart = shaderIndices.Count;
        foreach (int idx in groupIndices)
            shaderIndices.Add((uint)idx);
        return newStart;
    }

    #endregion

    #region Writing IoStore Header

    private static void WriteUInt32Array(BinaryWriter writer, uint[] values)
    {
        writer.Write(values.Length);
        foreach (uint v in values) writer.Write(v);
    }

    private static void WriteHashArray(BinaryWriter writer, byte[][] hashes)
    {
        writer.Write(hashes.Length);
        foreach (var h in hashes) writer.Write(h);
    }

    #endregion

    #region Package Shader Map Associations

    /// <summary>
    /// Which shader maps a cooked package uses, for the package store entries' ShaderMapHashes (the async loader
    /// preloads exactly these before the package's exports: AsyncLoading2.cpp, PreloadPackageShaderMaps).
    ///
    /// Stock IoStoreUtilities takes them from the cooker's ShaderAssetInfo json, which UE's packaging does not put into
    /// .pak files. A cooked material resource stores its shader map hash (the library key, 20 bytes) inline, so every
    /// library map hash found in the package's bytes is one it uses.
    /// </summary>
    /// <returns>All shader map hashes of the given libraries: the candidates for <see cref="FindShaderMapsInPackage"/>.</returns>
    public static List<byte[]> ShaderMapsOfLibraries(IEnumerable<string> shaderLibraryFiles)
    {
        var hashes = new List<byte[]>();
        foreach (var libraryFile in shaderLibraryFiles)
        {
            if (!libraryFile.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase)) continue;
            using var reader = new BinaryReader(File.OpenRead(libraryFile));
            if (reader.ReadInt32() != SHADER_LIBRARY_VERSION) continue;
            hashes.AddRange(ReadLegacyHeader(reader).ShaderMapHashes);
        }
        return hashes;
    }

    /// <summary>The shader maps (of <paramref name="candidates"/>) whose hash occurs in the package's bytes, sorted as stock sorts them.</summary>
    public static List<byte[]> FindShaderMapsInPackage(string uassetPath, IReadOnlyList<byte[]> candidates)
    {
        var found = new List<byte[]>();
        if (candidates.Count == 0) return found;
        foreach (var path in new[] { Path.ChangeExtension(uassetPath, ".uexp"), uassetPath })
        {
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            foreach (var h in candidates)
                if (!found.Any(f => f.AsSpan().SequenceEqual(h)) && bytes.AsSpan().IndexOf(h) >= 0) found.Add(h);
        }
        found.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        if (found.Count > 0)
            Console.Error.WriteLine($"[ShaderLib] {Path.GetFileNameWithoutExtension(uassetPath)}: {found.Count} shader map(s) ({string.Join(", ", found.Select(h => Convert.ToHexString(h)[..8]))})");
        return found;
    }

    #endregion

    #region Main Conversion

    /// <summary>
    /// Convert a legacy .ushaderbytecode file and write its ShaderCodeLibrary + ShaderCode chunks to the IoStoreWriter.
    /// </summary>
    /// <param name="ushaderbytecodeData">Raw bytes of the .ushaderbytecode file</param>
    /// <param name="shaderLibraryPath">UE path, e.g. "Marvel/Content/ShaderArchive-Marvel_Chunk0-PCD3D_SM6-PCD3D_SM6.ushaderbytecode"</param>
    /// <param name="writer">IoStoreWriter to write chunks into</param>
    /// <param name="keepShaderMaps">When given, only these shader maps (hex hashes) and their shaders are written.</param>
    public static void ConvertAndWrite(byte[] ushaderbytecodeData, string shaderLibraryPath, IoStoreWriter writer, ISet<string>? keepShaderMaps = null)
    {
        using var ms = new MemoryStream(ushaderbytecodeData);
        using var reader = new BinaryReader(ms);

        // Read version
        int version = reader.ReadInt32();
        if (version != SHADER_LIBRARY_VERSION)
            throw new InvalidDataException($"Unknown shader library version {version}. Expected {SHADER_LIBRARY_VERSION}");

        // Read legacy header
        var header = ReadLegacyHeader(reader);
        long shaderCodeStartOffset = ms.Position;
        if (keepShaderMaps != null)
        {
            int before = header.ShaderMapHashes.Length, shadersBefore = header.ShaderHashes.Length;
            header = FilterHeader(header, keepShaderMaps);
            Console.Error.WriteLine($"[ShaderLib] Keeping the {header.ShaderMapHashes.Length} of {before} shader maps the packaged packages use ({header.ShaderHashes.Length} of {shadersBefore} shaders)");
        }

        Console.Error.WriteLine($"[ShaderLib] Parsing {shaderLibraryPath}: {header.ShaderHashes.Length} shaders, {header.ShaderMapHashes.Length} shader maps");

        // Parse library name and format name from filename
        // e.g. "ShaderArchive-Marvel_Chunk0-PCD3D_SM6-PCD3D_SM6.ushaderbytecode"
        string filename = Path.GetFileNameWithoutExtension(shaderLibraryPath);
        int firstDash = filename.IndexOf('-');
        int lastDash = filename.LastIndexOf('-');
        if (firstDash < 0 || lastDash < 0 || firstDash == lastDash)
            throw new InvalidDataException($"Invalid shader library filename: {filename}");

        // retoc note: the splitting logic is "wrong" to match UnrealPak behavior
        // library_name = everything between "ShaderArchive-" and last "-"
        // format_name = everything after last "-"
        string libraryName = filename.Substring(firstDash + 1, lastDash - firstDash - 1);
        string formatName = filename.Substring(lastDash + 1);

        Console.Error.WriteLine($"[ShaderLib] Library name: {libraryName}, Format: {formatName}");

        // Read and decompress all individual shaders from the legacy file
        var decompressedShaders = new byte[header.ShaderEntries.Length][];
        long totalUncompressedSize = 0;
        string? detectedCompression = null;

        for (int i = 0; i < header.ShaderEntries.Length; i++)
        {
            var entry = header.ShaderEntries[i];
            ms.Position = shaderCodeStartOffset + (long)entry.Offset;
            byte[] compressedData = reader.ReadBytes((int)entry.Size);

            if (entry.Size != entry.UncompressedSize)
            {
                // Auto-detect compression method (matching retoc's logic)
                byte[]? uncompressed = null;

                // Try Oodle first (magic byte 0x8C)
                if (uncompressed == null && compressedData.Length > 0 && compressedData[0] == 0x8C)
                {
                    uncompressed = OodleCompression.Decompress(compressedData, (int)entry.UncompressedSize);
                    if (uncompressed != null) detectedCompression ??= "Oodle";
                }

                // Try Zstd (magic bytes xx B5 2F FD)
                if (uncompressed == null && compressedData.Length >= 4 && compressedData[1] == 0xB5 && compressedData[2] == 0x2F && compressedData[3] == 0xFD)
                {
                    // Zstd not currently supported - skip
                }

                // Try LZ4 as fallback (no reliable magic)
                if (uncompressed == null)
                {
                    try
                    {
                        var lz4Buffer = new byte[entry.UncompressedSize];
                        int decoded = LZ4Codec.Decode(compressedData, 0, compressedData.Length,
                            lz4Buffer, 0, lz4Buffer.Length);
                        if (decoded == (int)entry.UncompressedSize)
                        {
                            uncompressed = lz4Buffer;
                            detectedCompression ??= "LZ4";
                        }
                    }
                    catch { /* LZ4 failed, try next */ }
                }

                // Try Oodle as last resort if magic didn't match
                if (uncompressed == null && OodleCompression.IsAvailable)
                {
                    uncompressed = OodleCompression.Decompress(compressedData, (int)entry.UncompressedSize);
                    if (uncompressed != null) detectedCompression ??= "Oodle";
                }

                if (uncompressed == null)
                    throw new InvalidDataException($"Failed to decompress shader {i} (size={entry.Size}, uncompressed={entry.UncompressedSize}, first byte=0x{compressedData[0]:X2})");

                decompressedShaders[i] = uncompressed;
            }
            else
            {
                decompressedShaders[i] = compressedData;
            }
            totalUncompressedSize += decompressedShaders[i].Length;
        }

        Console.Error.WriteLine($"[ShaderLib] Decompressed {header.ShaderEntries.Length} shaders ({totalUncompressedSize / 1024}KB total, compression={detectedCompression ?? "none"})");

        // Build shader groups
        var groups = BuildShaderGroups(header);
        Console.Error.WriteLine($"[ShaderLib] Built {groups.Count} shader groups");

        // Build IoStore shader code entries (one per shader)
        var ioShaderEntries = new FIoStoreShaderCodeEntry[header.ShaderEntries.Length];

        // Build shader indices list (start from copy of original, may grow)
        var ioShaderIndices = new List<uint>(header.ShaderIndices);

        // Build shader map entries
        var ioShaderMapEntries = new FIoStoreShaderMapEntry[header.ShaderMapEntries.Length];
        for (int i = 0; i < header.ShaderMapEntries.Length; i++)
        {
            ioShaderMapEntries[i] = new FIoStoreShaderMapEntry
            {
                ShaderIndicesOffset = header.ShaderMapEntries[i].ShaderIndicesOffset,
                NumShaders = header.ShaderMapEntries[i].NumShaders
            };
        }

        // Build group entries, chunk IDs, and write shader code chunks
        var ioGroupEntries = new FIoStoreShaderGroupEntry[groups.Count];
        var groupChunkIds = new byte[groups.Count][];  // 12-byte raw chunk IDs
        long totalCompressedGroupsSize = 0;

        for (int gIdx = 0; gIdx < groups.Count; gIdx++)
        {
            var group = groups[gIdx];
            int uncompressedGroupSize = 0;

            // Populate shader entries with group index and offset
            foreach (int shaderIdx in group)
            {
                ioShaderEntries[shaderIdx] = FIoStoreShaderCodeEntry.Create(
                    gIdx,
                    uncompressedGroupSize,
                    header.ShaderEntries[shaderIdx].Frequency
                );
                uncompressedGroupSize += decompressedShaders[shaderIdx].Length;
            }

            // Compute group SHA1 hash (shader hash + uncompressed size for each shader, then format name)
            using var sha1 = SHA1.Create();
            using var hashStream = new MemoryStream();
            foreach (int shaderIdx in group)
            {
                hashStream.Write(header.ShaderHashes[shaderIdx], 0, 20);
                hashStream.Write(BitConverter.GetBytes(header.ShaderEntries[shaderIdx].UncompressedSize), 0, 4);
            }
            hashStream.Write(Encoding.UTF8.GetBytes(formatName));
            hashStream.Position = 0;
            byte[] groupHash = sha1.ComputeHash(hashStream);

            // Build uncompressed group buffer
            var groupBuffer = new byte[uncompressedGroupSize];
            int offset = 0;
            foreach (int shaderIdx in group)
            {
                Array.Copy(decompressedShaders[shaderIdx], 0, groupBuffer, offset, decompressedShaders[shaderIdx].Length);
                offset += decompressedShaders[shaderIdx].Length;
            }

            // Try Oodle compression (matching the IoStoreWriter's approach)
            byte[] chunkData = groupBuffer;
            if (OodleCompression.IsAvailable)
            {
                var compressed = OodleCompression.Compress(groupBuffer, OodleCompressor.Kraken, OodleCompressionLevel.Normal);
                if (compressed != null && compressed.Length < groupBuffer.Length)
                    chunkData = compressed;
            }

            int compressedSize = chunkData.Length;
            totalCompressedGroupsSize += compressedSize;

            // Find or add shader indices sequence for this group
            int indicesOffset = FindOrAddSequence(ioShaderIndices, group);

            ioGroupEntries[gIdx] = new FIoStoreShaderGroupEntry
            {
                ShaderIndicesOffset = (uint)indicesOffset,
                NumShaders = (uint)group.Count,
                UncompressedSize = (uint)uncompressedGroupSize,
                CompressedSize = (uint)compressedSize
            };

            // Create chunk ID from SHA1 hash
            var shaderCodeChunkId = FIoChunkId.CreateShaderCodeChunkId(groupHash);
            groupChunkIds[gIdx] = shaderCodeChunkId.ToBytes();

            // Write the shader code chunk (raw, no directory index path)
            // Note: IoStoreWriter.WriteChunk will apply its own Oodle+AES on top of the 128KB blocks.
            // We need to write the UNCOMPRESSED group data and let IoStoreWriter handle block compression.
            // The compressed_size in the IoStore header refers to per-group compression, NOT per-block.
            // Actually, in IoStore format the group data is stored as-is in the chunk.
            // The IoStore block compression is orthogonal to per-group compression.
            writer.WriteRawChunk(shaderCodeChunkId, chunkData);
        }

        // Serialize the IoStore shader library header
        using var headerMs = new MemoryStream();
        using var headerWriter = new BinaryWriter(headerMs);

        // Version
        headerWriter.Write((uint)1); // EIoStoreShaderLibraryVersion::Initial

        // FIoStoreShaderCodeArchiveHeader
        // ShaderMapHashes
        headerWriter.Write(header.ShaderMapHashes.Length);
        foreach (var h in header.ShaderMapHashes) headerWriter.Write(h);

        // ShaderHashes
        headerWriter.Write(header.ShaderHashes.Length);
        foreach (var h in header.ShaderHashes) headerWriter.Write(h);

        // ShaderGroupChunkIds (12 bytes each, raw)
        headerWriter.Write(groups.Count);
        foreach (var raw in groupChunkIds) headerWriter.Write(raw);

        // ShaderMapEntries
        headerWriter.Write(ioShaderMapEntries.Length);
        foreach (var e in ioShaderMapEntries)
        {
            headerWriter.Write(e.ShaderIndicesOffset);
            headerWriter.Write(e.NumShaders);
        }

        // ShaderEntries (packed u64 each)
        headerWriter.Write(ioShaderEntries.Length);
        foreach (var e in ioShaderEntries) headerWriter.Write(e.Packed);

        // ShaderGroupEntries
        headerWriter.Write(ioGroupEntries.Length);
        foreach (var e in ioGroupEntries)
        {
            headerWriter.Write(e.ShaderIndicesOffset);
            headerWriter.Write(e.NumShaders);
            headerWriter.Write(e.UncompressedSize);
            headerWriter.Write(e.CompressedSize);
        }

        // ShaderIndices
        headerWriter.Write(ioShaderIndices.Count);
        foreach (uint idx in ioShaderIndices) headerWriter.Write(idx);

        byte[] headerData = headerMs.ToArray();

        // Create the ShaderCodeLibrary chunk ID
        var libraryChunkId = FIoChunkId.CreateShaderLibraryChunkId(libraryName, formatName);

        // The directory index keeps the .ushaderbytecode extension. Stock UnrealPak writes the
        // cooked file's own path (IoStoreUtilities.cpp: WriteOptions.FileName = DestinationPath),
        // and the game's pakchunkShaderAsset lists
        // ../../../Marvel/Content/ShaderArchive-Marvel_Chunk0-PCD3D_SM6-PCD3D_SM6.ushaderbytecode.
        // retoc strips it; anything that finds libraries by file name would then miss ours.
        string libraryPath = shaderLibraryPath.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase)
            ? shaderLibraryPath
            : shaderLibraryPath + ".ushaderbytecode";
        writer.WriteChunk(libraryChunkId, libraryPath, headerData);

        // Stats
        long totalCompressedLegacy = ushaderbytecodeData.Length - shaderCodeStartOffset;
        double ratio = totalCompressedGroupsSize > 0
            ? Math.Round((double)totalUncompressedSize / totalCompressedGroupsSize * 100)
            : 0;

        Console.Error.WriteLine($"[ShaderLib] Shader Library {filename} statistics: " +
            $"Shader Groups: {groups.Count}, Shader Maps: {header.ShaderMapHashes.Length}, " +
            $"Uncompressed Size: {totalUncompressedSize / 1024}KB, " +
            $"Original Compressed Size: {totalCompressedLegacy / 1024}KB, " +
            $"Total Group Compressed Size: {totalCompressedGroupsSize / 1024}KB, " +
            $"Compression Ratio: {ratio}%");
    }

    #endregion
}
