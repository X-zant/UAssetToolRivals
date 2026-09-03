using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UAssetTool.IoStore;

/// <summary>
/// Creates companion PAK files for IoStore bundles.
///
/// A companion .pak must exist beside every .utoc/.ucas: the engine discovers mods by
/// enumerating *.pak under ~mods and only then opens the sibling container. A .utoc with
/// no .pak is never opened at all.
///
/// The pak does NOT need to contain anything. An empty stub (mount point "/", zero
/// entries) mounts its container exactly like a populated one -- the same shape the retail
/// pakchunk*.pak stubs use, and it comes out to 400 bytes.
///
/// This writer used to always embed a "chunknames" entry: a plaintext, newline-separated
/// manifest of every asset path the bundle overrides. The loader never reads it; it was
/// only ever a convenience for external tooling. It is off by default now -- see
/// <paramref name="includeChunkNames"/>.
///
/// Reference: repak-gui/src/install_mod/install_mod_logic/iotoc.rs lines 269-288
/// </summary>
public static class ChunkNamesPakWriter
{
    /// <summary>Mount point used by retail stub paks and by empty companion paks.</summary>
    public const string StubMountPoint = "/";

    /// <summary>Mount point required when the pak carries real, path-mounted entries.</summary>
    public const string ContentMountPoint = "../../../";

    /// <summary>The in-pak path of the legacy manifest entry.</summary>
    public const string ChunkNamesEntry = "chunknames";

    /// <summary>
    /// Create a companion PAK file for an IoStore bundle.
    /// </summary>
    /// <param name="pakPath">Output path for the .pak file</param>
    /// <param name="filePaths">Paths to list in the manifest (ignored unless <paramref name="includeChunkNames"/>)</param>
    /// <param name="mountPoint">
    /// Mount point. Ignored when the resulting pak ends up with no entries at all -- an empty
    /// pak is always written with <see cref="StubMountPoint"/> to match the retail stub shape.
    /// </param>
    /// <param name="pathHashSeed">Path hash seed (default: 0)</param>
    /// <param name="aesKeyHex">AES key in hex format (default: Marvel Rivals key)</param>
    /// <param name="rawFiles">
    /// Optional "hybrid" payload: arbitrary (in-PAK path, bytes) pairs stored as real, loose
    /// file entries inside the companion PAK. Used for non-Unreal assets (e.g. .bnk/.wem audio,
    /// raw .png, .bin) that the game mounts by path directly rather than through the IoStore.
    /// These are NOT listed in the manifest.
    /// </param>
    /// <param name="includeChunkNames">
    /// Embed the "chunknames" manifest. Defaults to false: it is not required to mount, and it
    /// enumerates in plaintext exactly which game assets the bundle replaces.
    /// </param>
    public static void Create(
        string pakPath,
        IEnumerable<string> filePaths,
        string mountPoint = ContentMountPoint,
        ulong pathHashSeed = 0,
        string? aesKeyHex = null,
        IEnumerable<(string inPakPath, byte[] data)>? rawFiles = null,
        bool includeChunkNames = false)
    {
        var raw = rawFiles?.ToList() ?? new List<(string inPakPath, byte[] data)>();

        // With no manifest and no hybrid payload the pak has no entries, so it must use the
        // stub mount point: "../../../" over an empty index is not a shape retail ever emits.
        bool willBeEmpty = !includeChunkNames && raw.Count == 0;
        string effectiveMount = willBeEmpty ? StubMountPoint : mountPoint;

        using var pakWriter = new PakWriter(effectiveMount, pathHashSeed, aesKeyHex);

        int listed = 0;
        if (includeChunkNames)
        {
            string chunkNamesContent = string.Join("\n", filePaths);
            pakWriter.AddEntry(ChunkNamesEntry, Encoding.UTF8.GetBytes(chunkNamesContent));
            listed = chunkNamesContent.Length == 0 ? 0 : chunkNamesContent.Split('\n').Length;
        }

        foreach (var (inPakPath, data) in raw)
            pakWriter.AddEntry(inPakPath, data);

        pakWriter.Write(pakPath);

        Console.Error.WriteLine($"[ChunkNamesPakWriter] Created companion PAK: {pakPath}");
        if (includeChunkNames)
            Console.Error.WriteLine($"[ChunkNamesPakWriter]   manifest entries listed: {listed}");
        else
            Console.Error.WriteLine($"[ChunkNamesPakWriter]   manifest omitted, mount point {effectiveMount}");
        if (raw.Count > 0)
            Console.Error.WriteLine($"[ChunkNamesPakWriter]   Raw (hybrid) files embedded: {raw.Count}");
    }

    /// <summary>
    /// Write an empty 400-byte stub pak: mount point "/", zero entries. Structurally identical
    /// to the retail pakchunk*.pak stubs apart from index hash and path hash seed.
    /// </summary>
    public static void CreateStub(string pakPath, ulong pathHashSeed = 0, string? aesKeyHex = null)
        => Create(pakPath, Array.Empty<string>(), StubMountPoint, pathHashSeed, aesKeyHex, null, includeChunkNames: false);

    /// <summary>
    /// Create a complete IoStore bundle (utoc + ucas + pak) from legacy assets.
    /// </summary>
    /// <param name="outputBasePath">Base path without extension (e.g., "C:/Mods/MyMod_P")</param>
    /// <param name="assets">Dictionary of relative paths to asset data</param>
    /// <param name="mountPoint">Mount point (default: "../../../")</param>
    /// <param name="pathHashSeed">Path hash seed (default: 0)</param>
    /// <param name="aesKeyHex">AES key in hex format (default: Marvel Rivals key)</param>
    /// <param name="includeChunkNames">Embed the manifest in the companion pak (default: false)</param>
    public static void CreateIoStoreBundle(
        string outputBasePath,
        Dictionary<string, byte[]> assets,
        string mountPoint = ContentMountPoint,
        ulong pathHashSeed = 0,
        string? aesKeyHex = null,
        bool includeChunkNames = false)
    {
        string utocPath = outputBasePath + ".utoc";
        string pakPath = outputBasePath + ".pak";

        // Create IoStore container
        using var ioStoreWriter = new IoStoreWriter(
            utocPath,
            EIoStoreTocVersion.PerfectHashWithOverflow,
            EIoContainerHeaderVersion.OptionalSegmentPackages,
            mountPoint);

        var filePaths = new List<string>();

        foreach (var (relativePath, data) in assets)
        {
            // Create chunk ID from package name
            string packageName = relativePath;
            if (packageName.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                packageName = packageName[..^7];
            else if (packageName.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
                packageName = packageName[..^5];

            var packageId = FPackageId.FromName("/" + packageName.Replace('\\', '/'));
            var chunkId = FIoChunkId.FromPackageId(packageId, 0, EIoChunkType.ExportBundleData);

            // Create store entry
            var storeEntry = new StoreEntry
            {
                ExportCount = 1,
                ExportBundleCount = 1,
                LoadOrder = 0
            };

            // Write chunk
            string fullPath = mountPoint + relativePath.Replace('\\', '/');
            ioStoreWriter.WritePackageChunk(chunkId, fullPath, data, storeEntry);

            filePaths.Add(relativePath.Replace('\\', '/'));
        }

        // Complete IoStore
        ioStoreWriter.Complete();

        // Create companion PAK
        Create(pakPath, filePaths, mountPoint, pathHashSeed, aesKeyHex, null, includeChunkNames);

        Console.Error.WriteLine($"[CreateIoStoreBundle] Created complete IoStore bundle:");
        Console.Error.WriteLine($"[CreateIoStoreBundle]   {utocPath}");
        Console.Error.WriteLine($"[CreateIoStoreBundle]   {Path.ChangeExtension(utocPath, ".ucas")}");
        Console.Error.WriteLine($"[CreateIoStoreBundle]   {pakPath}");
    }
}
