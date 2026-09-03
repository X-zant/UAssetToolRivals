using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UAssetTool.IoStore;

/// <summary>
/// PAK V11 reader implementation based on repak source code.
/// Reads encrypted PAK files compatible with Marvel Rivals.
/// 
/// Reference: repak/src/pak.rs, repak/src/entry.rs, repak/src/data.rs, repak/src/footer.rs
/// </summary>
public class PakReader : IDisposable
{
    // Constants from repak/src/lib.rs
    private const uint PAK_MAGIC = 0x5A6F12E1;
    private const string DEFAULT_AES_KEY_HEX = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";

    private readonly Stream _stream;
    private readonly SafeFileHandle? _fileHandle; // Non-null when backed by a real file; enables thread-safe RandomAccess reads
    private readonly byte[] _aesKey;
    private readonly byte[] _standardAesKey; // Key without byte-swap for standard UE4 PAKs
    private readonly bool _ownsStream;
    private bool _disposed;
    private bool _useStandardAes; // true = standard UE4 AES, false = repak byte-swapped AES

    // PAK metadata
    public int Version { get; private set; }
    public string MountPoint { get; private set; } = "";
    public bool EncryptedIndex { get; private set; }
    public ulong PathHashSeed { get; private set; }
    
    // Index data
    private readonly Dictionary<string, PakEntry> _entries = new();
    private int _skippedInherited;
    
    // Cache for encryption limits (path -> limit) to avoid re-hashing on repeat access
    // ConcurrentDictionary so Get() is safe to call from Parallel.ForEach
    private readonly ConcurrentDictionary<string, int> _encryptionLimitCache = new();
    
    // Compression methods from footer
    private readonly string[] _compressionMethods = new string[5];

    public PakReader(string pakPath, string? aesKeyHex = null, bool useStandardAes = false)
    {
        var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: false);
        _stream = fs;
        _fileHandle = fs.SafeFileHandle;
        _ownsStream = true;
        _useStandardAes = useStandardAes;
        string keyHex = aesKeyHex ?? DEFAULT_AES_KEY_HEX;
        _aesKey = ParseAesKey(keyHex);
        _standardAesKey = ParseAesKeyStandard(keyHex);
        ReadPak();
    }

    public PakReader(Stream stream, string? aesKeyHex = null, bool ownsStream = false, bool useStandardAes = false)
    {
        _stream = stream;
        _fileHandle = (stream is FileStream fs) ? fs.SafeFileHandle : null;
        _ownsStream = ownsStream;
        _useStandardAes = useStandardAes;
        string keyHex = aesKeyHex ?? DEFAULT_AES_KEY_HEX;
        _aesKey = ParseAesKey(keyHex);
        _standardAesKey = ParseAesKeyStandard(keyHex);
        
        ReadPak();
    }

    /// <summary>
    /// Parse AES key from hex string with 4-byte chunk reversal (from repak/src/utils.rs)
    /// </summary>
    private static byte[] ParseAesKey(string hex)
    {
        hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        // CRITICAL: Reverse each 4-byte chunk (from repak/src/utils.rs line 9)
        for (int i = 0; i < bytes.Length; i += 4)
        {
            Array.Reverse(bytes, i, 4);
        }

        return bytes;
    }

    /// <summary>
    /// Parse AES key from hex string WITHOUT byte-swap (standard UE4 format)
    /// </summary>
    private static byte[] ParseAesKeyStandard(string hex)
    {
        hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    /// <summary>
    /// Get list of all file paths in the PAK
    /// </summary>
    public IReadOnlyCollection<string> Files => _entries.Keys;

    /// <summary>
    /// Get entry info for a specific file
    /// </summary>
    public PakEntry? GetEntry(string path)
    {
        return _entries.TryGetValue(path, out var entry) ? entry : null;
    }

    /// <summary>
    /// Read file data from the PAK
    /// </summary>
    public byte[] Get(string path)
    {
        if (!_entries.TryGetValue(path, out var entry))
            throw new KeyNotFoundException($"Entry not found: {path}");

        return ReadEntryData(entry, path);
    }

    /// <summary>
    /// Check if PAK contains a specific file
    /// </summary>
    public bool Contains(string path) => _entries.ContainsKey(path);

    /// <summary>
    /// Read and parse the PAK file
    /// </summary>
    private void ReadPak()
    {
        using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);

        // 1. Read footer (at end of file)
        ReadFooter(reader, out long indexOffset, out ulong indexSize, out byte[] expectedIndexHash);

        // 2. Read index (decrypt if encrypted)
        _stream.Seek(indexOffset, SeekOrigin.Begin);
        byte[] rawIndex = reader.ReadBytes((int)indexSize);
        byte[] indexData;
        
        if (EncryptedIndex)
        {
            // AES-256-ECB decryption with byte-swap (Marvel Rivals specific)
            indexData = AesDecrypt(rawIndex, _aesKey);
            
            byte[] hash1 = SHA1.HashData(indexData);
            if (!hash1.SequenceEqual(expectedIndexHash))
            {
                Console.Error.WriteLine("[PakReader] Warning: Index hash mismatch after AES decryption");
            }
        }
        else
        {
            indexData = rawIndex;
        }

        // 3. Parse index
        using var indexStream = new MemoryStream(indexData);
        using var indexReader = new BinaryReader(indexStream);
        ParseIndex(indexReader, reader);
    }

    /// <summary>
    /// Read footer from end of PAK file (from footer.rs)
    /// Footer is 221 bytes for V11
    /// </summary>
    private void ReadFooter(BinaryReader reader, out long indexOffset, out ulong indexSize, out byte[] indexHash)
    {
        // Footer size for V11 = 221 bytes
        // encryption_guid(16) + encrypted(1) + magic(4) + version(4) + offset(8) + size(8) + hash(20) + compression(5*32=160)
        const int footerSize = 221;
        
        _stream.Seek(-footerSize, SeekOrigin.End);

        // Skip encryption GUID (16 bytes)
        byte[] encGuid = reader.ReadBytes(16);

        // Is index encrypted (1 byte)
        EncryptedIndex = reader.ReadByte() != 0;

        // Magic (4 bytes)
        uint magic = reader.ReadUInt32();
        if (magic != PAK_MAGIC)
            throw new InvalidDataException($"Invalid PAK magic: 0x{magic:X8}, expected 0x{PAK_MAGIC:X8}");

        // Version (4 bytes)
        Version = reader.ReadInt32();

        // Index offset (8 bytes)
        indexOffset = reader.ReadInt64();

        // Index size (8 bytes)
        indexSize = reader.ReadUInt64();

        // Index hash (20 bytes)
        indexHash = reader.ReadBytes(20);

        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
        {
            Console.Error.WriteLine($"[PakReader] Footer: version={Version}, encrypted={EncryptedIndex}, indexOffset={indexOffset}, indexSize={indexSize}");
            Console.Error.WriteLine($"[PakReader] Footer: encGuid={BitConverter.ToString(encGuid)}, indexHash={BitConverter.ToString(indexHash)}");
        }

        // Read compression methods (5 * 32 = 160 bytes)
        for (int i = 0; i < 5; i++)
        {
            byte[] methodBytes = reader.ReadBytes(32);
            int nullIndex = Array.IndexOf(methodBytes, (byte)0);
            if (nullIndex < 0) nullIndex = 32;
            _compressionMethods[i] = Encoding.ASCII.GetString(methodBytes, 0, nullIndex);
        }
    }

    /// <summary>
    /// Parse the decrypted index (from pak.rs)
    /// </summary>
    private void ParseIndex(BinaryReader indexReader, BinaryReader dataReader)
    {
        // Mount point (length-prefixed string with null terminator)
        MountPoint = ReadString(indexReader);

        // Record count
        uint recordCount = indexReader.ReadUInt32();

        // Path hash seed (V10+)
        PathHashSeed = indexReader.ReadUInt64();

        // Has path hash index
        uint hasPathHashIndex = indexReader.ReadUInt32();
        long pathHashIndexOffset = 0;
        ulong pathHashIndexSize = 0;
        byte[] pathHashIndexHash = Array.Empty<byte>();
        
        if (hasPathHashIndex != 0)
        {
            pathHashIndexOffset = indexReader.ReadInt64();
            pathHashIndexSize = indexReader.ReadUInt64();
            pathHashIndexHash = indexReader.ReadBytes(20);
        }

        // Has full directory index
        uint hasFullDirectoryIndex = indexReader.ReadUInt32();
        long fullDirectoryIndexOffset = 0;
        ulong fullDirectoryIndexSize = 0;
        byte[] fullDirectoryIndexHash = Array.Empty<byte>();
        
        if (hasFullDirectoryIndex != 0)
        {
            fullDirectoryIndexOffset = indexReader.ReadInt64();
            fullDirectoryIndexSize = indexReader.ReadUInt64();
            fullDirectoryIndexHash = indexReader.ReadBytes(20);
        }

        // Encoded entries size and data
        uint encodedEntriesSize = indexReader.ReadUInt32();
        byte[] encodedEntriesData = indexReader.ReadBytes((int)encodedEntriesSize);

        // Unused file count
        uint unusedFileCount = indexReader.ReadUInt32();

        // Read path hash index to get file paths (decrypt only if index is encrypted)
        if (hasPathHashIndex != 0 && pathHashIndexSize > 0)
        {
            _stream.Seek(pathHashIndexOffset, SeekOrigin.Begin);
            byte[] rawPhi = dataReader.ReadBytes((int)pathHashIndexSize);
            byte[] phiData = EncryptedIndex 
                ? AesDecrypt(rawPhi, _aesKey) 
                : rawPhi;

            using var phiStream = new MemoryStream(phiData);
            using var phiReader = new BinaryReader(phiStream);
            
            uint phiCount = phiReader.ReadUInt32();
            var pathHashToOffset = new Dictionary<ulong, uint>();
            
            for (uint i = 0; i < phiCount; i++)
            {
                ulong pathHash = phiReader.ReadUInt64();
                uint offset = phiReader.ReadUInt32();
                pathHashToOffset[pathHash] = offset;
            }
        }

        // Read full directory index to get file paths and entry offsets (decrypt only if index is encrypted)
        if (hasFullDirectoryIndex != 0 && fullDirectoryIndexSize > 0)
        {
            _stream.Seek(fullDirectoryIndexOffset, SeekOrigin.Begin);
            byte[] rawFdi = dataReader.ReadBytes((int)fullDirectoryIndexSize);
            byte[] fdiData = EncryptedIndex 
                ? AesDecrypt(rawFdi, _aesKey) 
                : rawFdi;

            using var fdiStream = new MemoryStream(fdiData);
            using var fdiReader = new BinaryReader(fdiStream);
            
            uint directoryCount = fdiReader.ReadUInt32();
            
            for (uint d = 0; d < directoryCount; d++)
            {
                string directory = ReadString(fdiReader);
                uint fileCount = fdiReader.ReadUInt32();
                
                for (uint f = 0; f < fileCount; f++)
                {
                    string filename = ReadString(fdiReader);
                    uint encodedOffset = fdiReader.ReadUInt32();

                    // Combine directory and filename
                    string fullPath = directory + filename;

                    // The directory index gives each file an offset into the encoded-entries
                    // blob. A patch pak also lists every file it does NOT change - the whole
                    // base-game namespace - and marks those with 0x80000000 (the "not stored
                    // here, inherit from the base pak" sentinel) instead of a real offset. Its
                    // data is not in this pak, so there is nothing to read; recording it as an
                    // entry would point at bytes that are not present. Treating the sentinel as
                    // a real offset also threw and aborted the whole pak. A full patch pak can
                    // carry over a million of these, so this is the common case, not an error.
                    const uint InheritFromBaseSentinel = 0x80000000u;
                    if ((encodedOffset & InheritFromBaseSentinel) != 0)
                    {
                        _skippedInherited++;
                        continue;
                    }

                    // Parse encoded entry at this offset
                    var entry = ParseEncodedEntry(encodedEntriesData, (int)encodedOffset);
                    _entries[fullPath] = entry;
                }
            }
        }

        Console.Error.WriteLine($"[PakReader] Loaded {_entries.Count} entries from PAK");
        if (_skippedInherited > 0)
            Console.Error.WriteLine($"[PakReader] {_skippedInherited} files inherited from base pak (no data in this patch pak)");
        Console.Error.WriteLine($"[PakReader] Compression methods: [{string.Join(", ", _compressionMethods.Where(m => !string.IsNullOrEmpty(m)))}]");
        
        // Debug: print first few entries
        int debugCount = 0;
        foreach (var kvp in _entries)
        {
            if (debugCount++ >= 3) break;
            Console.Error.WriteLine($"[PakReader] Entry '{kvp.Key}': Offset={kvp.Value.Offset}, Compressed={kvp.Value.CompressedSize}, Uncompressed={kvp.Value.UncompressedSize}, CompressionSlot={kvp.Value.CompressionSlot}, BlockSize={kvp.Value.CompressionBlockSize}, BlockCount={kvp.Value.CompressionBlocksCount}");
        }
    }

    /// <summary>
    /// Parse encoded entry (from repak entry.rs read_encoded)
    /// </summary>
    private static PakEntry ParseEncodedEntry(byte[] data, int offset)
    {
        using var stream = new MemoryStream(data, offset, data.Length - offset);
        using var reader = new BinaryReader(stream);

        uint bits = reader.ReadUInt32();

        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
        {
            Console.Error.WriteLine($"[PakReader] EncodedEntry bits=0x{bits:X8} at offset={offset}");
        }

        // Extract flags (matching repak's read_encoded exactly)
        uint compressionSlot = (bits >> 23) & 0x3F;
        bool isEncrypted = (bits & (1 << 22)) != 0;
        uint compressionBlockCount = (bits >> 6) & 0xFFFF;
        uint compressionBlockSizeRaw = bits & 0x3F;
        
        // Handle special case where block size needs extra read
        uint compressionBlockSize;
        if (compressionBlockSizeRaw == 0x3F)
        {
            compressionBlockSize = reader.ReadUInt32();
        }
        else
        {
            compressionBlockSize = compressionBlockSizeRaw << 11;
        }

        bool isOffsetSafe = (bits & (1u << 31)) != 0;
        bool isUncompressedSizeSafe = (bits & (1 << 30)) != 0;
        bool isSizeSafe = (bits & (1 << 29)) != 0;

        // Read offset
        ulong entryOffset = isOffsetSafe ? reader.ReadUInt32() : reader.ReadUInt64();

        // Read uncompressed size
        ulong uncompressedSize = isUncompressedSizeSafe ? reader.ReadUInt32() : reader.ReadUInt64();

        // Compressed size (only if compression is used)
        ulong compressedSize = uncompressedSize;
        if (compressionSlot > 0)
        {
            compressedSize = isSizeSafe ? reader.ReadUInt32() : reader.ReadUInt64();
        }

        // Calculate the inline entry header size (offset_base in repak)
        // This is where the actual data starts relative to entry.Offset
        ulong offsetBase = GetSerializedEntrySize(compressionSlot > 0 ? (int?)compressionSlot : null, compressionBlockCount);

        // Build block list (matching repak's read_encoded)
        List<(ulong start, ulong end)>? blocks = null;
        if (compressionBlockCount == 1 && !isEncrypted)
        {
            // Single block, not encrypted: block spans from offset_base to offset_base + compressed
            blocks = new List<(ulong start, ulong end)> { (offsetBase, offsetBase + compressedSize) };
        }
        else if (compressionBlockCount > 0)
        {
            // Multiple blocks or encrypted: read block sizes from encoded entry
            blocks = new List<(ulong start, ulong end)>((int)compressionBlockCount);
            ulong index = offsetBase;
            for (uint i = 0; i < compressionBlockCount; i++)
            {
                uint blockSize = reader.ReadUInt32();
                if (i < 3 && Environment.GetEnvironmentVariable("DEBUG") == "1")
                {
                    Console.Error.WriteLine($"[PakReader]   Encoded block[{i}]: size={blockSize}, startRel={index}, endRel={index + blockSize}");
                }
                blocks.Add((index, index + blockSize));
                if (isEncrypted)
                {
                    blockSize = (blockSize + 15) & ~15u; // Align to 16 bytes
                }
                index += blockSize;
            }
        }

        return new PakEntry
        {
            Offset = entryOffset,
            CompressedSize = compressedSize,
            UncompressedSize = uncompressedSize,
            CompressionSlot = compressionSlot > 0 ? (int?)(compressionSlot - 1) : null,
            IsEncrypted = isEncrypted,
            CompressionBlockSize = compressionBlockSize,
            CompressionBlocksCount = compressionBlockCount,
            Blocks = blocks
        };
    }
    
    /// <summary>
    /// Calculate serialized entry size (matching repak's Entry::get_serialized_size for V11)
    /// </summary>
    private static ulong GetSerializedEntrySize(int? compressionSlot, uint blockCount)
    {
        ulong size = 0;
        size += 8; // offset
        size += 8; // compressed
        size += 8; // uncompressed
        size += 4; // compression (32-bit for V11)
        size += 20; // hash
        if (compressionSlot.HasValue)
        {
            size += 4 + (8 + 8) * blockCount; // blockCount + blocks (start:8 + end:8 each)
        }
        size += 1; // flags/encrypted
        size += 4; // compression_block_size
        return size;
    }

    /// <summary>
    /// Read entry data from PAK file (matching repak entry.rs read_file exactly).
    /// 1. Seek to entry.offset
    /// 2. Read inline FPakEntry header to skip past it
    /// 3. Read ALL compressed data at once (aligned if encrypted)
    /// 4. Decrypt entire blob with plain AES-256-ECB
    /// 5. Truncate to actual compressed size
    /// 6. Slice by block ranges for decompression
    /// Uses RandomAccess.Read when backed by a file (thread-safe, no seek contention).
    /// </summary>
    private byte[] ReadEntryData(PakEntry entry, string path)
    {
        ulong uncompressedSize = entry.UncompressedSize;
        bool isCompressed = entry.CompressionSlot.HasValue;
        bool isEncrypted = entry.IsEncrypted;
        uint compressionMethod = isCompressed ? (uint)(entry.CompressionSlot!.Value + 1) : 0;

        // Handle empty files
        if (uncompressedSize == 0)
            return Array.Empty<byte>();

        // Compute inline header size from known entry metadata (no seek needed)
        long structSize = (long)GetSerializedEntrySize(entry.CompressionSlot.HasValue ? entry.CompressionSlot : null, entry.CompressionBlocksCount);
        long dataOffset = (long)entry.Offset + structSize;

        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
        {
            Console.Error.WriteLine($"[PakReader] ReadEntryData '{path}': entryOffset={entry.Offset}, dataOffset={dataOffset}, structSize={structSize}, compressed={entry.CompressedSize}, uncompressed={uncompressedSize}, encrypted={isEncrypted}");
        }

        // Step 3: Read ALL compressed data at once (thread-safe positional read when file-backed)
        long readSize = isEncrypted 
            ? ((long)entry.CompressedSize + 15) & ~15L  // align to AES block size
            : (long)entry.CompressedSize;
        
        byte[] data = new byte[readSize];
        if (_fileHandle != null)
        {
            int bytesRead = RandomAccess.Read(_fileHandle, data.AsSpan(), dataOffset);
            if (bytesRead != (int)readSize)
                throw new InvalidDataException($"Failed to read entry data: expected {readSize} bytes, got {bytesRead}");
        }
        else
        {
            lock (_stream)
            {
                _stream.Seek(dataOffset, SeekOrigin.Begin);
                int bytesRead = _stream.Read(data, 0, (int)readSize);
                if (bytesRead != (int)readSize)
                    throw new InvalidDataException($"Failed to read entry data: expected {readSize} bytes, got {bytesRead}");
            }
        }

        // Step 4: Partial decryption (Marvel Rivals uses partial encryption via blake3 path hash)
        // Only the first get_limit(path) bytes are encrypted, matching repak-rivals data.rs
        // The path for get_limit must be root_path(mount_point, entry_path) = strip "../../../" from mount_point + "/" + path
        if (isEncrypted)
        {
            string rootPath = ComputeRootPath(MountPoint, path);
            int limit = _encryptionLimitCache.GetOrAdd(rootPath, GetEncryptionLimit);
            if (limit > data.Length)
                limit = data.Length;
            // Align limit to AES block size (16 bytes)
            limit = (limit + 15) & ~15;
            if (limit > data.Length)
                limit = data.Length;
            
            if (limit > 0)
            {
                byte[] toDecrypt = new byte[limit];
                Array.Copy(data, toDecrypt, limit);
                byte[] decrypted = AesDecrypt(toDecrypt, _aesKey);
                Array.Copy(decrypted, 0, data, 0, limit);
            }
            
            // Truncate to actual compressed size
            if (data.Length > (long)entry.CompressedSize)
                Array.Resize(ref data, (int)entry.CompressedSize);
            
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine($"[PakReader] Partial decrypt: rootPath='{rootPath}', limit={limit}, dataLen={data.Length}");
                Console.Error.WriteLine($"[PakReader] After decrypt first 16 bytes: {BitConverter.ToString(data, 0, Math.Min(16, data.Length))}");
            }
        }

        // Step 6: Handle uncompressed entries
        if (!isCompressed)
        {
            if (data.Length > (int)uncompressedSize)
                Array.Resize(ref data, (int)uncompressedSize);
            return data;
        }

        // Step 6b: For compressed entries, compute block ranges relative to data start 
        // and decompress each block (matching repak's range computation for RelativeChunkOffsets)
        if (entry.Blocks == null || entry.Blocks.Count == 0)
            throw new InvalidDataException($"Compressed entry has no block info");

        uint blockSize = entry.CompressionBlockSize > 0 ? entry.CompressionBlockSize : 65536;

        using var outputStream = new MemoryStream((int)uncompressedSize);

        for (int blockIdx = 0; blockIdx < entry.Blocks.Count; blockIdx++)
        {
            var (blockStart, blockEnd) = entry.Blocks[blockIdx];
            
            // Convert block offsets from entry-relative to data-relative
            // repak: offset(index) = index - (data_offset - self.offset) = index - struct_size
            int rangeStart = (int)((long)blockStart - structSize);
            int rangeEnd = (int)((long)blockEnd - structSize);
            
            if (rangeStart < 0 || rangeEnd > data.Length || rangeStart >= rangeEnd)
            {
                Console.Error.WriteLine($"[PakReader] Block {blockIdx} out of range: rangeStart={rangeStart}, rangeEnd={rangeEnd}, dataLen={data.Length}");
                throw new InvalidDataException($"Block {blockIdx} range [{rangeStart}..{rangeEnd}] out of bounds (data length: {data.Length})");
            }

            byte[] blockData = new byte[rangeEnd - rangeStart];
            Array.Copy(data, rangeStart, blockData, 0, blockData.Length);

            // Calculate expected uncompressed size for this block
            long remainingUncompressed = (long)uncompressedSize - outputStream.Position;
            int blockUncompressedSize = (int)Math.Min(blockSize, remainingUncompressed);
            
            if (blockUncompressedSize <= 0)
                continue;

            if (blockIdx == 0 && Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine($"[PakReader] Block 0: range=[{rangeStart}..{rangeEnd}], compSize={blockData.Length}, uncompSize={blockUncompressedSize}");
                Console.Error.WriteLine($"[PakReader] Block 0 first 16 bytes: {BitConverter.ToString(blockData, 0, Math.Min(16, blockData.Length))}");
            }

            try
            {
                byte[] decompressedBlock = Decompress(blockData, blockUncompressedSize, compressionMethod);
                outputStream.Write(decompressedBlock, 0, decompressedBlock.Length);
            }
            catch (Exception)
            {
                Console.Error.WriteLine($"[PakReader] Block {blockIdx} decompression failed: compSize={blockData.Length}, uncompSize={blockUncompressedSize}, method={compressionMethod}");
                throw;
            }
        }

        return outputStream.ToArray();
    }

    /// <summary>
    /// Read inline FPakEntry header to advance stream past it (matching repak Entry::read for V11).
    /// Does NOT return entry data — just advances the reader position.
    /// </summary>
    private void ReadInlineEntryHeader(PakEntry entry)
    {
        using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        long startPos = _stream.Position;
        
        long ihOffset = reader.ReadInt64();       // offset (8)
        long ihCompressed = reader.ReadInt64();    // compressed (8)
        long ihUncompressed = reader.ReadInt64();  // uncompressed (8)
        int ihCompMethod = reader.ReadInt32();     // compression method (4) - U32 for V11
        reader.ReadBytes(20);                      // hash (20)
        
        int ihBlockCount = 0;
        // For V11 (>= CompressionEncryption):
        if (entry.CompressionSlot.HasValue)
        {
            // Read compression blocks array: count(4) + blocks(16*N)
            ihBlockCount = reader.ReadInt32();
            for (int i = 0; i < ihBlockCount; i++)
            {
                reader.ReadInt64(); // block start
                reader.ReadInt64(); // block end
            }
        }
        byte ihFlags = reader.ReadByte();          // flags (1)
        uint ihBlockSize = reader.ReadUInt32();    // compression_block_size (4)
        
        long headerSize = _stream.Position - startPos;
        
        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
        {
            Console.Error.WriteLine($"[PakReader] InlineHeader: offset={ihOffset}, comp={ihCompressed}, uncomp={ihUncompressed}, method={ihCompMethod}, flags=0x{ihFlags:X2}, blockSize={ihBlockSize}, blockCount={ihBlockCount}, headerSize={headerSize}");
        }
    }

    /// <summary>
    /// Partially decrypt data based on path hash (from data.rs)
    /// </summary>
    private byte[] PartialDecrypt(byte[] data, string path)
    {
        if (_useStandardAes)
        {
            // Standard UE4: full-block AES decryption
            int alignedSize = (data.Length + 15) & ~15;
            byte[] padded = new byte[alignedSize];
            Array.Copy(data, padded, data.Length);
            return StandardAesDecrypt(padded, _standardAesKey);
        }

        // Repak mode: partial encryption based on path hash
        int limit = GetEncryptionLimit(path);
        if (limit > data.Length)
            limit = data.Length;
        limit = (limit + 15) & ~15; // Align to 16 bytes
        if (limit > data.Length)
            limit = data.Length;

        byte[] result = new byte[data.Length];
        Array.Copy(data, result, data.Length);

        if (limit > 0)
        {
            byte[] toDecrypt = new byte[limit];
            Array.Copy(data, toDecrypt, limit);
            byte[] decrypted = AesDecrypt(toDecrypt, _aesKey);
            Array.Copy(decrypted, result, limit);
        }

        return result;
    }

    /// <summary>
    /// Calculate encryption limit based on path hash (from data.rs)
    /// </summary>
    private static int GetEncryptionLimit(string path)
    {
        using var hasher = Blake3.Hasher.New();
        hasher.Update(new byte[] { 0x11, 0x22, 0x33, 0x44 });
        hasher.Update(Encoding.ASCII.GetBytes(path.ToLowerInvariant()));

        byte[] hashBytes = hasher.Finalize().AsSpan().ToArray();
        ulong hashValue = BitConverter.ToUInt64(hashBytes, 0);

        long limit = (long)(((hashValue % 0x3d) * 63 + 319) & 0xffffffffffffffc0);
        if (limit == 0)
            limit = 0x1000;

        return (int)limit;
    }

    /// <summary>
    /// Compute the root path for encryption limit calculation.
    /// Matches repak-rivals' root_path(mount_point, path): concat with "/" separator,
    /// deduplicate consecutive slashes, then strip "../../../" prefix.
    /// </summary>
    private static string ComputeRootPath(string mountPoint, string entryPath)
    {
        string combined = mountPoint + "/" + entryPath;
        // Deduplicate consecutive slashes
        var sb = new System.Text.StringBuilder(combined.Length);
        bool lastWasSlash = false;
        foreach (char c in combined)
        {
            if (c == '/')
            {
                if (!lastWasSlash)
                    sb.Append(c);
                lastWasSlash = true;
            }
            else
            {
                sb.Append(c);
                lastWasSlash = false;
            }
        }
        string result = sb.ToString();
        // Strip "../../../" prefix
        const string prefix = "../../../";
        if (result.StartsWith(prefix))
            result = result.Substring(prefix.Length);
        return result;
    }

    /// <summary>
    /// Standard AES-256-ECB decrypt (no byte-swapping, used by game's original PAK files)
    /// </summary>
    private static byte[] StandardAesDecrypt(byte[] data, byte[] key)
    {
        int paddedLength = (data.Length + 15) & ~15;
        byte[] paddedData = new byte[paddedLength];
        Array.Copy(data, paddedData, data.Length);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(paddedData, 0, paddedData.Length);
    }

    /// <summary>
    /// AES decrypt with UE4's custom byte-swapping pattern (from data.rs)
    /// </summary>
    private static byte[] AesDecrypt(byte[] data, byte[] key)
    {
        int paddedLength = (data.Length + 15) & ~15;
        byte[] paddedData = new byte[paddedLength];
        Array.Copy(data, paddedData, data.Length);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        byte[] result = new byte[paddedData.Length];
        // Create decryptor once outside the loop
        using var decryptor = aes.CreateDecryptor();
        byte[] block = new byte[16];
        byte[] decrypted = new byte[16];

        for (int i = 0; i < paddedData.Length; i += 16)
        {
            Array.Copy(paddedData, i, block, 0, 16);

            // Reverse each 4-byte chunk BEFORE decryption
            ReverseChunks(block);

            // Decrypt the block (reuse decryptor, reset via TransformBlock)
            decryptor.TransformBlock(block, 0, 16, decrypted, 0);

            // Reverse each 4-byte chunk AFTER decryption
            ReverseChunks(decrypted);

            Array.Copy(decrypted, 0, result, i, 16);
        }

        return result;
    }

    /// <summary>
    /// Reverse each 4-byte chunk in a 16-byte block
    /// </summary>
    private static void ReverseChunks(byte[] block)
    {
        for (int i = 0; i < 16; i += 4)
        {
            (block[i], block[i + 3]) = (block[i + 3], block[i]);
            (block[i + 1], block[i + 2]) = (block[i + 2], block[i + 1]);
        }
    }

    /// <summary>
    /// Read length-prefixed string with null terminator
    /// </summary>
    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length <= 0)
            return "";
        
        byte[] bytes = reader.ReadBytes(length);
        // Remove null terminator
        return Encoding.UTF8.GetString(bytes, 0, length - 1);
    }

    /// <summary>
    /// Decompress data using the specified compression method index
    /// </summary>
    private byte[] Decompress(byte[] data, int uncompressedSize, uint compressionMethodIndex)
    {
        // compressionMethodIndex is 1-based index into _compressionMethods array
        // 0 = no compression
        if (compressionMethodIndex == 0)
            return data;
            
        int methodIdx = (int)compressionMethodIndex - 1;
        if (methodIdx < 0 || methodIdx >= _compressionMethods.Length)
            throw new NotSupportedException($"Compression method index {compressionMethodIndex} out of range");
            
        string methodName = _compressionMethods[methodIdx];
        
        if (string.IsNullOrEmpty(methodName))
            throw new NotSupportedException($"Compression method at index {methodIdx} is empty");
        
        // Match by name (case-insensitive)
        if (methodName.Equals("Zlib", StringComparison.OrdinalIgnoreCase))
            return DecompressZlib(data, uncompressedSize);
        else if (methodName.Equals("Oodle", StringComparison.OrdinalIgnoreCase))
            return DecompressOodle(data, uncompressedSize);
        else if (methodName.Equals("Gzip", StringComparison.OrdinalIgnoreCase))
            return DecompressZlib(data, uncompressedSize); // Gzip uses same decompressor
        else if (methodName.Equals("LZ4", StringComparison.OrdinalIgnoreCase))
            return DecompressLz4(data, uncompressedSize);
        else if (methodName.Equals("Zstd", StringComparison.OrdinalIgnoreCase))
            return DecompressZstd(data, uncompressedSize);
        else
            throw new NotSupportedException($"Compression method '{methodName}' not supported");
    }

    private static byte[] DecompressZlib(byte[] data, int uncompressedSize)
    {
        using var inputStream = new MemoryStream(data);
        using var zlibStream = new System.IO.Compression.ZLibStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
        using var outputStream = new MemoryStream();
        zlibStream.CopyTo(outputStream);
        return outputStream.ToArray();
    }

    private static byte[] DecompressOodle(byte[] data, int uncompressedSize)
    {
        // Use the OodleCompression class which handles DLL loading/downloading
        byte[]? result = OodleCompression.Decompress(data, uncompressedSize);
        if (result == null)
            throw new InvalidDataException($"Oodle decompression failed. Make sure the Oodle native library is in the application directory.");
        return result;
    }

    private static byte[] DecompressLz4(byte[] data, int uncompressedSize)
    {
        // LZ4 decompression
        // For now, throw - can add K4os.Compression.LZ4 NuGet package if needed
        throw new NotSupportedException("LZ4 decompression not yet implemented. Add K4os.Compression.LZ4 package.");
    }

    private static byte[] DecompressZstd(byte[] data, int uncompressedSize)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(data).ToArray();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsStream)
                _stream.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents a PAK file entry
/// </summary>
public class PakEntry
{
    public ulong Offset { get; set; }
    public ulong CompressedSize { get; set; }
    public ulong UncompressedSize { get; set; }
    public int? CompressionSlot { get; set; }
    public bool IsEncrypted { get; set; }
    public uint CompressionBlockSize { get; set; }
    public uint CompressionBlocksCount { get; set; }
    /// <summary>
    /// Pre-calculated block offsets (relative to entry.Offset). Each tuple is (start, end).
    /// </summary>
    public List<(ulong start, ulong end)>? Blocks { get; set; }
}
