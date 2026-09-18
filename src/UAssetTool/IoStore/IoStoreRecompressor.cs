using System;
using System.Collections.Generic;
using System.IO;

namespace UAssetTool.IoStore;

/// <summary>
/// IoStore recompressor - recompresses IoStore containers with Oodle compression
/// Equivalent to retoc::recompress_iostore()
/// </summary>
public static class IoStoreRecompressor
{
    private const string DEFAULT_AES_KEY_HEX = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";

    /// <summary>
    /// Recompress an IoStore container by reading all chunks and writing them back with Oodle compression.
    /// This is useful for mods that have uncompressed .ucas files.
    /// </summary>
    /// <param name="utocPath">Path to the .utoc file</param>
    /// <param name="aesKeyHex">AES key in hex format (optional, defaults to Marvel Rivals key)</param>
    /// <returns>Path to the recompressed .utoc file</returns>
    public static string Recompress(string utocPath, string? aesKeyHex = null)
    {
        aesKeyHex ??= DEFAULT_AES_KEY_HEX;
        byte[] aesKey = ParseAesKey(aesKeyHex);

        string basePath = Path.ChangeExtension(utocPath, null);
        string ucasPath = basePath + ".ucas";
        // Use a temp subdirectory so the writer's Path.ChangeExtension produces correct .ucas path
        string tempDir = basePath + "_recomp_tmp";
        Directory.CreateDirectory(tempDir);
        string tempUtocPath = Path.Combine(tempDir, Path.GetFileName(utocPath));
        string tempUcasPath = Path.Combine(tempDir, Path.GetFileName(ucasPath));

        Console.Error.WriteLine($"[IoStoreRecompressor] Recompressing: {Path.GetFileName(utocPath)}");

        // Read all chunks into memory, preserving original TOC metadata
        var chunks = new List<(FIoChunkId ChunkId, string? Path, byte[] Data)>();
        FIoChunkId? containerHeaderChunkId = null;
        byte[]? containerHeaderData = null;
        EIoStoreTocVersion tocVersion;
        string mountPoint;
        FIoContainerId originalContainerId;
        byte[] originalEncryptionKeyGuid;
        uint originalCompressionBlockSize;
        byte[]? rawDirectoryIndex;

        using (var reader = new IoStoreReader(utocPath, aesKey))
        {
            var toc = reader.Toc;
            tocVersion = toc.Version;
            mountPoint = toc.MountPoint;
            originalContainerId = toc.ContainerId;
            originalEncryptionKeyGuid = toc.EncryptionKeyGuid;
            originalCompressionBlockSize = toc.CompressionBlockSize;
            rawDirectoryIndex = toc.RawDirectoryIndex;
            int chunkIndex = 0;

            Console.Error.WriteLine($"[IoStoreRecompressor] Original: ContainerId=0x{originalContainerId.Value:X16}, BlockSize={originalCompressionBlockSize}");

            foreach (var chunkId in reader.GetChunks())
            {
                try
                {
                    byte[] data = reader.ReadChunk(chunkId);
                    string? path = reader.GetChunkPath(chunkId);

                    // Separate container header - it needs special handling
                    if (chunkId.ChunkType == EIoChunkType.ContainerHeader)
                    {
                        containerHeaderChunkId = chunkId;
                        containerHeaderData = data;
                    }
                    else
                    {
                        chunks.Add((chunkId, path, data));
                    }
                    chunkIndex++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[IoStoreRecompressor] Warning: Failed to read chunk {chunkIndex}: {ex.Message}");
                }
            }
        } // Reader disposed here - files are now unlocked

        Console.Error.WriteLine($"[IoStoreRecompressor] Read {chunks.Count} chunks + container header, writing with Oodle compression...");

        // Create new IoStore with Oodle compression, preserving original TOC metadata:
        // - ContainerId: must match the container header's ID or the game can't resolve packages
        // - CompressionBlockSize: must match original to avoid block alignment issues
        // - EncryptionKeyGuid: preserved from original
        // Pass null for containerHeaderVersion so the writer does NOT generate a new container header
        // We pass through the original container header uncompressed instead
        using (var writer = new IoStoreWriter(
            tempUtocPath,
            tocVersion,
            null, // Don't generate new container header - we pass through the original
            mountPoint,
            enableCompression: true,
            enableEncryption: false,
            aesKeyHex: aesKeyHex,
            containerId: originalContainerId,
            compressionBlockSize: originalCompressionBlockSize))
        {
            // Preserve original encryption key GUID in the TOC header
            writer.SetEncryptionKeyGuid(originalEncryptionKeyGuid);

            // Preserve original directory index structure (hierarchical tree)
            // instead of rebuilding it (which creates a flat structure)
            if (rawDirectoryIndex != null)
                writer.SetRawDirectoryIndex(rawDirectoryIndex);

            // Write all regular chunks (compressed)
            foreach (var (chunkId, path, data) in chunks)
            {
                writer.WriteChunk(chunkId, path, data);
            }

            // Write original container header uncompressed (preserving exact original data)
            if (containerHeaderChunkId.HasValue && containerHeaderData != null)
            {
                writer.WriteChunkUncompressed(containerHeaderChunkId.Value, containerHeaderData);
            }

            writer.Complete();
        }

        // Replace original files with recompressed ones
        File.Delete(utocPath);
        File.Delete(ucasPath);
        File.Move(tempUtocPath, utocPath);
        File.Move(tempUcasPath, ucasPath);

        // Clean up temp directory
        try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }

        // Get new sizes
        var utocInfo = new FileInfo(utocPath);
        var ucasInfo = new FileInfo(ucasPath);
        Console.Error.WriteLine($"[IoStoreRecompressor] Done. New sizes: .utoc={utocInfo.Length}, .ucas={ucasInfo.Length}");

        return utocPath;
    }

    /// <summary>
    /// Check if recompression would be beneficial (container is not already compressed)
    /// </summary>
    public static bool ShouldRecompress(string utocPath)
    {
        return !IoStoreReader.IsCompressed(utocPath);
    }

    /// <summary>
    /// Parse AES key from hex string
    /// </summary>
    private static byte[] ParseAesKey(string hex)
    {
        hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
}
