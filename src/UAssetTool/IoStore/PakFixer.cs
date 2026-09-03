using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UAssetTool.IoStore;

/// <summary>
/// Rewrites already-built companion PAK files into the retail stub shape.
///
/// Background: a companion pak only has to exist so the engine opens the sibling
/// .utoc/.ucas. It does not need to contain anything. Bundles built by older versions of
/// this tool embed a "chunknames" entry -- a plaintext manifest naming every game asset the
/// bundle replaces -- which serves no loader purpose.
///
/// This fixer drops that entry and rewrites the pak. Real "hybrid" payload entries
/// (.bnk/.wem audio, raw .png/.bin that the game mounts by path) are preserved, because
/// removing those would break the mod.
/// </summary>
public static class PakFixer
{
    public enum FixOutcome
    {
        /// <summary>Pak had no manifest entry; nothing to do.</summary>
        AlreadyClean,
        /// <summary>Pak contained only the manifest; rewritten as an empty stub.</summary>
        RewrittenAsStub,
        /// <summary>Manifest removed; hybrid payload entries preserved.</summary>
        RewrittenWithPayload,
        /// <summary>Pak could not be read or rewritten.</summary>
        Failed
    }

    /// <summary>What happened to the sibling IoStore container, if encryption was requested.</summary>
    public enum ContainerOutcome
    {
        /// <summary>Encryption not requested, or the bundle has no .utoc.</summary>
        NotAttempted,
        /// <summary>Container already carried the Encrypted flag.</summary>
        AlreadyEncrypted,
        /// <summary>Container rewritten with AES-encrypted chunks and the Encrypted flag set.</summary>
        Encrypted,
        /// <summary>Container rewrite failed; the container is left as it was.</summary>
        Failed
    }

    public sealed class FixResult
    {
        public string PakPath { get; init; } = "";
        public FixOutcome Outcome { get; init; }
        public int EntriesBefore { get; init; }
        public int EntriesAfter { get; init; }
        public long SizeBefore { get; init; }
        public long SizeAfter { get; init; }
        public string MountPointBefore { get; init; } = "";
        public string MountPointAfter { get; init; } = "";
        public ContainerOutcome Container { get; init; } = ContainerOutcome.NotAttempted;
        public string? ContainerError { get; init; }
        public string? Error { get; init; }

        public override string ToString()
        {
            string name = Path.GetFileName(PakPath);
            string pak = Outcome switch
            {
                FixOutcome.AlreadyClean => $"  SKIP  {name}  (no manifest, {EntriesBefore} entries)",
                FixOutcome.RewrittenAsStub => $"  FIXED {name}  {SizeBefore:N0}B -> {SizeAfter:N0}B  ({EntriesBefore} -> 0 entries, mount {MountPointBefore} -> {MountPointAfter})",
                FixOutcome.RewrittenWithPayload => $"  FIXED {name}  {SizeBefore:N0}B -> {SizeAfter:N0}B  ({EntriesBefore} -> {EntriesAfter} entries, hybrid payload kept)",
                _ => $"  ERROR {name}: {Error}"
            };
            string container = Container switch
            {
                ContainerOutcome.Encrypted => "  [container: ENCRYPTED]",
                ContainerOutcome.AlreadyEncrypted => "  [container: already encrypted]",
                ContainerOutcome.Failed => $"  [container: FAILED - {ContainerError}]",
                _ => ""
            };
            return pak + container;
        }
    }

    private static bool IsManifest(string entryPath)
        => string.Equals(entryPath.TrimStart('/'), ChunkNamesPakWriter.ChunkNamesEntry, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Inspect a pak without modifying it.
    /// </summary>
    public static (bool hasManifest, int entryCount, string mountPoint, List<string> entries) Inspect(
        string pakPath, string? aesKeyHex = null)
    {
        using var reader = new PakReader(pakPath, aesKeyHex);
        var entries = reader.Files.ToList();
        return (entries.Any(IsManifest), entries.Count, reader.MountPoint, entries);
    }

    /// <summary>
    /// Rewrite one companion pak without its manifest entry.
    /// </summary>
    /// <param name="pakPath">Pak to fix, rewritten in place.</param>
    /// <param name="aesKeyHex">AES key (default: Marvel Rivals key).</param>
    /// <param name="dryRun">Report what would change without writing.</param>
    /// <param name="backup">Write .bak copies before overwriting (pak, and .ucas/.utoc when encrypting).</param>
    /// <param name="encryptContainer">
    /// Also rewrite the sibling .utoc/.ucas with AES-encrypted chunks and the Encrypted
    /// container flag, matching what create_mod_iostore --obfuscate produces. This rewrites
    /// the container payload, not just a header bit.
    /// </param>
    public static FixResult Fix(string pakPath, string? aesKeyHex = null, bool dryRun = false,
                                bool backup = false, bool encryptContainer = false)
    {
        long sizeBefore = new FileInfo(pakPath).Length;

        List<string> entries;
        string mountBefore;
        var payload = new List<(string inPakPath, byte[] data)>();
        ulong seed;

        try
        {
            using var reader = new PakReader(pakPath, aesKeyHex);
            entries = reader.Files.ToList();
            mountBefore = reader.MountPoint;
            seed = reader.PathHashSeed;

            if (!entries.Any(IsManifest))
            {
                // Pak needs no work -- but the container may still need encrypting, so the
                // encryption pass has to run even when the pak is already clean.
                var (co, cerr) = HandleContainer(pakPath, aesKeyHex, dryRun, backup, encryptContainer);
                return new FixResult
                {
                    PakPath = pakPath,
                    Outcome = FixOutcome.AlreadyClean,
                    EntriesBefore = entries.Count,
                    EntriesAfter = entries.Count,
                    SizeBefore = sizeBefore,
                    SizeAfter = sizeBefore,
                    MountPointBefore = mountBefore,
                    MountPointAfter = mountBefore,
                    Container = co,
                    ContainerError = cerr
                };
            }

            // Read every non-manifest entry out before we touch the file on disk.
            foreach (string e in entries.Where(e => !IsManifest(e)))
                payload.Add((e.TrimStart('/'), reader.Get(e)));
        }
        catch (Exception ex)
        {
            return new FixResult
            {
                PakPath = pakPath,
                Outcome = FixOutcome.Failed,
                SizeBefore = sizeBefore,
                Error = ex.Message
            };
        }

        bool willBeStub = payload.Count == 0;
        string mountAfter = willBeStub ? ChunkNamesPakWriter.StubMountPoint : mountBefore;

        if (dryRun)
        {
            var (dco, dcerr) = HandleContainer(pakPath, aesKeyHex, true, backup, encryptContainer);
            return new FixResult
            {
                PakPath = pakPath,
                Outcome = willBeStub ? FixOutcome.RewrittenAsStub : FixOutcome.RewrittenWithPayload,
                EntriesBefore = entries.Count,
                EntriesAfter = payload.Count,
                SizeBefore = sizeBefore,
                SizeAfter = willBeStub ? 400 : -1,
                MountPointBefore = mountBefore,
                MountPointAfter = mountAfter,
                Container = dco,
                ContainerError = dcerr
            };
        }

        try
        {
            if (backup)
            {
                string bak = pakPath + ".bak";
                if (!File.Exists(bak))
                    File.Copy(pakPath, bak);
            }

            // Write to a temp file first so a failure cannot leave a truncated pak in ~mods.
            string tmp = pakPath + ".tmp";
            ChunkNamesPakWriter.Create(
                tmp,
                Array.Empty<string>(),
                mountBefore,
                seed,
                aesKeyHex,
                payload.Count > 0 ? payload : null,
                includeChunkNames: false);

            File.Move(tmp, pakPath, overwrite: true);

            var (co2, cerr2) = HandleContainer(pakPath, aesKeyHex, false, backup, encryptContainer);
            return new FixResult
            {
                PakPath = pakPath,
                Outcome = willBeStub ? FixOutcome.RewrittenAsStub : FixOutcome.RewrittenWithPayload,
                EntriesBefore = entries.Count,
                EntriesAfter = payload.Count,
                SizeBefore = sizeBefore,
                SizeAfter = new FileInfo(pakPath).Length,
                MountPointBefore = mountBefore,
                MountPointAfter = mountAfter,
                Container = co2,
                ContainerError = cerr2
            };
        }
        catch (Exception ex)
        {
            return new FixResult
            {
                PakPath = pakPath,
                Outcome = FixOutcome.Failed,
                SizeBefore = sizeBefore,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Encrypt the sibling IoStore container if asked to and it is not encrypted already.
    /// Returns the outcome plus an error message when the rewrite failed.
    /// </summary>
    private static (ContainerOutcome, string?) HandleContainer(
        string pakPath, string? aesKeyHex, bool dryRun, bool backup, bool encryptContainer)
    {
        if (!encryptContainer) return (ContainerOutcome.NotAttempted, null);

        string utoc = Path.ChangeExtension(pakPath, ".utoc");
        string ucas = Path.ChangeExtension(pakPath, ".ucas");
        if (!File.Exists(utoc) || !File.Exists(ucas))
            return (ContainerOutcome.NotAttempted, null);   // pak-only mod, nothing to encrypt

        try
        {
            if (IoStoreReader.IsEncrypted(utoc))
                return (ContainerOutcome.AlreadyEncrypted, null);

            if (dryRun) return (ContainerOutcome.Encrypted, null);

            if (backup)
            {
                foreach (string f in new[] { utoc, ucas })
                    if (!File.Exists(f + ".bak")) File.Copy(f, f + ".bak");
            }

            // Preserve whatever compression state the container already had -- turning
            // compression on or off here would change more than we intend.
            bool wasCompressed = IoStoreReader.IsCompressed(utoc);
            IoStoreRecompressor.Rewrite(utoc, aesKeyHex, wasCompressed, enableEncryption: true);

            return IoStoreReader.IsEncrypted(utoc)
                ? (ContainerOutcome.Encrypted, null)
                : (ContainerOutcome.Failed, "Encrypted flag not set after rewrite");
        }
        catch (Exception ex)
        {
            return (ContainerOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Fix every .pak under a directory (recursively) or a single .pak file.
    /// </summary>
    public static List<FixResult> FixPath(string path, string? aesKeyHex = null, bool dryRun = false,
                                          bool backup = false, bool encryptContainer = false)
    {
        var targets = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.pak", SearchOption.AllDirectories).OrderBy(p => p).ToList()
            : new List<string> { path };

        return targets.Select(t => Fix(t, aesKeyHex, dryRun, backup, encryptContainer)).ToList();
    }
}
