using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UAssetTool.IoStore;

/// <summary>
/// IoStore container reader - reads and extracts chunks from IoStore containers (.utoc/.ucas)
/// Translated from retoc-rivals/src/iostore.rs and lib.rs
/// </summary>
public class IoStoreReader : IDisposable
{
    private const string DEFAULT_AES_KEY_HEX = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";
    
    private readonly string _containerName;
    private readonly string _utocPath;
    private readonly string _ucasPath;
    private readonly IoStoreToc _toc;
    private FileStream? _casStream;
    private SafeFileHandle? _casHandle; // set once CAS is opened; enables thread-safe RandomAccess reads
    private readonly object _casOpenLock = new();
    private readonly byte[]? _aesKey;

    public string ContainerName => _containerName;
    public IoStoreToc Toc => _toc;
    
    /// <summary>
    /// Check if an IoStore container is compressed (has any compression blocks with non-zero compression method)
    /// Equivalent to retoc::is_iostore_compressed()
    /// </summary>
    public static bool IsCompressed(string utocPath)
    {
        using var stream = new FileStream(utocPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        // Read header to get compression info
        reader.ReadBytes(16); // magic
        byte version = reader.ReadByte();
        reader.ReadByte(); // reserved0
        reader.ReadUInt16(); // reserved1
        reader.ReadUInt32(); // tocHeaderSize
        uint tocEntryCount = reader.ReadUInt32();
        uint tocCompressedBlockEntryCount = reader.ReadUInt32();
        reader.ReadUInt32(); // tocCompressedBlockEntrySize
        uint compressionMethodNameCount = reader.ReadUInt32();

        // If there are compression methods registered and blocks exist, check if any are compressed
        if (compressionMethodNameCount == 0 || tocCompressedBlockEntryCount == 0)
            return false;

        // Skip rest of header and chunk data to get to compression blocks
        reader.ReadUInt32(); // compressionMethodNameLength
        reader.ReadUInt32(); // compressionBlockSize
        reader.ReadUInt32(); // directoryIndexSize
        reader.ReadUInt32(); // partitionCount
        reader.ReadUInt64(); // containerId
        reader.ReadBytes(16); // encryptionKeyGuid
        reader.ReadByte(); // containerFlags
        reader.ReadByte(); // reserved3
        reader.ReadUInt16(); // reserved4
        uint tocChunkPerfectHashSeedsCount = reader.ReadUInt32();
        reader.ReadUInt64(); // partitionSize
        uint tocChunksWithoutPerfectHashCount = reader.ReadUInt32();
        reader.ReadUInt32(); // reserved7
        reader.ReadBytes(40); // reserved8

        // Skip chunk IDs (12 bytes each)
        stream.Seek(tocEntryCount * 12, SeekOrigin.Current);
        // Skip offset/lengths (10 bytes each)
        stream.Seek(tocEntryCount * 10, SeekOrigin.Current);
        // Skip perfect hash data if present
        if (version >= 3) // PerfectHashWithOverflow
        {
            stream.Seek(tocChunkPerfectHashSeedsCount * 4, SeekOrigin.Current);
            stream.Seek(tocChunksWithoutPerfectHashCount * 4, SeekOrigin.Current);
        }

        // Read compression blocks and check if any have non-zero compression method
        for (int i = 0; i < Math.Min(tocCompressedBlockEntryCount, 100); i++) // Check first 100 blocks
        {
            byte[] blockData = reader.ReadBytes(12);
            byte compressionMethod = blockData[11];
            if (compressionMethod != 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if an IoStore container is encrypted (obfuscated).
    /// Reads the containerFlags byte from the UTOC header.
    /// </summary>
    public static bool IsEncrypted(string utocPath)
    {
        using var stream = new FileStream(utocPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        // Read header to get container flags
        reader.ReadBytes(16); // magic
        reader.ReadByte(); // version
        reader.ReadByte(); // reserved0
        reader.ReadUInt16(); // reserved1
        reader.ReadUInt32(); // tocHeaderSize
        reader.ReadUInt32(); // tocEntryCount
        reader.ReadUInt32(); // tocCompressedBlockEntryCount
        reader.ReadUInt32(); // tocCompressedBlockEntrySize
        reader.ReadUInt32(); // compressionMethodNameCount
        reader.ReadUInt32(); // compressionMethodNameLength
        reader.ReadUInt32(); // compressionBlockSize
        reader.ReadUInt32(); // directoryIndexSize
        reader.ReadUInt32(); // partitionCount
        reader.ReadUInt64(); // containerId
        reader.ReadBytes(16); // encryptionKeyGuid
        byte containerFlags = reader.ReadByte();

        // EIoContainerFlags::Encrypted = 0x02
        return (containerFlags & 0x02) != 0;
    }

    /// <summary>
    /// Extract ScriptObjects.bin from game IoStore containers
    /// Equivalent to retoc::extract_script_objects()
    /// </summary>
    public static byte[]? ExtractScriptObjects(string paksPath, string? aesKeyHex = null)
    {
        aesKeyHex ??= DEFAULT_AES_KEY_HEX;
        byte[] aesKey = ParseAesKey(aesKeyHex);

        // Find all .utoc files in the paks directory
        var utocFiles = Directory.GetFiles(paksPath, "*.utoc", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f)
            .ToList();

        // ScriptObjects chunk type is 11 (EIoChunkType::ScriptObjects)
        foreach (var utocPath in utocFiles)
        {
            try
            {
                using var reader = new IoStoreReader(utocPath, aesKey);
                
                // Look for ScriptObjects chunk (type 11)
                foreach (var chunk in reader.GetChunks())
                {
                    if (chunk.ChunkType == EIoChunkType.ScriptObjects)
                    {
                        Console.Error.WriteLine($"[ExtractScriptObjects] Found ScriptObjects in {Path.GetFileName(utocPath)}");
                        return reader.ReadChunk(chunk);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ExtractScriptObjects] Error reading {Path.GetFileName(utocPath)}: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Parse AES key from hex string
    /// </summary>
    internal static byte[] ParseAesKey(string hex)
    {
        hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    public IoStoreReader(string utocPath, byte[]? aesKey = null)
    {
        _utocPath = utocPath;
        _ucasPath = Path.ChangeExtension(utocPath, ".ucas");
        _containerName = Path.GetFileNameWithoutExtension(utocPath);
        _aesKey = aesKey;

        if (!File.Exists(_utocPath))
            throw new FileNotFoundException($"TOC file not found: {_utocPath}");
        if (!File.Exists(_ucasPath))
            throw new FileNotFoundException($"CAS file not found: {_ucasPath}");

        _toc = IoStoreToc.Read(_utocPath, _aesKey);
    }

    /// <summary>
    /// Create an IoStoreReader with multiple AES keys to try.
    /// The first key that successfully decrypts the directory index will be used.
    /// </summary>
    public IoStoreReader(string utocPath, List<byte[]> aesKeys)
    {
        _utocPath = utocPath;
        _ucasPath = Path.ChangeExtension(utocPath, ".ucas");
        _containerName = Path.GetFileNameWithoutExtension(utocPath);

        if (!File.Exists(_utocPath))
            throw new FileNotFoundException($"TOC file not found: {_utocPath}");
        if (!File.Exists(_ucasPath))
            throw new FileNotFoundException($"CAS file not found: {_ucasPath}");

        _toc = IoStoreToc.Read(_utocPath, aesKeys);
        _aesKey = _toc.AesKey; // Use whichever key worked
    }

    /// <summary>
    /// Read a chunk by its chunk ID
    /// </summary>
    /// <summary>
    /// Uncompressed size of a chunk, taken straight from the TOC. Costs no I/O and no
    /// decompression, so it is safe to call for every chunk in a container.
    /// Returns -1 when the chunk is not present.
    /// </summary>
    public long GetChunkSize(FIoChunkId chunkId)
    {
        if (!_toc.ChunkIdMap.TryGetValue(chunkId.ToRaw(), out uint tocEntryIndex))
            return -1;

        return (long)_toc.ChunkOffsetLengths[(int)tocEntryIndex].Length;
    }

    /// <summary>
    /// Read at most <paramref name="maxBytes"/> bytes from the start of a chunk, decompressing
    /// only the blocks needed to cover them. A zen package header sits at the front of its
    /// chunk, so this reads a header without paying for the export data behind it.
    /// </summary>
    public byte[] ReadChunkPrefix(FIoChunkId chunkId, int maxBytes)
    {
        if (!_toc.ChunkIdMap.TryGetValue(chunkId.ToRaw(), out uint tocEntryIndex))
            throw new KeyNotFoundException($"Chunk {chunkId} not found in container {_containerName}");

        return ReadChunkByIndex(tocEntryIndex, maxBytes);
    }

    public byte[] ReadChunk(FIoChunkId chunkId)
    {
        if (!_toc.ChunkIdMap.TryGetValue(chunkId.ToRaw(), out uint tocEntryIndex))
            throw new KeyNotFoundException($"Chunk {chunkId} not found in container {_containerName}");

        return ReadChunkByIndex(tocEntryIndex);
    }

    /// <summary>
    /// Read a chunk by its TOC entry index
    /// </summary>
    public byte[] ReadChunkByIndex(uint tocEntryIndex, int maxBytes = -1)
    {
        EnsureCasOpen();

        var offsetAndLength = _toc.ChunkOffsetLengths[(int)tocEntryIndex];
        ulong offset = offsetAndLength.Offset;
        ulong size = offsetAndLength.Length;

        // How many bytes the caller actually wants. Block addressing below still works off the
        // full chunk size - clamping that would mis-size the destination buffer - so a prefix
        // read is expressed purely as stopping the decompression loop early.
        int wanted = maxBytes >= 0 && (ulong)maxBytes < size ? maxBytes : (int)size;

        // Helper: thread-safe positional read via RandomAccess
        void ReadAt(long pos, byte[] buf, int bufOffset, int count)
        {
            int got = RandomAccess.Read(_casHandle!, buf.AsSpan(bufOffset, count), pos);
            if (got != count)
                throw new IOException($"CAS short read at {pos}: expected {count}, got {got}");
        }

        // If no compression blocks, read directly from CAS
        if (_toc.CompressionBlocks.Count == 0)
        {
            byte[] data = new byte[wanted];
            ReadAt((long)offset, data, 0, wanted);
            return data;
        }

        uint compressionBlockSize = _toc.CompressionBlockSize;
        int firstBlockIndex = (int)(offset / compressionBlockSize);

        // Only the blocks covering the requested bytes are needed. Bounding this by wanted
        // rather than by the whole chunk is what keeps a prefix read cheap: reading a package
        // header out of a multi-megabyte texture chunk otherwise allocated, and zeroed, a
        // buffer the size of the texture.
        int lastBlockIndex = (int)((AlignU64(offset + (ulong)wanted, compressionBlockSize) - 1) / compressionBlockSize);

        // Bounds check
        if (firstBlockIndex >= _toc.CompressionBlocks.Count || lastBlockIndex >= _toc.CompressionBlocks.Count)
        {
            byte[] directData = new byte[wanted];
            ReadAt((long)offset, directData, 0, wanted);
            return directData;
        }

        // Sized from the blocks actually decompressed, since each writes its own uncompressed
        // length at its own offset and the last one must not run off the end.
        long blockBytes = 0;
        for (int b = firstBlockIndex; b <= lastBlockIndex; b++)
            blockBytes += _toc.CompressionBlocks[b].UncompressedSize;

        byte[] data2 = new byte[AlignUSize((int)Math.Max(blockBytes, wanted), 16)];
        int cur = 0;

        for (int blockIndex = firstBlockIndex; blockIndex <= lastBlockIndex; blockIndex++)
        {
            var block = _toc.CompressionBlocks[blockIndex];
            int compressedSize = (int)block.CompressedSize;
            int uncompressedSize = (int)block.UncompressedSize;

            byte compressionMethodIndex = block.CompressionMethodIndex;

            if (compressionMethodIndex == 0)
            {
                // Uncompressed
                if (_aesKey != null && _toc.IsEncrypted)
                {
                    int alignedSize = AlignUSize(uncompressedSize, 16);
                    byte[] encryptedData = new byte[alignedSize];
                    ReadAt((long)block.Offset, encryptedData, 0, alignedSize);
                    DecryptAes(encryptedData, _aesKey);
                    Array.Copy(encryptedData, 0, data2, cur, uncompressedSize);
                }
                else
                {
                    ReadAt((long)block.Offset, data2, cur, uncompressedSize);
                }
            }
            else
            {
                // Compressed
                byte[] compressedData;
                if (_aesKey != null && _toc.IsEncrypted)
                {
                    int alignedSize = AlignUSize(compressedSize, 16);
                    compressedData = new byte[alignedSize];
                    ReadAt((long)block.Offset, compressedData, 0, alignedSize);
                    DecryptAes(compressedData, _aesKey);
                    compressedData = compressedData[..compressedSize];
                }
                else
                {
                    compressedData = new byte[compressedSize];
                    ReadAt((long)block.Offset, compressedData, 0, compressedSize);
                }

                string? compressionMethod = _toc.CompressionMethods.Count > compressionMethodIndex - 1
                    ? _toc.CompressionMethods[compressionMethodIndex - 1]
                    : null;

                Decompress(compressionMethod, compressedData, data2, cur, uncompressedSize);
            }

            cur += uncompressedSize;

            // Prefix read: everything the caller asked for is decompressed, so skip the
            // remaining blocks entirely.
            if (cur >= wanted) break;
        }

        // Truncate to actual size
        if (data2.Length != wanted)
        {
            byte[] result = new byte[wanted];
            Array.Copy(data2, result, wanted);
            return result;
        }

        return data2;
    }

    /// <summary>
    /// Check if the container has a specific chunk
    /// </summary>
    public bool HasChunk(FIoChunkId chunkId)
    {
        return _toc.ChunkIdMap.ContainsKey(chunkId.ToRaw());
    }

    /// <summary>
    /// Get all chunk IDs in this container
    /// </summary>
    public IEnumerable<FIoChunkId> GetChunks()
    {
        return _toc.Chunks;
    }

    /// <summary>
    /// Get the file path for a chunk if available
    /// </summary>
    public string? GetChunkPath(FIoChunkId chunkId)
    {
        if (!_toc.ChunkIdMap.TryGetValue(chunkId.ToRaw(), out uint tocEntryIndex))
            return null;

        if (!_toc.FileMapRev.TryGetValue(tocEntryIndex, out string? path))
            return null;

        // Directory index paths are built starting from the mount point (see BuildFilePaths),
        // so what is stored here already carries it and must not have it applied again. Doing
        // so repeated the mount: harmless-looking on a shallow "../../../" container, but one
        // mounted deeper came out as
        //   ../../../Marvel/Content/Marvel/Environment/../../../Marvel/Content/Marvel/...
        // which resolves to Marvel/Marvel/Content/... rather than the real path.
        return path;
    }

    /// <summary>
    /// Read a chunk by its file path (relative to mount point, e.g. "Marvel/Content/Marvel/.../T_Foo.uptnl")
    /// Returns null if not found.
    /// </summary>
    public byte[]? ReadChunkByPath(string relativePath)
    {
        if (!_toc.FileMap.TryGetValue(relativePath, out uint tocEntryIndex))
            return null;
        if (tocEntryIndex >= _toc.Chunks.Count)
            return null;
        var chunkId = _toc.Chunks[(int)tocEntryIndex];
        return ReadChunk(chunkId);
    }

    /// <summary>
    /// Check if a file path exists in this container's directory index.
    /// </summary>
    public bool HasPath(string relativePath)
    {
        return _toc.FileMap.ContainsKey(relativePath);
    }

    private void EnsureCasOpen()
    {
        if (_casHandle != null) return; // fast path (handle set atomically after stream)
        lock (_casOpenLock)
        {
            if (_casStream == null)
            {
                _casStream = new FileStream(_ucasPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: false);
                _casHandle = _casStream.SafeFileHandle; // volatile-like: assigned last so fast path is safe
            }
        }
    }

    private static ulong AlignU64(ulong value, uint alignment)
    {
        return (value + alignment - 1) / alignment * alignment;
    }

    private static int AlignUSize(int value, int alignment)
    {
        return (value + alignment - 1) / alignment * alignment;
    }

    private static void DecryptAes(byte[] data, byte[] key)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        for (int i = 0; i < data.Length; i += 16)
        {
            decryptor.TransformBlock(data, i, 16, data, i);
        }
    }

    private static void Decompress(string? method, byte[] compressedData, byte[] output, int outputOffset, int uncompressedSize)
    {
        if (method == null || method == "None")
        {
            Array.Copy(compressedData, 0, output, outputOffset, uncompressedSize);
            return;
        }

        if (method == "Oodle" || method.StartsWith("Oodle"))
        {
            OodleDecompressor.Decompress(compressedData, output, outputOffset, uncompressedSize);
            return;
        }

        if (method == "Zlib")
        {
            using var inputStream = new MemoryStream(compressedData);
            using var zlibStream = new System.IO.Compression.ZLibStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            zlibStream.ReadExactly(output, outputOffset, uncompressedSize);
            return;
        }

        if (method == "Gzip")
        {
            using var inputStream = new MemoryStream(compressedData);
            using var gzipStream = new System.IO.Compression.GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            gzipStream.ReadExactly(output, outputOffset, uncompressedSize);
            return;
        }

        if (method == "LZ4")
        {
            // LZ4 decompression - would need K4os.Compression.LZ4 package
            throw new NotSupportedException($"LZ4 decompression not yet implemented");
        }

        throw new NotSupportedException($"Unknown compression method: {method}");
    }

    public void Dispose()
    {
        _casStream?.Dispose();
        _casStream = null;
        _casHandle = null;
    }
}

/// <summary>
/// IoStore TOC (Table of Contents) - parsed from .utoc file
/// </summary>
public class IoStoreToc
{
    public EIoStoreTocVersion Version { get; set; }
    public EIoContainerFlags ContainerFlags { get; set; }
    public uint CompressionBlockSize { get; set; }
    public List<FIoChunkId> Chunks { get; set; } = new();
    public List<FIoOffsetAndLengthReadable> ChunkOffsetLengths { get; set; } = new();
    public List<FIoStoreTocCompressedBlockEntryReadable> CompressionBlocks { get; set; } = new();
    public List<string> CompressionMethods { get; set; } = new();
    public Dictionary<FIoChunkIdRaw, uint> ChunkIdMap { get; set; } = new();
    public Dictionary<uint, string> FileMapRev { get; set; } = new();
    public Dictionary<string, uint> FileMap { get; set; } = new();
    public string MountPoint { get; set; } = "";
    public byte[]? AesKey { get; set; }
    public FIoContainerId ContainerId { get; set; }
    public byte[] EncryptionKeyGuid { get; set; } = new byte[16];
    /// <summary>
    /// Raw directory index bytes from the original TOC, for pass-through during recompression.
    /// </summary>
    public byte[]? RawDirectoryIndex { get; set; }
    public bool IsEncrypted => ContainerFlags.HasFlag(EIoContainerFlags.Encrypted);
    public bool CanDecrypt => IsEncrypted && AesKey != null && AesKey.Length == 32;

    /// <summary>
    /// Read TOC with multiple AES keys - tries each key for directory index decryption.
    /// The key that successfully decrypts is stored in AesKey.
    /// </summary>
    public static IoStoreToc Read(string utocPath, List<byte[]> aesKeys)
    {
        // Try each key, return the first one that produces a valid directory index
        IoStoreToc? bestToc = null;
        foreach (var key in aesKeys)
        {
            try
            {
                var toc = Read(utocPath, key);
                if (toc.FileMapRev.Count > 0)
                    return toc; // This key worked
                // Keep the first successful parse even if no files (unencrypted container)
                bestToc ??= toc;
            }
            catch
            {
                // This key didn't work, try next
            }
        }
        // If no key produced file paths, return the best we got or try without key
        return bestToc ?? Read(utocPath, (byte[]?)null);
    }

    public static IoStoreToc Read(string utocPath, byte[]? aesKey = null)
    {
        using var stream = new FileStream(utocPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var toc = new IoStoreToc();
        toc.AesKey = aesKey;

        // Read header - matches FIoStoreTocHeader structure
        byte[] magic = reader.ReadBytes(16);
        // Validate magic: "-==--==--==--==-" (0x2D, 0x3D, 0x3D, 0x2D repeated)
        
        toc.Version = (EIoStoreTocVersion)reader.ReadByte();
        byte reserved0 = reader.ReadByte();
        ushort reserved1 = reader.ReadUInt16();
        uint tocHeaderSize = reader.ReadUInt32();
        uint tocEntryCount = reader.ReadUInt32();
        uint tocCompressedBlockEntryCount = reader.ReadUInt32();
        uint tocCompressedBlockEntrySize = reader.ReadUInt32(); // Size of each compressed block entry (12 bytes)
        uint compressionMethodNameCount = reader.ReadUInt32();
        uint compressionMethodNameLength = reader.ReadUInt32();
        toc.CompressionBlockSize = reader.ReadUInt32();
        uint directoryIndexSize = reader.ReadUInt32();
        uint partitionCount = reader.ReadUInt32();
        ulong containerId = reader.ReadUInt64();
        toc.ContainerId = new FIoContainerId(containerId);
        byte[] encryptionKeyGuid = reader.ReadBytes(16);
        toc.EncryptionKeyGuid = encryptionKeyGuid;
        toc.ContainerFlags = (EIoContainerFlags)reader.ReadByte();
        byte reserved3 = reader.ReadByte();
        ushort reserved4 = reader.ReadUInt16();
        uint tocChunkPerfectHashSeedsCount = reader.ReadUInt32();
        ulong partitionSize = reader.ReadUInt64();
        uint tocChunksWithoutPerfectHashCount = reader.ReadUInt32();
        uint reserved7 = reader.ReadUInt32();
        reader.ReadBytes(40); // reserved8: 5 x u64
        
        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            Console.Error.WriteLine($"[TOC] partitionCount={partitionCount}, partitionSize={partitionSize}, compressionBlocks={tocCompressedBlockEntryCount}");
        
        // We're now at the end of the header, chunk data follows

        // Read chunk IDs
        for (int i = 0; i < tocEntryCount; i++)
        {
            var chunkId = FIoChunkId.Read(reader, toc.Version);
            toc.Chunks.Add(chunkId);
            toc.ChunkIdMap[chunkId.ToRaw()] = (uint)i;
        }

        // Read chunk offset/lengths
        for (int i = 0; i < tocEntryCount; i++)
        {
            toc.ChunkOffsetLengths.Add(FIoOffsetAndLengthReadable.Read(reader));
        }

        // Read hash map (for PerfectHashWithOverflow and later)
        if (toc.Version >= EIoStoreTocVersion.PerfectHashWithOverflow)
        {
            // Skip perfect hash seeds
            reader.BaseStream.Seek(tocChunkPerfectHashSeedsCount * 4, SeekOrigin.Current);
            // Skip chunks without perfect hash
            reader.BaseStream.Seek(tocChunksWithoutPerfectHashCount * 4, SeekOrigin.Current);
        }
        else if (toc.Version >= EIoStoreTocVersion.PerfectHash)
        {
            // Skip perfect hash seeds only
            reader.BaseStream.Seek(tocChunkPerfectHashSeedsCount * 4, SeekOrigin.Current);
        }

        // Read compression blocks
        for (int i = 0; i < tocCompressedBlockEntryCount; i++)
        {
            toc.CompressionBlocks.Add(FIoStoreTocCompressedBlockEntryReadable.Read(reader));
        }

        // Read compression methods - they come right after compression blocks
        for (int i = 0; i < compressionMethodNameCount; i++)
        {
            byte[] nameBytes = reader.ReadBytes((int)compressionMethodNameLength);
            string name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            if (!string.IsNullOrEmpty(name))
                toc.CompressionMethods.Add(name);
        }

        // If no compression methods found but count > 0, add Oodle as default
        if (toc.CompressionMethods.Count == 0 && compressionMethodNameCount > 0)
        {
            toc.CompressionMethods.Add("Oodle");
        }
        
        // Skip signatures if container is signed
        if (toc.ContainerFlags.HasFlag(EIoContainerFlags.Signed))
        {
            uint signatureSize = reader.ReadUInt32();
            reader.BaseStream.Seek(signatureSize * 2, SeekOrigin.Current); // toc_signature + block_signature
            reader.BaseStream.Seek(tocCompressedBlockEntryCount * 20, SeekOrigin.Current); // chunk_block_signatures (FSHAHash = 20 bytes)
        }

        // Read directory index if present
        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            Console.Error.WriteLine($"[TOC] Pre-directory: size={directoryIndexSize}, encrypted={toc.IsEncrypted}, canDecrypt={toc.CanDecrypt}, flags={toc.ContainerFlags}, position={stream.Position}, streamLength={stream.Length}");
        
        if (directoryIndexSize > 0 && (!toc.IsEncrypted || toc.CanDecrypt))
        {
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                Console.Error.WriteLine($"[TOC] Reading directory index...");
            
            // Check if we have enough data
            if (stream.Position + directoryIndexSize <= stream.Length)
            {
                try
                {
                    // Read the raw directory index data
                    byte[] directoryData = reader.ReadBytes((int)directoryIndexSize);
                    byte[] originalData = (byte[])directoryData.Clone();
                    
                    bool parsed = false;
                    
                    // For obfuscated containers: the Encrypted flag is set but the directory
                    // index is NOT actually encrypted. This blocks FModel (which checks GUID
                    // before trying keys) but we can read it by trying raw data first.
                    // Try raw (unencrypted) first, then fall back to decryption.
                    
                    // Try 1: Parse raw data directly (handles obfuscated containers)
                    try
                    {
                        using var dirStream = new MemoryStream(originalData);
                        using var dirReader = new BinaryReader(dirStream);
                        ReadDirectoryIndex(dirReader, toc, directoryIndexSize);
                        
                        // Validate: mount point should look like a valid UE path (starts with ../ or /)
                        // This prevents false positives when parsing encrypted garbage
                        bool validMountPoint = !string.IsNullOrEmpty(toc.MountPoint) && 
                            (toc.MountPoint.StartsWith("../") || toc.MountPoint.StartsWith("/"));
                        
                        if (toc.FileMapRev.Count > 0 && validMountPoint)
                        {
                            toc.RawDirectoryIndex = (byte[])originalData.Clone();
                            parsed = true;
                            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                                Console.Error.WriteLine($"[TOC] Raw directory index parsed: {toc.FileMapRev.Count} file paths");
                        }
                        else if (toc.FileMapRev.Count > 0)
                        {
                            // Parsed something but mount point looks like garbage - clear and try decrypt
                            toc.FileMapRev.Clear();
                            toc.MountPoint = "";
                            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                                Console.Error.WriteLine($"[TOC] Raw parse produced invalid mount point, trying decrypt...");
                        }
                    }
                    catch
                    {
                        // Raw parse failed, will try decryption below
                    }
                    
                    // Try 2: Decrypt and parse (handles properly encrypted containers)
                    if (!parsed && toc.IsEncrypted && toc.CanDecrypt)
                    {
                        try
                        {
                            toc.FileMapRev.Clear();
                            
                            byte[] decryptedData = (byte[])originalData.Clone();
                            DecryptAes(decryptedData, toc.AesKey!);
                            
                            using var dirStream = new MemoryStream(decryptedData);
                            using var dirReader = new BinaryReader(dirStream);
                            ReadDirectoryIndex(dirReader, toc, directoryIndexSize);
                            
                            if (toc.FileMapRev.Count > 0)
                            {
                                toc.RawDirectoryIndex = (byte[])decryptedData.Clone();
                                parsed = true;
                                if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                                    Console.Error.WriteLine($"[TOC] Decrypted directory index parsed: {toc.FileMapRev.Count} file paths");
                            }
                        }
                        catch
                        {
                            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                                Console.Error.WriteLine($"[TOC] Decrypted parse also failed");
                        }
                    }
                    
                    if (!parsed && Environment.GetEnvironmentVariable("DEBUG") == "1")
                        Console.Error.WriteLine($"[TOC] Could not parse directory index");
                }
                catch (Exception ex)
                {
                    if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                        Console.Error.WriteLine($"[TOC] Failed to parse directory index: {ex.Message}");
                }
            }
            else if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine($"[TOC] Directory index beyond stream end, skipping");
            }
        }

        return toc;
    }
    
    private static void DecryptAes(byte[] data, byte[] key)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        for (int i = 0; i < data.Length; i += 16)
        {
            decryptor.TransformBlock(data, i, 16, data, i);
        }
    }
    
    private static void ReadDirectoryIndex(BinaryReader reader, IoStoreToc toc, uint directoryIndexSize)
    {
        long startPos = reader.BaseStream.Position;
        long endPos = startPos + directoryIndexSize;
        
        // Read mount point (length-prefixed string like retoc)
        toc.MountPoint = ReadLengthPrefixedString(reader);
        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            Console.Error.WriteLine($"[TOC] Mount point: {toc.MountPoint}");
        
        // Read directory entries array (count then entries)
        int dirEntryCount = reader.ReadInt32();
        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            Console.Error.WriteLine($"[TOC] Directory entries: {dirEntryCount}");
        
        if (dirEntryCount < 0 || dirEntryCount > 1000000)
            throw new Exception($"Invalid directory entry count: {dirEntryCount}");
        
        var dirEntries = new List<(uint name, uint firstChild, uint nextSibling, uint firstFile)>();
        for (int i = 0; i < dirEntryCount; i++)
        {
            dirEntries.Add((
                reader.ReadUInt32(), // name string index
                reader.ReadUInt32(), // first child directory
                reader.ReadUInt32(), // next sibling directory
                reader.ReadUInt32()  // first file
            ));
        }
        
        // Read file entries array
        int fileEntryCount = reader.ReadInt32();
        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            Console.Error.WriteLine($"[TOC] File entries: {fileEntryCount}");
        
        if (fileEntryCount < 0 || fileEntryCount > 1000000)
            throw new Exception($"Invalid file entry count: {fileEntryCount}");
        
        var fileEntries = new List<(uint name, uint nextFile, uint userData)>();
        for (int i = 0; i < fileEntryCount; i++)
        {
            fileEntries.Add((
                reader.ReadUInt32(), // name string index
                reader.ReadUInt32(), // next file in directory
                reader.ReadUInt32()  // user data (chunk index)
            ));
        }
        
        // Read string table (count then length-prefixed strings)
        int stringCount = reader.ReadInt32();
        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            Console.Error.WriteLine($"[TOC] Strings: {stringCount}");
        
        if (stringCount < 0 || stringCount > 1000000)
            throw new Exception($"Invalid string count: {stringCount}");
        
        var strings = new List<string>();
        for (int i = 0; i < stringCount; i++)
        {
            strings.Add(ReadLengthPrefixedString(reader));
        }
        
        // Build file paths recursively
        BuildFilePaths(toc, dirEntries, fileEntries, strings, 0, toc.MountPoint);
        
        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            Console.Error.WriteLine($"[TOC] Built {toc.FileMapRev.Count} file paths");
    }
    
    /// <summary>
    /// Read a length-prefixed string in UE format (matching retoc's read_string)
    /// Positive length = ASCII, negative length = UTF-16
    /// </summary>
    private static string ReadLengthPrefixedString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length == 0)
            return "";
        
        if (length < 0)
        {
            // UTF-16 string
            int charCount = -length;
            byte[] bytes = reader.ReadBytes(charCount * 2);
            string str = Encoding.Unicode.GetString(bytes);
            // Trim null terminator
            int nullPos = str.IndexOf('\0');
            return nullPos >= 0 ? str[..nullPos] : str;
        }
        else
        {
            // ASCII/UTF-8 string
            byte[] bytes = reader.ReadBytes(length);
            string str = Encoding.UTF8.GetString(bytes);
            // Trim null terminator
            int nullPos = str.IndexOf('\0');
            return nullPos >= 0 ? str[..nullPos] : str;
        }
    }
    
    private static void BuildFilePaths(
        IoStoreToc toc,
        List<(uint name, uint firstChild, uint nextSibling, uint firstFile)> dirEntries,
        List<(uint name, uint nextFile, uint userData)> fileEntries,
        List<string> strings,
        uint dirIndex,
        string currentPath)
    {
        if (dirIndex >= dirEntries.Count)
            return;
        
        var dir = dirEntries[(int)dirIndex];
        
        // Process files in this directory
        uint fileIndex = dir.firstFile;
        while (fileIndex < fileEntries.Count && fileIndex != 0xFFFFFFFF)
        {
            var file = fileEntries[(int)fileIndex];
            string fileName = file.name < strings.Count ? strings[(int)file.name] : "";
            string fullPath = string.IsNullOrEmpty(currentPath) ? fileName : $"{currentPath}{fileName}";
            
            // userData is the chunk index
            if (file.userData < toc.Chunks.Count)
            {
                toc.FileMapRev[file.userData] = fullPath;
                toc.FileMap[fullPath] = file.userData;
            }
            
            fileIndex = file.nextFile;
        }
        
        // Process child directories
        uint childIndex = dir.firstChild;
        while (childIndex < dirEntries.Count && childIndex != 0xFFFFFFFF)
        {
            var child = dirEntries[(int)childIndex];
            string dirName = child.name < strings.Count ? strings[(int)child.name] : "";
            string childPath = string.IsNullOrEmpty(currentPath) ? $"{dirName}/" : $"{currentPath}{dirName}/";
            
            BuildFilePaths(toc, dirEntries, fileEntries, strings, childIndex, childPath);
            
            childIndex = child.nextSibling;
        }
    }
}

/// <summary>
/// Readable version of FIoOffsetAndLength
/// </summary>
public struct FIoOffsetAndLengthReadable
{
    public ulong Offset { get; set; }
    public ulong Length { get; set; }

    public static FIoOffsetAndLengthReadable Read(BinaryReader reader)
    {
        byte[] data = reader.ReadBytes(10);
        return new FIoOffsetAndLengthReadable
        {
            // Big-endian read
            Offset = ((ulong)data[0] << 32) | ((ulong)data[1] << 24) | ((ulong)data[2] << 16) | ((ulong)data[3] << 8) | data[4],
            Length = ((ulong)data[5] << 32) | ((ulong)data[6] << 24) | ((ulong)data[7] << 16) | ((ulong)data[8] << 8) | data[9]
        };
    }
}

/// <summary>
/// Readable version of FIoStoreTocCompressedBlockEntry
/// </summary>
public struct FIoStoreTocCompressedBlockEntryReadable
{
    public ulong Offset { get; set; }
    public uint CompressedSize { get; set; }
    public uint UncompressedSize { get; set; }
    public byte CompressionMethodIndex { get; set; }

    public static FIoStoreTocCompressedBlockEntryReadable Read(BinaryReader reader)
    {
        byte[] data = reader.ReadBytes(12);

        // Offset: 5 bytes LITTLE-ENDIAN (matching retoc-rivals)
        ulong offset = (ulong)data[0] | ((ulong)data[1] << 8) | ((ulong)data[2] << 16) | ((ulong)data[3] << 24) | ((ulong)data[4] << 32);

        // Compressed size: 3 bytes little-endian
        uint compressedSize = (uint)(data[5] | (data[6] << 8) | (data[7] << 16));

        // Uncompressed size: 3 bytes little-endian
        uint uncompressedSize = (uint)(data[8] | (data[9] << 8) | (data[10] << 16));

        // Compression method: 1 byte
        byte compressionMethod = data[11];

        return new FIoStoreTocCompressedBlockEntryReadable
        {
            Offset = offset,
            CompressedSize = compressedSize,
            UncompressedSize = uncompressedSize,
            CompressionMethodIndex = compressionMethod
        };
    }
}

/// <summary>
/// Raw chunk ID (12 bytes)
/// </summary>
public struct FIoChunkIdRaw : IEquatable<FIoChunkIdRaw>
{
    public byte[] Id { get; set; }

    public FIoChunkIdRaw(byte[] id)
    {
        Id = id;
    }

    public bool Equals(FIoChunkIdRaw other)
    {
        if (Id == null || other.Id == null) return false;
        if (Id.Length != other.Id.Length) return false;
        for (int i = 0; i < Id.Length; i++)
            if (Id[i] != other.Id[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is FIoChunkIdRaw other && Equals(other);

    public override int GetHashCode()
    {
        if (Id == null) return 0;
        int hash = 17;
        foreach (byte b in Id)
            hash = hash * 31 + b;
        return hash;
    }
}

/// <summary>
/// Oodle decompressor - wrapper for native Oodle library
/// </summary>
public static class OodleDecompressor
{
    public static void Decompress(byte[] compressedData, byte[] output, int outputOffset, int uncompressedSize)
    {
        // Use the OodleCompression class which has proper P/Invoke setup
        if (!OodleCompression.IsAvailable)
        {
            string libName = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux)
                ? "liboo2corelinux64.so.9"
                : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)
                    ? "liboo2coremac64.9.dylib"
                    : "oo2core_9_win64.dll";
            throw new NotSupportedException(
                $"Oodle decompression requires the native Oodle library ({libName}). " +
                "Please ensure it's in the application directory.");
        }

        var decompressed = OodleCompression.Decompress(compressedData, uncompressedSize);
        if (decompressed == null)
        {
            throw new InvalidOperationException("Oodle decompression failed");
        }

        Array.Copy(decompressed, 0, output, outputOffset, uncompressedSize);
    }
}
