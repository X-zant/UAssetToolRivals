using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetTool.ZenPackage;

namespace UAssetTool;

/// <summary>
/// Builds a <c>MaterialTagPresets.ini</c> from the game's cooked SkeletalMeshes.
///
/// The INI is consumed by the MaterialTagPlugin in the UE editor: one section per mesh
/// listing every material slot with the gameplay tags the game ships on it, a reverse
/// tag → slots index, and the per-LOD <c>DefaultHiddenMaterials</c> flags.
///
/// Data sources (both read from the cooked asset, no editor-side carrier involved):
/// <list type="bullet">
///   <item>Slot names and tags come from the <c>FSkeletalMaterial</c> array in the
///   SkeletalMesh export's binary extras (<see cref="SkeletalMeshExport.Materials"/>),
///   which Marvel Rivals extends with a trailing <c>FGameplayTagContainer</c>.</item>
///   <item>Hidden flags come from the <c>DefaultHiddenMaterials</c> bool array inside
///   each <c>LODInfo</c> struct (<see cref="HiddenMaterialsReader.ExtractFromCookedMesh"/>),
///   which needs a usmap because the properties are unversioned.</item>
/// </list>
///
/// File format (UTF-8, CRLF):
/// <code>
/// [SK_1014_1014001]
/// SlotCount=14
/// Slot_0=MI_1014001_Weapon|MaterialTag.装备.武器
/// Slot_1=MI_1014001_Head|
/// MaterialTag.装备.武器=MI_1014001_Weapon
/// LOD_0_Hidden=0,0,0,1
/// LODCount=1
/// </code>
/// Tag index lines are sorted ordinally; slot lists inside them follow slot order.
/// LOD lines only appear when at least one LOD carries DefaultHiddenMaterials.
/// </summary>
public static class MaterialTagPresets
{
    private const string DefaultAesKey = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";

    /// <summary>
    /// How this exporter is invoked, for the usage text. The standalone
    /// MaterialTagPresetExporter sets its own name; the UAssetTool subcommand keeps the default.
    /// </summary>
    public static string UsageName { get; set; } = "UAssetTool export_material_tag_presets";

    public sealed class SlotPreset
    {
        public string Name { get; init; } = "";
        public List<string> Tags { get; init; } = new();
    }

    public sealed class MeshPreset
    {
        public string MeshName { get; init; } = "";
        public string PackagePath { get; init; } = "";
        public List<SlotPreset> Slots { get; init; } = new();
        /// <summary>One entry per LOD; empty array = LOD has no DefaultHiddenMaterials.</summary>
        public List<bool[]> LodHidden { get; init; } = new();
        public bool HasAnyTag => Slots.Any(s => s.Tags.Count > 0);
        /// <summary>
        /// False when UAssetAPI's layout heuristic could not find an FGameplayTagContainer
        /// after each material (e.g. a null material reference in slot 2). Tags for such a
        /// mesh are unknown rather than empty.
        /// </summary>
        public bool TagContainerDetected { get; init; } = true;
    }

    public sealed class Options
    {
        /// <summary>Only packages whose file name starts with one of these are considered. Empty = every package.</summary>
        public List<string> NamePrefixes { get; } = new() { "SK_" };
        /// <summary>Only packages under this VFS path are considered. Empty = whole game.</summary>
        public string PathPrefix { get; set; } = "Marvel/Content/Marvel/Characters/";
        /// <summary>Optional substring filters (OR) applied on top of the prefix checks.</summary>
        public List<string> Filters { get; } = new();
        public List<string> AesKeys { get; } = new();
        /// <summary>Emit meshes that have no tag on any slot (the reference INI omits them).</summary>
        public bool IncludeUntagged { get; set; }
        /// <summary>
        /// Write slot names without the FName number suffix ("MI_Fire_4" instead of
        /// "MI_Fire_4_112"). The suffix is what the editor shows and what MaterialTag
        /// injection matches on, so it is kept by default; this reproduces older INIs.
        /// </summary>
        public bool BareSlotNames { get; set; }
        /// <summary>
        /// Omit the <c>; /Game/...</c> package-path line under each section header. The
        /// original hand-made preset INI had no such line, so dropping it is what makes the
        /// output diffable against that file.
        /// </summary>
        public bool NoPathComment { get; set; }
        /// <summary>Always decompress whole package chunks instead of the mesh-export prefix (diagnostics).</summary>
        public bool FullRead { get; set; }
        /// <summary>Log every package as it is processed.</summary>
        public bool Verbose { get; set; }
        /// <summary>Packages converted concurrently. Each in-flight package costs a few copies of its size, so keep this small.</summary>
        public int MaxParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        /// <summary>Packages processed between cache evictions; bounds the context's raw-chunk cache.</summary>
        public int BatchSize { get; set; } = 16;
        /// <summary>
        /// How much of the SkeletalMesh export to read. Properties, the material list and the
        /// reference skeleton sit at the front of the export; the LOD render data behind them
        /// is the bulk of the package and is never needed here.
        /// </summary>
        public int MeshPrefixBytes { get; set; } = 1024 * 1024;
        public bool Debug { get; set; } = Environment.GetEnvironmentVariable("DEBUG") == "1";
    }

    public sealed class Result
    {
        public List<MeshPreset> Presets { get; } = new();
        public int Candidates { get; set; }
        public int SkeletalMeshes { get; set; }
        public int Failed { get; set; }
        public int Untagged { get; set; }
        /// <summary>Meshes whose material layout didn't reveal a tag container; see <see cref="MeshPreset.TagContainerDetected"/>.</summary>
        public int NoTagContainer { get; set; }
        /// <summary>SkeletalMeshes whose material list could not be located in the cooked extras, so they were skipped.</summary>
        public int Unparseable { get; set; }
        public List<string> Errors { get; } = new();
    }

    // ------------------------------------------------------------------ CLI

    public static int RunCli(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine($"Usage: {UsageName} <paks_directory> <output.ini> --usmap <path> [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Scans the game's IoStore containers for SkeletalMeshes and writes a MaterialTagPresets.ini");
            Console.Error.WriteLine("with per-slot gameplay tags and per-LOD DefaultHiddenMaterials flags.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --usmap <path>          - Mappings file (required: LODInfo is unversioned)");
            Console.Error.WriteLine("  --path <vfs_prefix>     - Only packages under this path (default: Marvel/Content/Marvel/Characters/)");
            Console.Error.WriteLine("  --all-paths             - Scan the whole game instead of the Characters directory");
            Console.Error.WriteLine("  --filter <patterns...>  - Only meshes whose path contains one of the patterns");
            Console.Error.WriteLine("  --prefix <prefixes...>  - Package name prefixes to consider (default: SK_)");
            Console.Error.WriteLine("  --all                   - Consider every package name, detect SkeletalMesh by class");
            Console.Error.WriteLine("  --include-untagged      - Also write meshes that carry no tags");
            Console.Error.WriteLine("  --bare-slot-names       - Drop the FName number suffix from slot names (MI_Fire_4 instead of MI_Fire_4_112)");
            Console.Error.WriteLine("  --no-path-comment       - Omit the '; /Game/...' line under each section header");
            Console.Error.WriteLine("  --legacy-format         - Shorthand for --bare-slot-names --no-path-comment (the original INI's exact shape)");
            Console.Error.WriteLine("  --aes <hex>             - AES key (default: Marvel Rivals)");
            Console.Error.WriteLine("  --threads <n>           - Packages converted concurrently (default: half the cores, max 4)");
            Console.Error.WriteLine("  --batch <n>             - Packages per cache-eviction batch (default: 16)");
            Console.Error.WriteLine("  --mesh-prefix-kb <n>    - Bytes of each mesh export to read (default: 1024); the render data behind is skipped");
            Console.Error.WriteLine("  --full-read             - Decompress whole package chunks instead of the mesh prefix (slow; diagnostics)");
            Console.Error.WriteLine("  --verbose               - Log every package as it is processed");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Example:");
            Console.Error.WriteLine($"  {UsageName} \"F:/MarvelRivals/MarvelGame/Marvel/Content/Paks\" MaterialTagPresets.ini --usmap Marvel.usmap");
            return 1;
        }

        string paksPath = args[1];
        string outputPath = args[2];
        string? usmapPath = null;
        var options = new Options();

        for (int i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--usmap" when i + 1 < args.Length:
                    usmapPath = args[++i];
                    break;
                case "--aes":
                case "--aes-key":
                    if (i + 1 < args.Length) options.AesKeys.Add(args[++i]);
                    break;
                case "--filter":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        options.Filters.Add(args[++i]);
                    break;
                case "--prefix":
                    options.NamePrefixes.Clear();
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        options.NamePrefixes.Add(args[++i]);
                    break;
                case "--all":
                    options.NamePrefixes.Clear();
                    break;
                case "--path" when i + 1 < args.Length:
                    options.PathPrefix = args[++i];
                    break;
                case "--all-paths":
                    options.PathPrefix = "";
                    break;
                case "--include-untagged":
                    options.IncludeUntagged = true;
                    break;
                case "--bare-slot-names":
                    options.BareSlotNames = true;
                    break;
                case "--no-path-comment":
                    options.NoPathComment = true;
                    break;
                case "--legacy-format":
                    // Byte-compatible with the original MaterialTagPresets.ini: bare slot
                    // names, no package-path comments. Content still reflects the installed
                    // build, so genuine data changes since that file was made still show up.
                    options.BareSlotNames = true;
                    options.NoPathComment = true;
                    break;
                case "--full-read":
                    options.FullRead = true;
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
                case "--threads" when i + 1 < args.Length && int.TryParse(args[i + 1], out int t) && t > 0:
                    options.MaxParallelism = t;
                    i++;
                    break;
                case "--batch" when i + 1 < args.Length && int.TryParse(args[i + 1], out int b) && b > 0:
                    options.BatchSize = b;
                    i++;
                    break;
                case "--mesh-prefix-kb" when i + 1 < args.Length && int.TryParse(args[i + 1], out int kb) && kb > 0:
                    options.MeshPrefixBytes = kb * 1024;
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return 1;
            }
        }

        if (!Directory.Exists(paksPath))
        {
            Console.Error.WriteLine($"Paks directory not found: {paksPath}");
            return 1;
        }
        if (string.IsNullOrEmpty(usmapPath) || !File.Exists(usmapPath))
        {
            Console.Error.WriteLine("A valid --usmap is required: DefaultHiddenMaterials lives in unversioned LODInfo properties.");
            return 1;
        }

        var mappings = new Usmap(usmapPath);
        using var context = LoadGameContext(paksPath, options.AesKeys, log: Console.WriteLine);

        var result = Collect(context, mappings, options, log: Console.WriteLine);
        WriteIni(outputPath, result.Presets, writePathComments: !options.NoPathComment);

        Console.WriteLine();
        Console.WriteLine($"Candidates scanned: {result.Candidates}");
        Console.WriteLine($"SkeletalMeshes parsed: {result.SkeletalMeshes}");
        Console.WriteLine($"Meshes without tags: {result.Untagged}{(options.IncludeUntagged ? " (included)" : " (omitted)")}");
        if (result.NoTagContainer > 0)
            Console.WriteLine($"Meshes with undetectable tag layout: {result.NoTagContainer} (run with --verbose to list them)");
        if (result.Unparseable > 0)
            Console.WriteLine($"Meshes skipped, material list not locatable: {result.Unparseable} (run with --verbose to list them)");
        Console.WriteLine($"Failed: {result.Failed}");
        Console.WriteLine($"Wrote {result.Presets.Count} preset section(s) to {outputPath}");
        return result.Presets.Count > 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ JSON API

    public static UAssetResponse RunJson(string? paksPath, string? outputPath, string? usmapPath, List<string>? filters, string? aesKey)
    {
        if (string.IsNullOrEmpty(paksPath) || !Directory.Exists(paksPath))
            return new UAssetResponse { Success = false, Message = "game_paks directory not found" };
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "output_path is required" };
        if (string.IsNullOrEmpty(usmapPath) || !File.Exists(usmapPath))
            return new UAssetResponse { Success = false, Message = "usmap_path is required and must exist" };

        var options = new Options();
        if (filters != null) options.Filters.AddRange(filters.Where(f => !string.IsNullOrWhiteSpace(f)));
        if (!string.IsNullOrEmpty(aesKey)) options.AesKeys.Add(aesKey);

        var mappings = new Usmap(usmapPath);
        using var context = LoadGameContext(paksPath, options.AesKeys, log: Console.Error.WriteLine);
        var result = Collect(context, mappings, options, log: Console.Error.WriteLine);
        WriteIni(outputPath, result.Presets, writePathComments: !options.NoPathComment);

        return new UAssetResponse
        {
            Success = result.Presets.Count > 0,
            Message = $"Wrote {result.Presets.Count} preset section(s)",
            Data = new
            {
                output_path = outputPath,
                sections = result.Presets.Count,
                candidates = result.Candidates,
                skeletal_meshes = result.SkeletalMeshes,
                untagged = result.Untagged,
                no_tag_container = result.NoTagContainer,
                unparseable = result.Unparseable,
                failed = result.Failed,
                errors = result.Errors.Take(50).ToList()
            }
        };
    }

    // ------------------------------------------------------------------ Core

    /// <summary>
    /// Load global.utoc plus every top-level game container, mirroring extract_iostore_legacy.
    /// </summary>
    public static FZenPackageContext LoadGameContext(string paksPath, IReadOnlyList<string> aesKeys, Action<string> log)
    {
        var context = new FZenPackageContext();
        if (aesKeys.Count > 0)
            foreach (var key in aesKeys) context.AddAesKey(key);
        else
            context.SetAesKey(DefaultAesKey);

        log($"Loading game containers from: {paksPath}");
        string globalPath = Path.Combine(paksPath, "global.utoc");
        if (File.Exists(globalPath))
        {
            context.LoadContainer(globalPath);
            context.LoadScriptObjectsFromContainer(0);
        }

        // Later containers override earlier ones, so load the base pakchunks first and the
        // patch containers after them in build-number order: the newest patch must win.
        var utocFiles = Directory.GetFiles(paksPath, "*.utoc", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith("global.utoc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => IsPatchContainer(f) ? 1 : 0)
            .ThenBy(f => PatchBuildNumber(f))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var utoc in utocFiles)
        {
            try { context.LoadContainer(utoc); }
            catch (Exception ex) { log($"  Warning: failed to load {Path.GetFileName(utoc)}: {ex.Message}"); }

            // Parsing one container's TOC and directory index churns through far more memory
            // than the index it keeps. Collecting between containers holds the peak down to
            // roughly one container's worth instead of letting 35 containers' garbage pile up.
            GC.Collect();
        }

        // Container indexing leaves a lot of short-lived garbage behind; with server GC that
        // would otherwise sit in the working set for the rest of the run.
        long resident = GC.GetTotalMemory(forceFullCollection: true);
        log($"Containers: {context.ContainerCount}, packages: {context.PackageCount}, resident after load: {resident / (1024 * 1024)} MB");
        return context;
    }

    private static bool IsPatchContainer(string utocPath)
    {
        string name = Path.GetFileNameWithoutExtension(utocPath);
        return name.StartsWith("Patch", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_P", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Largest digit run in the file name, e.g. 3825464 from "Patch_-Windows_1.1.3825464_P".</summary>
    private static long PatchBuildNumber(string utocPath)
    {
        string name = Path.GetFileNameWithoutExtension(utocPath);
        long best = 0, cur = 0;
        foreach (char c in name)
        {
            if (char.IsDigit(c)) { cur = cur * 10 + (c - '0'); best = Math.Max(best, cur); }
            else cur = 0;
        }
        return best;
    }

    /// <summary>
    /// Convert every candidate package to legacy format in memory, parse it, and collect
    /// slot/tag/hidden data for each SkeletalMesh found.
    /// </summary>
    public static Result Collect(FZenPackageContext context, Usmap mappings, Options options, Action<string> log)
    {
        var result = new Result();

        // Cheap pre-filter on the package path so we never touch chunks we don't need.
        var candidates = new List<ulong>();
        foreach (var pkgId in context.GetAllPackageIds())
        {
            string? path = context.GetPackagePath(pkgId);
            if (string.IsNullOrEmpty(path)) continue;

            if (options.NamePrefixes.Count > 0)
            {
                string baseName = path.Substring(path.LastIndexOf('/') + 1);
                if (!options.NamePrefixes.Any(p => baseName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }
            if (!string.IsNullOrEmpty(options.PathPrefix) && !context.PathMatchesFilter(pkgId, options.PathPrefix))
                continue;
            if (options.Filters.Count > 0 && !options.Filters.Any(f => context.PathMatchesFilter(pkgId, f)))
                continue;

            candidates.Add(pkgId);
        }
        // Stable order so batches (and the cache footprint) are reproducible.
        candidates.Sort((a, b) => string.CompareOrdinal(context.GetPackagePath(a), context.GetPackagePath(b)));
        result.Candidates = candidates.Count;
        log($"Scanning {candidates.Count} candidate package(s)...");

        var presets = new List<MeshPreset>(candidates.Count);
        var errors = new List<string>();
        int skeletal = 0, failed = 0, untagged = 0, noContainer = 0, unparseable = 0, done = 0;
        int parallelism = Math.Max(1, options.MaxParallelism);
        int batchSize = Math.Max(parallelism, options.BatchSize);

        // Work in small batches: the context caches every raw chunk it reads, and a
        // SkeletalMesh package can be tens of MB. Evicting after each batch keeps the
        // resident set at roughly (batch + in-flight copies) instead of the whole set.
        for (int start = 0; start < candidates.Count; start += batchSize)
        {
            var batch = candidates.GetRange(start, Math.Min(batchSize, candidates.Count - start));
            var batchPresets = new ConcurrentBag<MeshPreset>();
            var batchErrors = new ConcurrentBag<string>();

            Parallel.ForEach(batch, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, pkgId =>
            {
                string packagePath = context.GetPackagePath(pkgId) ?? $"{pkgId:X16}";
                long allocStart = options.Verbose ? GC.GetAllocatedBytesForCurrentThread() : 0;
                try
                {
                    // Header-only peek: one or two compression blocks, nothing cached.
                    var (header, raw) = PeekHeader(context, pkgId);
                    if (header == null || raw == null) return;

                    int meshExportIndex = FindSkeletalMeshExport(context, header);
                    if (meshExportIndex < 0) return;
                    if (options.Debug) log($"  {packagePath}: SkeletalMesh at export #{meshExportIndex}");

                    var preset = ReadPreset(context, pkgId, packagePath, mappings, header, raw, meshExportIndex, options);
                    if (preset == null)
                    {
                        Interlocked.Increment(ref unparseable);
                        if (options.Verbose) log($"  {packagePath}: material list could not be located, skipped");
                        return;
                    }

                    Interlocked.Increment(ref skeletal);
                    if (!preset.TagContainerDetected)
                    {
                        Interlocked.Increment(ref noContainer);
                        if (options.Verbose) log($"  {packagePath}: no FGameplayTagContainer layout detected, tags unknown");
                    }
                    if (!preset.HasAnyTag)
                    {
                        Interlocked.Increment(ref untagged);
                        if (!options.IncludeUntagged) return;
                    }
                    batchPresets.Add(preset);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    batchErrors.Add($"{packagePath}: {ex.Message}");
                    if (options.Debug) log($"  Failed: {packagePath}: {ex}");
                }
                finally
                {
                    if (options.Verbose)
                        log($"  [{(GC.GetAllocatedBytesForCurrentThread() - allocStart) / (1024 * 1024)}MB allocated] {packagePath}");
                }
            });

            presets.AddRange(batchPresets);
            errors.AddRange(batchErrors);
            context.ClearCache();
            // Each batch leaves ~batch × (prefix + legacy copy + parsed asset) of garbage; with
            // server GC those large-object buffers would otherwise pile up until a gen2 happens.
            GC.Collect();

            done += batch.Count;
            if (options.Verbose)
                log($"  [batch] {done}/{candidates.Count} scanned, {skeletal} mesh(es), heap={GC.GetTotalMemory(false) / (1024 * 1024)}MB, peak working set={Environment.WorkingSet / (1024 * 1024)}MB");
            else if (done % (batchSize * 4) == 0 || done == candidates.Count)
                log($"  {done}/{candidates.Count} scanned, {skeletal} skeletal mesh(es) so far");
        }

        result.SkeletalMeshes = skeletal;
        result.Failed = failed;
        result.Untagged = untagged;
        result.NoTagContainer = noContainer;
        result.Unparseable = unparseable;
        result.Errors.AddRange(errors.OrderBy(e => e, StringComparer.Ordinal));
        // Deterministic output: order by package path.
        result.Presets.AddRange(presets.OrderBy(p => p.PackagePath, StringComparer.Ordinal));

        foreach (var err in result.Errors.Take(20))
            log($"  Failed: {err}");
        if (result.Errors.Count > 20)
            log($"  ... {result.Errors.Count - 20} more failure(s)");

        return result;
    }

    /// <summary>
    /// Index of the first export whose class resolves to /Script/Engine.SkeletalMesh,
    /// or -1. Uses the Zen header only, no conversion.
    /// </summary>
    private static int FindSkeletalMeshExport(FZenPackageContext context, FZenPackageHeader header)
    {
        if (context.ScriptObjects == null) return -1;
        for (int i = 0; i < header.ExportMap.Count; i++)
        {
            var export = header.ExportMap[i];
            if (!export.ClassIndex.IsScriptImport()) continue;
            var scriptObj = context.ResolveScriptImport(export.ClassIndex);
            if (scriptObj == null) continue;
            if (context.ScriptObjects.GetName(scriptObj.ObjectName).Equals("SkeletalMesh", StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private const int HeaderPeekBytes = 64 * 1024;

    /// <summary>
    /// Deserialize a package's zen header from the smallest chunk prefix that holds it.
    /// Returns the header plus the bytes read so far (reused by <see cref="ReadPreset"/>).
    /// </summary>
    private static (FZenPackageHeader? Header, byte[]? Raw) PeekHeader(FZenPackageContext context, ulong pkgId)
    {
        long chunkSize = context.GetPackageChunkSize(pkgId);
        if (chunkSize <= 0) return (null, null);

        // Old container versions don't carry HeaderSize in the summary; just read it all.
        if (context.ContainerHeaderVersion <= EIoContainerHeaderVersion.Initial)
        {
            var full = context.ReadPackagePrefix(pkgId, (int)chunkSize);
            return full == null ? (null, null) : (FZenPackageHeader.Deserialize(full, context.ContainerHeaderVersion), full);
        }

        var raw = context.ReadPackagePrefix(pkgId, (int)Math.Min(HeaderPeekBytes, chunkSize));
        if (raw == null || raw.Length < 8) return (null, null);

        // Summary layout (post-Initial): HasVersioningInfo, HeaderSize, ...
        uint headerSize = BitConverter.ToUInt32(raw, 4);
        if (headerSize > raw.Length && headerSize <= chunkSize)
            raw = context.ReadPackagePrefix(pkgId, (int)headerSize) ?? raw;

        return (FZenPackageHeader.Deserialize(raw, context.ContainerHeaderVersion), raw);
    }

    /// <summary>
    /// Read just enough of the package to cover the SkeletalMesh export's properties,
    /// material list and reference skeleton, convert that slice to legacy format in memory,
    /// and parse it. Every other export (morph targets, user data) is left unparsed, and the
    /// LOD render data behind the skeleton is never decompressed. Falls back to the whole
    /// chunk if the slice turns out to be too short for the material list.
    /// </summary>
    private static MeshPreset? ReadPreset(FZenPackageContext context, ulong pkgId, string packagePath, Usmap mappings,
        FZenPackageHeader header, byte[] raw, int meshExportIndex, Options options)
    {
        long chunkSize = context.GetPackageChunkSize(pkgId);
        var meshEntry = header.ExportMap[meshExportIndex];
        long wanted = header.Summary.HeaderSize + (long)meshEntry.CookedSerialOffset + options.MeshPrefixBytes;
        bool truncated = !options.FullRead && wanted < chunkSize;

        int readBytes = (int)Math.Min(wanted, chunkSize);
        if (options.Verbose)
            Console.Error.WriteLine($"[read] {readBytes / 1024}KB of {chunkSize / 1024}KB, mesh export #{meshExportIndex} at {meshEntry.CookedSerialOffset / 1024}KB: {packagePath}");
        if (raw.Length < readBytes)
            raw = context.ReadPackagePrefix(pkgId, readBytes) ?? raw;

        if (truncated)
            ClampExportSizesToPrefix(header, raw.Length);

        var preset = TryReadPreset(context, pkgId, packagePath, mappings, header, raw, meshExportIndex, options);
        if (preset != null || !truncated)
            return preset;

        // The slice was too short for the material list (or the parse landed on the wrong
        // bytes): pay for the whole chunk once.
        if (options.Verbose)
            Console.Error.WriteLine($"[retry] prefix rejected, reading full {chunkSize / 1024}KB chunk: {packagePath}");
        raw = context.ReadPackagePrefix(pkgId, (int)chunkSize) ?? raw;
        header = FZenPackageHeader.Deserialize(raw, context.ContainerHeaderVersion);
        return TryReadPreset(context, pkgId, packagePath, mappings, header, raw, meshExportIndex, options);
    }

    /// <summary>
    /// Shrink every export's serial size to what the prefix actually contains so the
    /// converter and UAssetAPI never reach past the bytes we read. Exports that start
    /// beyond the prefix become zero-length.
    /// </summary>
    private static void ClampExportSizesToPrefix(FZenPackageHeader header, int rawLength)
    {
        foreach (var export in header.ExportMap)
        {
            long start = header.Summary.HeaderSize + (long)export.CookedSerialOffset;
            long available = rawLength - start;
            if (available <= 0)
                export.CookedSerialSize = 0;
            else if ((long)export.CookedSerialSize > available)
                export.CookedSerialSize = (ulong)available;
        }
    }

    private static MeshPreset? TryReadPreset(FZenPackageContext context, ulong pkgId, string packagePath, Usmap mappings,
        FZenPackageHeader header, byte[] raw, int meshExportIndex, Options options)
    {
        // Context-free conversion: script imports still resolve through the read-only
        // ScriptObjects database, package imports get placeholder names. Nothing here needs
        // an import's real name (slots, tags and LODInfo are all package-local), and this
        // keeps the converter from loading imported packages through the shared context
        // cache, which is not safe to hit from several converters at once.
        var converter = new ZenToLegacyConverter(header, raw, context.ScriptObjects);
        var bundle = converter.Convert(skipBulkData: true);

        // Legacy export order matches the Zen export map, so skip everything but the mesh.
        int[] skips = Enumerable.Range(0, header.ExportMap.Count).Where(i => i != meshExportIndex).ToArray();

        var asset = ParseLegacy(bundle, packagePath, mappings, skips);
        var skel = meshExportIndex < asset.Exports.Count ? asset.Exports[meshExportIndex] as SkeletalMeshExport : null;
        if (skel == null)
        {
            // Order didn't line up; fall back to a full parse and search by type.
            asset = ParseLegacy(bundle, packagePath, mappings, null);
            skel = asset.Exports.OfType<SkeletalMeshExport>().FirstOrDefault();
            if (skel == null)
                return null;
        }

        skel.EnsureExtraDataParsed();
        if (skel.Materials == null || skel.Materials.Count == 0)
            return null;

        // The material list is found by pattern matching inside the export's binary extras.
        // When it locks onto the wrong offset it yields plausible-looking nonsense (bone
        // names as slot names). The reference skeleton is serialized immediately after the
        // real material list, so parsing it is the cheap confirmation that the offset was
        // right; without it the slot names cannot be trusted and the mesh is reported as
        // unparseable rather than written out.
        if (skel.ReferenceSkeleton == null || skel.ReferenceSkeleton.BoneCount == 0)
            return null;

        var preset = new MeshPreset
        {
            MeshName = skel.ObjectName?.ToString() ?? asset.FilePath.Replace(".uasset", ""),
            PackagePath = packagePath,
            // The layout heuristic only needs the material list plus the start of the
            // reference skeleton, which the prefix always covers; a false here is a genuine
            // heuristic miss, not a truncation artifact, so a full re-read would not help.
            TagContainerDetected = skel.SourceHadGameplayTags
        };

        foreach (var mat in skel.Materials)
        {
            string slotName = options.BareSlotNames
                ? mat.MaterialSlotName?.Value?.Value ?? "None"
                : mat.MaterialSlotName?.ToString() ?? "None";
            var slot = new SlotPreset { Name = slotName };
            if (mat.GameplayTagContainer?.GameplayTags != null)
            {
                foreach (var tag in mat.GameplayTagContainer.GameplayTags)
                {
                    string? tagName = tag?.TagName?.ToString();
                    if (!string.IsNullOrEmpty(tagName))
                        slot.Tags.Add(tagName);
                }
            }
            preset.Slots.Add(slot);
        }

        preset.LodHidden.AddRange(HiddenMaterialsReader.ExtractFromCookedMesh(skel));
        return preset;
    }

    private static UAsset ParseLegacy(LegacyAssetBundle bundle, string packagePath, Usmap mappings, int[]? manualSkips)
    {
        var asset = new UAsset(EngineVersion.VER_UE5_3, mappings);
        asset.FilePath = packagePath.Substring(packagePath.LastIndexOf('/') + 1) + ".uasset";
        asset.UseSeparateBulkDataFiles = true;

        // UAssetAPI expects the .uexp bytes appended directly after the .uasset header.
        using var stream = new MemoryStream(bundle.AssetData.Length + bundle.ExportsData.Length);
        stream.Write(bundle.AssetData, 0, bundle.AssetData.Length);
        stream.Write(bundle.ExportsData, 0, bundle.ExportsData.Length);
        stream.Position = 0;
        asset.Read(new AssetBinaryReader(stream, asset), manualSkips);
        return asset;
    }

    // ------------------------------------------------------------------ INI writer

    public static void WriteIni(string outputPath, IReadOnlyList<MeshPreset> presets, bool writePathComments = true)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.Append(BuildIni(presets, writePathComments));
        File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string BuildIni(IReadOnlyList<MeshPreset> presets, bool writePathComments = true)
    {
        const string nl = "\r\n";
        var sb = new StringBuilder();

        sb.Append("; MaterialTag Preset INI - Generated from Marvel Rivals game data").Append(nl);
        sb.Append("; Each section is a mesh name.").Append(nl);
        sb.Append("; SlotCount = number of material slots").Append(nl);
        sb.Append("; Slot_N = SlotName|Tag1,Tag2 (or SlotName| if no tag)").Append(nl);
        sb.Append("; Tag lines: TagName=Slot1, Slot2 (for quick lookup)").Append(nl);
        sb.Append("; LODCount = number of LODs that carry DefaultHiddenMaterials").Append(nl);
        sb.Append("; LOD_N_Hidden = comma-separated 0/1 flags, one per slot (1 = hidden by default)").Append(nl);
        sb.Append("; Used by MaterialTagPlugin to show preset info and tag pills in UE editor,").Append(nl);
        sb.Append("; and to pre-populate the per-LOD DefaultHiddenMaterials list for new mods.").Append(nl);

        foreach (var preset in presets)
        {
            sb.Append(nl);
            sb.Append('[').Append(preset.MeshName).Append(']').Append(nl);
            if (writePathComments)
                sb.Append("; ").Append(preset.PackagePath).Append(nl);
            sb.Append("SlotCount=").Append(preset.Slots.Count).Append(nl);

            // Slot_N=Name|Tag1,Tag2 — tags keep the order they have in the asset.
            var tagToSlots = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            for (int i = 0; i < preset.Slots.Count; i++)
            {
                var slot = preset.Slots[i];
                sb.Append("Slot_").Append(i).Append('=').Append(slot.Name).Append('|')
                  .Append(string.Join(",", slot.Tags)).Append(nl);

                foreach (var tag in slot.Tags)
                {
                    if (!tagToSlots.TryGetValue(tag, out var slots))
                        tagToSlots[tag] = slots = new List<string>();
                    if (!slots.Contains(slot.Name))
                        slots.Add(slot.Name);
                }
            }

            // Reverse index: TagName=Slot1, Slot2 — sorted by tag, slots in slot order.
            foreach (var kv in tagToSlots)
                sb.Append(kv.Key).Append('=').Append(string.Join(", ", kv.Value)).Append(nl);

            // Per-LOD hidden flags, only for LODs that actually carry the property.
            int lodCount = 0;
            for (int lod = 0; lod < preset.LodHidden.Count; lod++)
            {
                var flags = preset.LodHidden[lod];
                if (flags == null || flags.Length == 0) continue;
                sb.Append("LOD_").Append(lod).Append("_Hidden=")
                  .Append(string.Join(",", flags.Select(f => f ? "1" : "0"))).Append(nl);
                lodCount++;
            }
            if (lodCount > 0)
                sb.Append("LODCount=").Append(lodCount).Append(nl);
        }

        return sb.ToString();
    }
}
