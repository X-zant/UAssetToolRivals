using System;
using System.Collections.Generic;

namespace UAssetTool.IoStore;

/// <summary>
/// Writes the companion .pak that sits beside an IoStore container (.utoc/.ucas).
///
/// The pak platform file mounts a container through a .pak of the same name, but needs nothing
/// inside it: the game's own companions (pakchunkCharacter-Windows.pak and the rest) are empty
/// 400-byte paks. So by default this writes an empty pak. The only content it ever carries is
/// "hybrid" files: non-Unreal assets such as .bnk/.wem audio that the game reads by path straight
/// from the pak rather than through the container.
///
/// No chunknames manifest is written. It was never read by the loader, and listing every package
/// in plain text made a mod's contents trivially visible to anyone who opened the pak.
/// </summary>
public static class CompanionPakWriter
{
    /// <summary>
    /// Create the companion pak for an IoStore bundle.
    /// </summary>
    /// <param name="pakPath">Output path for the .pak file</param>
    /// <param name="mountPoint">Mount point (default: "../../../")</param>
    /// <param name="pathHashSeed">Path hash seed (default: 0)</param>
    /// <param name="aesKeyHex">AES key in hex, or null for a plain pak</param>
    /// <param name="rawFiles">
    /// Optional hybrid payload: (in-pak path, bytes) pairs stored as real loose entries.
    /// </param>
    public static void Create(
        string pakPath,
        string mountPoint = "../../../",
        ulong pathHashSeed = 0,
        string? aesKeyHex = null,
        IEnumerable<(string inPakPath, byte[] data)>? rawFiles = null)
    {
        using var pakWriter = new PakWriter(mountPoint, pathHashSeed, aesKeyHex);

        int rawCount = 0;
        if (rawFiles != null)
        {
            foreach (var (inPakPath, data) in rawFiles)
            {
                pakWriter.AddEntry(inPakPath, data);
                rawCount++;
            }
        }

        pakWriter.Write(pakPath);

        Console.Error.WriteLine($"[CompanionPakWriter] Created companion PAK: {pakPath}");
        if (rawCount > 0)
            Console.Error.WriteLine($"[CompanionPakWriter]   Raw (hybrid) files embedded: {rawCount}");
    }
}
