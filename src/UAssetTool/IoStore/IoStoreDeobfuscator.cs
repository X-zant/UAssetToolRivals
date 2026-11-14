using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UAssetTool.IoStore;

/// <summary>
/// Turns an obfuscated (AES-encrypted) IoStore mod back into a plain one without extracting or
/// repackaging anything.
///
/// Obfuscation changes two things and nothing else. Each compression block in the .ucas is
/// padded to 16 bytes and AES-256-ECB encrypted where it sits, and the .utoc gets the Encrypted
/// container flag. The companion .pak is unaffected: every companion pak, obfuscated or not, has
/// its index encrypted with the game's own key, as the game's own stub paks do. The block table
/// records each block's real compressed size, so decrypting a block in place leaves exactly the
/// bytes a plain container holds at that offset, followed by padding nothing reads. Offsets,
/// sizes, chunk ids and chunk hashes all stay valid, so no Oodle work and no Zen conversion is
/// involved: the cost is one sequential pass over the .ucas.
///
/// The directory index is decrypted too if it was encrypted. This tool never encrypts it, but
/// other tools may, so it is detected rather than assumed.
///
/// Output is written to temporary files and moved into place only once everything succeeded, so
/// an interrupted run never leaves a half-decrypted container behind.
/// </summary>
public static class IoStoreDeobfuscator
{
    public const string DefaultAesKey = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";

    public sealed class Result
    {
        public bool WasEncrypted { get; set; }
        public int BlocksDecrypted { get; set; }
        public bool DirectoryIndexDecrypted { get; set; }
        public string UtocPath { get; set; } = "";
        public string UcasPath { get; set; } = "";
        public string? PakPath { get; set; }
        public string Message { get; set; } = "";
    }

    private sealed class TocLayout
    {
        public EIoStoreTocVersion Version;
        public long ContainerFlagsOffset;
        public EIoContainerFlags ContainerFlags;
        public uint PartitionCount;
        public long DirectoryIndexOffset;
        public uint DirectoryIndexSize;
        public List<FIoStoreTocCompressedBlockEntryReadable> Blocks = new();
    }

    /// <summary>
    /// Deobfuscate the container at <paramref name="utocPath"/>. With <paramref name="outputBase"/>
    /// null the files are replaced in place; otherwise plain copies are written to
    /// outputBase + .utoc/.ucas/.pak and the originals are left untouched.
    /// </summary>
    public static Result Deobfuscate(string utocPath, string? outputBase = null, string? aesKeyHex = null)
    {
        if (!utocPath.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected a .utoc path", nameof(utocPath));
        if (!File.Exists(utocPath))
            throw new FileNotFoundException("Container not found", utocPath);

        string ucasPath = Path.ChangeExtension(utocPath, ".ucas");
        string pakPath = Path.ChangeExtension(utocPath, ".pak");
        if (!File.Exists(ucasPath))
            throw new FileNotFoundException("Matching .ucas not found", ucasPath);

        byte[] key = ParseKey(aesKeyHex ?? DefaultAesKey);
        byte[] toc = File.ReadAllBytes(utocPath);
        TocLayout layout = ReadLayout(toc);

        string outUtoc = outputBase != null ? outputBase + ".utoc" : utocPath;
        string outUcas = outputBase != null ? outputBase + ".ucas" : ucasPath;
        string outPak = outputBase != null ? outputBase + ".pak" : pakPath;
        var result = new Result { UtocPath = outUtoc, UcasPath = outUcas, PakPath = File.Exists(pakPath) ? outPak : null };

        if (!layout.ContainerFlags.HasFlag(EIoContainerFlags.Encrypted))
        {
            result.Message = "Container is not obfuscated; nothing to do.";
            if (outputBase != null)
            {
                CopyThrough(utocPath, outUtoc);
                CopyThrough(ucasPath, outUcas);
                if (File.Exists(pakPath)) CopyThrough(pakPath, outPak);
            }
            return result;
        }

        result.WasEncrypted = true;

        // Signed containers carry block hashes that decryption would invalidate. Mods are not
        // signed, so refuse rather than quietly producing a container that fails verification.
        if (layout.ContainerFlags.HasFlag(EIoContainerFlags.Signed))
            throw new NotSupportedException("Container is signed; decrypting it would invalidate its signatures.");
        if (layout.PartitionCount > 1)
            throw new NotSupportedException($"Container has {layout.PartitionCount} partitions; only single-partition mods are supported.");

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(outUcas));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        string tmpUcas = outUcas + ".deobf.tmp";
        string tmpUtoc = outUtoc + ".deobf.tmp";
        try
        {
            // 1. .ucas: copy once, then decrypt every block where it sits.
            File.Copy(ucasPath, tmpUcas, overwrite: true);
            result.BlocksDecrypted = DecryptBlocks(tmpUcas, layout.Blocks, key);

            // AES-ECB has no integrity check, so a wrong key would "succeed" and write garbage.
            // Decompressing one block proves the key before anything is replaced.
            VerifyKey(tmpUcas, layout);

            // 2. .utoc: clear the Encrypted flag, and decrypt the directory index if needed.
            byte[] newToc = (byte[])toc.Clone();
            newToc[layout.ContainerFlagsOffset] = (byte)(layout.ContainerFlags & ~EIoContainerFlags.Encrypted);
            if (layout.DirectoryIndexSize > 0 && !LooksLikePlainDirectoryIndex(newToc, layout.DirectoryIndexOffset, layout.DirectoryIndexSize))
            {
                if (layout.DirectoryIndexSize % 16 != 0)
                    throw new InvalidDataException("Directory index is neither readable nor a whole number of AES blocks.");
                byte[] dir = new byte[layout.DirectoryIndexSize];
                Array.Copy(newToc, layout.DirectoryIndexOffset, dir, 0, dir.Length);
                DecryptInPlace(dir, key);
                if (!LooksLikePlainDirectoryIndex(dir, 0, (uint)dir.Length))
                    throw new InvalidDataException("Directory index did not decrypt to a readable index; wrong AES key?");
                Array.Copy(dir, 0, newToc, layout.DirectoryIndexOffset, dir.Length);
                result.DirectoryIndexDecrypted = true;
            }
            File.WriteAllBytes(tmpUtoc, newToc);

            // Everything succeeded: move the results into place. The companion .pak needs no
            // change; with --output it is copied alongside so the bundle stays complete.
            ReplaceFile(tmpUcas, outUcas);
            ReplaceFile(tmpUtoc, outUtoc);
            if (File.Exists(pakPath) && outputBase != null) CopyThrough(pakPath, outPak);
        }
        finally
        {
            foreach (var t in new[] { tmpUcas, tmpUtoc })
                if (File.Exists(t)) File.Delete(t);
        }

        result.Message = $"Deobfuscated: {result.BlocksDecrypted} blocks decrypted"
            + (result.DirectoryIndexDecrypted ? ", directory index decrypted" : "");
        return result;
    }

    /// <summary>
    /// Walk the .utoc the same way <see cref="IoStoreReader"/> does, recording where the flags byte
    /// and the directory index sit and every compression block's offset and size.
    /// </summary>
    private static TocLayout ReadLayout(byte[] toc)
    {
        using var ms = new MemoryStream(toc);
        using var reader = new BinaryReader(ms);
        var layout = new TocLayout();

        byte[] magic = reader.ReadBytes(16);
        if (!magic.SequenceEqual(FIoStoreTocHeader.TOC_MAGIC))
            throw new InvalidDataException("Not an IoStore .utoc (bad magic)");

        layout.Version = (EIoStoreTocVersion)reader.ReadByte();
        reader.ReadByte();
        reader.ReadUInt16();
        uint tocHeaderSize = reader.ReadUInt32();
        uint tocEntryCount = reader.ReadUInt32();
        uint blockCount = reader.ReadUInt32();
        uint blockEntrySize = reader.ReadUInt32();
        uint methodNameCount = reader.ReadUInt32();
        uint methodNameLength = reader.ReadUInt32();
        reader.ReadUInt32();                      // compression block size
        layout.DirectoryIndexSize = reader.ReadUInt32();
        layout.PartitionCount = reader.ReadUInt32();
        reader.ReadUInt64();                      // container id
        reader.ReadBytes(16);                     // encryption key guid
        layout.ContainerFlagsOffset = ms.Position;
        layout.ContainerFlags = (EIoContainerFlags)reader.ReadByte();
        reader.ReadByte();
        reader.ReadUInt16();
        uint perfectHashSeedsCount = reader.ReadUInt32();
        reader.ReadUInt64();                      // partition size
        uint chunksWithoutPerfectHash = reader.ReadUInt32();

        if (blockEntrySize != 12)
            throw new InvalidDataException($"Unexpected compression block entry size {blockEntrySize}");

        ms.Position = tocHeaderSize;
        for (int i = 0; i < tocEntryCount; i++) FIoChunkId.Read(reader, layout.Version);
        for (int i = 0; i < tocEntryCount; i++) FIoOffsetAndLengthReadable.Read(reader);

        if (layout.Version >= EIoStoreTocVersion.PerfectHashWithOverflow)
            ms.Position += perfectHashSeedsCount * 4L + chunksWithoutPerfectHash * 4L;
        else if (layout.Version >= EIoStoreTocVersion.PerfectHash)
            ms.Position += perfectHashSeedsCount * 4L;

        for (int i = 0; i < blockCount; i++)
            layout.Blocks.Add(FIoStoreTocCompressedBlockEntryReadable.Read(reader));

        ms.Position += methodNameCount * (long)methodNameLength;

        if (layout.ContainerFlags.HasFlag(EIoContainerFlags.Signed))
        {
            uint signatureSize = reader.ReadUInt32();
            ms.Position += signatureSize * 2L + blockCount * 20L;
        }

        layout.DirectoryIndexOffset = ms.Position;
        if (layout.DirectoryIndexOffset + layout.DirectoryIndexSize > toc.Length)
            throw new InvalidDataException("Directory index runs past the end of the .utoc");
        return layout;
    }

    /// <summary>
    /// Decrypt each block in place. A block occupies its compressed size rounded up to 16 bytes,
    /// which is exactly what the writer padded and encrypted.
    /// </summary>
    private static int DecryptBlocks(string ucasPath, List<FIoStoreTocCompressedBlockEntryReadable> blocks, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();

        using var fs = new FileStream(ucasPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        long length = fs.Length;
        byte[] buffer = Array.Empty<byte>();
        int count = 0;

        // Sequential order keeps the pass a single forward sweep over the file.
        foreach (var block in blocks.OrderBy(b => b.Offset))
        {
            int size = (int)((block.CompressedSize + 15u) & ~15u);
            if (size == 0) continue;
            if ((long)block.Offset + size > length)
                throw new InvalidDataException($"Block at {block.Offset} ({size} bytes) runs past the end of the .ucas");

            if (buffer.Length < size) buffer = new byte[size];
            fs.Position = (long)block.Offset;
            ReadExactly(fs, buffer, size);
            for (int i = 0; i < size; i += 16)
                decryptor.TransformBlock(buffer, i, 16, buffer, i);
            fs.Position = (long)block.Offset;
            fs.Write(buffer, 0, size);
            count++;
        }
        fs.Flush(true);
        return count;
    }

    /// <summary>
    /// Decompress the first Oodle block of the decrypted .ucas. Garbage from a wrong key will not
    /// decompress. Containers with no compressed blocks have nothing to check against and pass.
    /// </summary>
    private static void VerifyKey(string ucasPath, TocLayout layout)
    {
        var probe = layout.Blocks.FirstOrDefault(b => b.CompressionMethodIndex != 0 && b.CompressedSize > 0);
        if (probe.CompressedSize == 0) return;
        if (!OodleCompression.IsAvailable)
            throw new InvalidOperationException("Oodle is unavailable, so the AES key cannot be verified; refusing to write an unverified result.");

        byte[] data = new byte[probe.CompressedSize];
        using (var fs = new FileStream(ucasPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Position = (long)probe.Offset;
            ReadExactly(fs, data, data.Length);
        }

        byte[]? decompressed = null;
        try { decompressed = OodleCompression.Decompress(data, (int)probe.UncompressedSize); }
        catch { }
        if (decompressed == null || decompressed.Length != probe.UncompressedSize)
            throw new InvalidDataException("Decrypted data does not decompress: the AES key is wrong, nothing was changed.");
    }

    /// <summary>
    /// A plain directory index opens with the mount point as an FString: a small positive length,
    /// then that many bytes of text ending in a null, such as "../../../".
    /// </summary>
    private static bool LooksLikePlainDirectoryIndex(byte[] data, long offset, uint size)
    {
        if (size < 8 || offset + 4 > data.Length) return false;
        int len = BitConverter.ToInt32(data, (int)offset);
        if (len <= 0 || len > 1024 || 4 + len > size) return false;
        for (int i = 0; i < len - 1; i++)
        {
            byte c = data[offset + 4 + i];
            if (c < 0x20 || c > 0x7E) return false;
        }
        return data[offset + 4 + len - 1] == 0;
    }

    private static void DecryptInPlace(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        for (int i = 0; i < data.Length; i += 16)
            decryptor.TransformBlock(data, i, 16, data, i);
    }

    private static byte[] ParseKey(string hex)
    {
        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length != 64)
            throw new ArgumentException("AES key must be 32 bytes of hex (64 characters)");
        return Convert.FromHexString(hex);
    }

    private static void ReadExactly(Stream s, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buffer, read, count - read);
            if (n == 0) throw new EndOfStreamException("Unexpected end of .ucas");
            read += n;
        }
    }

    private static void ReplaceFile(string tmp, string target)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Move(tmp, target, overwrite: true);
    }

    private static void CopyThrough(string src, string dst)
    {
        if (Path.GetFullPath(src).Equals(Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(dst));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(src, dst, overwrite: true);
    }
}
