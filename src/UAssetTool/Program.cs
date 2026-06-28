#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.ExportTypes.Texture;
using UAssetAPI.Unversioned;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.Localization;

namespace UAssetTool;

/// <summary>
/// Unified UAsset Tool - Combines detection, fixing, and patching for all UE asset types.
/// Supports both interactive JSON mode (stdin/stdout) and CLI mode.
/// </summary>
public partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Set UTF-8 encoding for console to properly handle Unicode characters (Chinese, Korean, etc.)
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        
        // CLI mode: command-line arguments
        if (args.Length > 0)
        {
            return RunCliMode(args);
        }
        
        // Interactive JSON mode: read from stdin
        return await RunInteractiveMode();
    }

    #region CLI Mode
    
    private static int RunCliMode(string[] args)
    {
        string command = args[0].ToLower();
        
        try
        {
            return command switch
            {
                "detect" => CliDetect(args),
                "fix" => CliFix(args),
                "batch_detect" => CliBatchDetect(args),
                "dump" => CliDump(args),
                "to_zen" => CliToZen(args),
                "inspect_zen" => CliInspectZen(args),
                "create_pak" => CliCreatePak(args),
                "create_companion_pak" => CliCreateCompanionPak(args),
                "create_iostore_bundle" => CliCreateIoStoreBundle(args),
                "create_mod_iostore" => CliCreateModIoStore(args),
                "extract_iostore" => CliExtractIoStore(args),
                "extract_iostore_legacy" => CliExtractIoStoreLegacy(args),
                "is_iostore_compressed" => CliIsIoStoreCompressed(args),
                "is_iostore_encrypted" => CliIsIoStoreEncrypted(args),
                "extract_script_objects" => CliExtractScriptObjects(args),
                "recompress_iostore" => CliRecompressIoStore(args),
                "cityhash" => CliCityHash(args),
                "from_json" => CliFromJson(args),
                "to_json" => CliToJson(args),
                "dump_zen_from_game" => CliDumpZenFromGame(args),
                "clone_mod_iostore" => CliCloneModIoStore(args),
                "list_iostore" => CliListIoStore(args),
                "probe_chunks" => CliProbeChunks(args),
                "roundtrip" => CliRoundtrip(args),
                "test_objinject" => CliTestObjInject(args),
                "extract_pak" => CliExtractPak(args),
                "list_pak" => CliListPak(args),
                "niagara_poc" => CliNiagaraPoc(args),
                "niagara_details" => CliNiagaraDetails(args),
                "niagara_edit" => CliNiagaraEdit(args),
                "niagara_audit" => CliNiagaraAudit(args),
                "skeletal_mesh_info" => CliSkeletalMeshInfo(args),
                "inject_texture" => CliInjectTexture(args),
                "extract_texture" => CliExtractTexture(args),
                "batch_inject_texture" => CliBatchInjectTexture(args),
                "batch_extract_texture" => CliBatchExtractTexture(args),
                "parse_locres" => CliParseLocres(args),
                "version" or "--version" or "-v" => CliVersion(),
                "help" or "--help" or "-h" => CliHelp(),
                _ => throw new Exception($"Unknown command: {command}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
    
    private static int CliVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var infoVersion = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        
        Console.WriteLine($"UAssetTool v{infoVersion ?? version?.ToString() ?? "unknown"}");
        return 0;
    }

    private static int CliHelp()
    {
        Console.WriteLine("UAssetTool - Unified UE Asset Tool");
        Console.WriteLine();
        Console.WriteLine("Usage: UAssetTool <command> [args]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine();
        Console.WriteLine("  Asset Inspection:");
        Console.WriteLine("    detect <uasset_path> [usmap_path]       - Detect asset type");
        Console.WriteLine("    batch_detect <directory> [usmap_path]   - Detect all assets in directory");
        Console.WriteLine("    dump <uasset_path> <usmap_path>         - Dump detailed asset info");
        Console.WriteLine("    skeletal_mesh_info <uasset> <usmap>     - Get SkeletalMesh material/bone info");
        Console.WriteLine();
        Console.WriteLine("  Asset Editing:");
        Console.WriteLine("    fix <uasset_path> [usmap_path]          - Fix SerializeSize for meshes");
        Console.WriteLine("    to_json <path> [usmap] [output_dir]     - Convert uasset to JSON (file or directory)");
        Console.WriteLine("      Options: --compact                    - CUE4Parse-style compact output (read-only)");
        Console.WriteLine("    from_json <path> <output> [usmap]       - Convert JSON back to uasset (file or directory)");
        Console.WriteLine("    inject_texture <base> <image> <output>  - Inject PNG/TGA/DDS into texture uasset");
        Console.WriteLine("      Options: --no-mips                    - Mode A: single inline mip (default: Mode B mip chain + .ubulk/.uptnl)");
        Console.WriteLine("               --usmap <path>               - Path to usmap (pixel format preserved from base)");
        Console.WriteLine("    extract_texture <uasset> <output>       - Extract Texture2D to PNG/TGA/DDS/BMP");
        Console.WriteLine("      Options: --format PNG|TGA|DDS|BMP     - Output format (default: PNG)");
        Console.WriteLine("               --mip <index>                - Mip level to extract (default: 0)");
        Console.WriteLine("    batch_inject_texture <uasset_dir> <image_dir> <output_dir> - Batch inject textures");
        Console.WriteLine("      Matches image files to .uasset files by filename (e.g. T_Skin_D.png -> T_Skin_D.uasset)");
        Console.WriteLine("      Options: --no-mips                    - Mode A: single inline mip (default: Mode B mip chain)");
        Console.WriteLine("               --usmap <path>               - Path to usmap file");
        Console.WriteLine("    batch_extract_texture <uasset_dir> <output_dir> - Batch extract textures");
        Console.WriteLine("      Extracts all Texture2D .uasset files in directory to images");
        Console.WriteLine("      Options: --format PNG|TGA|DDS|BMP     - Output format (default: PNG)");
        Console.WriteLine("               --usmap <path>               - Path to usmap file");
        Console.WriteLine();
        Console.WriteLine("  Mod Creation (Legacy -> IoStore):");
        Console.WriteLine("    create_mod_iostore <output> <inputs...>  - Convert legacy assets and create IoStore bundle (add --hybrid to embed non-Unreal files in the companion PAK)");
        Console.WriteLine("    to_zen <uasset> [--no-material-tags] - Convert legacy to Zen format");
        Console.WriteLine("    create_pak <output.pak> <files...>       - Create encrypted PAK file");
        Console.WriteLine("    create_companion_pak <output.pak> <files...> - Create companion PAK for IoStore");
        Console.WriteLine("    create_iostore_bundle <output> <files...> - Create complete IoStore bundle");
        Console.WriteLine();
        Console.WriteLine("  Extraction:");
        Console.WriteLine("    extract_iostore_legacy <paks> <output> [options] - Extract IoStore to legacy format");
        Console.WriteLine("    extract_pak <pak_path> <output_dir> [options]    - Extract legacy PAK file");
        Console.WriteLine("    list_pak <pak_path> [options]                    - List contents of a PAK file");
        Console.WriteLine("    extract_script_objects <paks> <output>           - Extract ScriptObjects.bin");
        Console.WriteLine();
        Console.WriteLine("  IoStore Utilities:");
        Console.WriteLine("    inspect_zen <zen_asset_path>             - Inspect Zen package structure");
        Console.WriteLine("    is_iostore_compressed <utoc_path>        - Check if IoStore is compressed");
        Console.WriteLine("    recompress_iostore <utoc_path>           - Recompress IoStore with Oodle");
        Console.WriteLine("    cityhash <path_string>                   - Calculate CityHash64 for a path");
        Console.WriteLine("    clone_mod_iostore <utoc> <output>        - Clone/repackage a mod IoStore");
        Console.WriteLine("    list_iostore <utoc_path> [--aes <key>]   - List IoStore contents with ubulk status");
        Console.WriteLine();
        Console.WriteLine("  Localization:");
        Console.WriteLine("    parse_locres <path> [options]             - Parse .locres file(s) to JSON");
        Console.WriteLine("      Options: --output <path>               - Write JSON to file");
        Console.WriteLine("               --namespace <ns>              - Filter by namespace");
        Console.WriteLine("               --key <key>                   - Lookup specific key");
        Console.WriteLine("               --search <term>               - Search keys/values");
        Console.WriteLine("               --stats                       - Show namespace counts only");
        Console.WriteLine();
        Console.WriteLine("  Other:");
        Console.WriteLine("    version                                  - Show tool version");
        Console.WriteLine();
        Console.WriteLine("Interactive mode: Run without arguments to use JSON stdin/stdout");
        return 0;
    }
    
    private static int CliToZen(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool to_zen <uasset_path> [--no-material-tags]");
            return 1;
        }

        string uassetPath = args[1];
        
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--no-material-tags")
                ZenPackage.ZenConverter.SetMaterialTagsEnabled(false);
        }

        if (!File.Exists(uassetPath))
        {
            Console.Error.WriteLine($"File not found: {uassetPath}");
            return 1;
        }

        try
        {
            Console.Error.WriteLine($"[CliToZen] Converting {uassetPath} to Zen format...");
            
            byte[] zenData = ZenPackage.ZenConverter.ConvertLegacyToZen(uassetPath);
            
            string outputPath = Path.ChangeExtension(uassetPath, ".uzenasset");
            File.WriteAllBytes(outputPath, zenData);
            
            Console.WriteLine($"SUCCESS: Converted to {outputPath} ({zenData.Length} bytes)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
    
    private static int CliInspectZen(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool inspect_zen <zen_asset_path>");
            return 1;
        }

        string zenPath = args[1];
        if (!File.Exists(zenPath))
        {
            Console.Error.WriteLine($"File not found: {zenPath}");
            return 1;
        }

        try
        {
            ZenPackage.ZenInspector.InspectZenAsset(zenPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
    
    private static int CliCreatePak(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool create_pak <output.pak> <input1> [input2] ...");
            Console.Error.WriteLine("  Creates an encrypted PAK file. Inputs may be files or directories.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Path handling:");
            Console.Error.WriteLine("  - Directory input: all files are added recursively with paths relative");
            Console.Error.WriteLine("    to that directory (preserves Marvel/Content/... structure when present).");
            Console.Error.WriteLine("  - File input: the path inside the PAK is auto-detected by looking for");
            Console.Error.WriteLine("    a 'Marvel/Content/' ancestor in the absolute path. If found, the");
            Console.Error.WriteLine("    in-PAK path starts at 'Marvel/Content/...'. Otherwise the bare filename");
            Console.Error.WriteLine("    is used. Use --root to override the base directory explicitly.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --root <dir>          - Base directory; subsequent files are stored");
            Console.Error.WriteLine("                          with paths relative to this directory.");
            Console.Error.WriteLine("  --mount-point <path>  - Mount point (default: ../../../)");
            Console.Error.WriteLine("  --aes-key <hex>       - AES key in hex format");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Examples:");
            Console.Error.WriteLine("  create_pak mod.pak \"B:\\Mods\\MyMod_P\"");
            Console.Error.WriteLine("  create_pak mod.pak \"B:\\Mods\\MyMod_P\\Marvel\\Content\\WwiseAudio\\foo.bnk\"");
            Console.Error.WriteLine("  create_pak mod.pak --root \"B:\\Mods\\MyMod_P\" file1 file2");
            return 1;
        }

        string outputPath = args[1];
        string mountPoint = "../../../";
        string? aesKey = null;
        string? explicitRoot = null;
        var files = new List<(string relativePath, string absolutePath)>();

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--mount-point" && i + 1 < args.Length)
            {
                mountPoint = args[++i];
            }
            else if (args[i] == "--aes-key" && i + 1 < args.Length)
            {
                aesKey = args[++i];
            }
            else if (args[i] == "--root" && i + 1 < args.Length)
            {
                explicitRoot = Path.GetFullPath(args[++i]);
                if (!Directory.Exists(explicitRoot))
                {
                    Console.Error.WriteLine($"Warning: --root directory not found: {explicitRoot}");
                }
            }
            else if (Directory.Exists(args[i]))
            {
                // Directory input: walk recursively, use paths relative to this directory
                string baseDir = Path.GetFullPath(args[i]);
                int added = 0;
                foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
                {
                    string absPath = Path.GetFullPath(f);
                    string relPath = Path.GetRelativePath(baseDir, absPath).Replace('\\', '/');
                    files.Add((relPath, absPath));
                    added++;
                }
                Console.Error.WriteLine($"[CreatePak] Added directory '{baseDir}' ({added} files)");
            }
            else if (File.Exists(args[i]))
            {
                string absPath = Path.GetFullPath(args[i]);
                string relPath = ResolveInPakPath(absPath, explicitRoot);
                files.Add((relPath, absPath));
            }
            else
            {
                Console.Error.WriteLine($"Warning: File or directory not found: {args[i]}");
            }
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("Error: No valid files provided");
            return 1;
        }

        try
        {
            using var pakWriter = new IoStore.PakWriter(mountPoint, 0, aesKey);

            foreach (var (relPath, absPath) in files)
            {
                byte[] data = File.ReadAllBytes(absPath);
                pakWriter.AddEntry(relPath, data);
                Console.Error.WriteLine($"  Added: {relPath} ({data.Length} bytes)");
            }

            pakWriter.Write(outputPath);
            Console.WriteLine($"SUCCESS: Created PAK file at {outputPath} ({files.Count} entries)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Resolve the in-PAK path for a single file input.
    /// Priority:
    ///   1. If --root was provided and the file is under it, use the relative path.
    ///   2. Otherwise, look for a "Marvel/Content/" segment in the absolute path and
    ///      use everything from "Marvel/" onward (matches game's mount-relative layout).
    ///   3. Fall back to the bare filename.
    /// </summary>
    private static string ResolveInPakPath(string absPath, string? explicitRoot)
    {
        string normalized = absPath.Replace('\\', '/');

        if (!string.IsNullOrEmpty(explicitRoot))
        {
            string rootNorm = explicitRoot.Replace('\\', '/').TrimEnd('/');
            if (normalized.StartsWith(rootNorm + "/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(rootNorm.Length + 1);
            }
        }

        // Auto-detect the "Marvel/" mount root anchor (case-insensitive). Anchoring on "/Marvel/"
        // (not "/Marvel/Content/") preserves the full mount-relative layout for non-Content folders
        // too — e.g. Marvel/Config/DefaultEngine.ini, Marvel/Plugins/... — while giving identical
        // results for Content assets. "/MarvelGame/" and "/MarvelRivals/" do NOT contain "/Marvel/".
        const string anchor = "/Marvel/";
        int idx = normalized.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            // Return path starting from "Marvel/..."
            return normalized.Substring(idx + 1);
        }

        return Path.GetFileName(absPath);
    }

    /// <summary>
    /// Extensions that belong to the "immediate Unreal family" — packages, their bulk siblings,
    /// and shader libraries. These are routed into the IoStore (.ucas) by create_mod_iostore.
    /// Anything NOT in this set is considered a raw/loose file for hybrid bundles
    /// (e.g. .bnk, .wem, .png, .bin, .json, .locres) and goes into the companion PAK directly.
    /// Note: ".m.ubulk" resolves to a ".ubulk" extension via Path.GetExtension, so it is covered.
    /// </summary>
    private static readonly HashSet<string> UnrealFamilyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".uasset", ".umap", ".uexp", ".ubulk", ".uptnl", ".ushaderbytecode"
    };

    private static bool IsUnrealFamilyFile(string path)
        => UnrealFamilyExtensions.Contains(Path.GetExtension(path));

    private static int CliCreateCompanionPak(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool create_companion_pak <output.pak> <file_path1> [file_path2] ...");
            Console.Error.WriteLine("  Creates a companion PAK file for IoStore bundles.");
            Console.Error.WriteLine("  The PAK contains a 'chunknames' entry listing all provided file paths.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --mount-point <path>  - Mount point (default: ../../../)");
            Console.Error.WriteLine("  --path-hash-seed <n>  - Path hash seed (default: 0)");
            Console.Error.WriteLine("  --aes-key <hex>       - AES key in hex format");
            return 1;
        }

        string outputPath = args[1];
        string mountPoint = "../../../";
        ulong pathHashSeed = 0;
        string? aesKey = null;
        var filePaths = new List<string>();

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--mount-point" && i + 1 < args.Length)
            {
                mountPoint = args[++i];
            }
            else if (args[i] == "--path-hash-seed" && i + 1 < args.Length)
            {
                pathHashSeed = ulong.Parse(args[++i]);
            }
            else if (args[i] == "--aes-key" && i + 1 < args.Length)
            {
                aesKey = args[++i];
            }
            else
            {
                // Add as a file path (doesn't need to exist - just a path string)
                filePaths.Add(args[i]);
            }
        }

        if (filePaths.Count == 0)
        {
            Console.Error.WriteLine("Error: No file paths provided");
            return 1;
        }

        try
        {
            IoStore.ChunkNamesPakWriter.Create(outputPath, filePaths, mountPoint, pathHashSeed, aesKey);
            Console.WriteLine($"SUCCESS: Created companion PAK at {outputPath}");
            Console.WriteLine($"  Files listed: {filePaths.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int CliCreateIoStoreBundle(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool create_iostore_bundle <output_base> <file1> [file2] ...");
            Console.Error.WriteLine("  Creates a complete IoStore bundle (.utoc, .ucas, .pak) from the specified files.");
            Console.Error.WriteLine("  output_base should be the base name without extension (e.g., 'MyMod_P')");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --mount-point <path>  - Mount point (default: ../../../)");
            Console.Error.WriteLine("  --compress            - Enable Oodle compression (default: enabled)");
            Console.Error.WriteLine("  --no-compress         - Disable compression");
            Console.Error.WriteLine("  --encrypt             - Enable AES encryption");
            Console.Error.WriteLine("  --aes-key <hex>       - AES key in hex format");
            return 1;
        }

        string outputBase = args[1];
        string mountPoint = "../../../";
        bool enableCompression = true;
        bool enableEncryption = false;
        string? aesKey = null;
        var files = new List<(string relativePath, string absolutePath)>();

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--mount-point" && i + 1 < args.Length)
            {
                mountPoint = args[++i];
            }
            else if (args[i] == "--compress")
            {
                enableCompression = true;
            }
            else if (args[i] == "--no-compress")
            {
                enableCompression = false;
            }
            else if (args[i] == "--encrypt")
            {
                enableEncryption = true;
            }
            else if (args[i] == "--aes-key" && i + 1 < args.Length)
            {
                aesKey = args[++i];
                enableEncryption = true;
            }
            else if (File.Exists(args[i]))
            {
                string absPath = Path.GetFullPath(args[i]);
                string relPath = Path.GetFileName(args[i]);
                files.Add((relPath, absPath));
            }
            else
            {
                Console.Error.WriteLine($"Warning: File not found: {args[i]}");
            }
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("Error: No valid files provided");
            return 1;
        }

        try
        {
            string utocPath = outputBase + ".utoc";
            string pakPath = outputBase + ".pak";

            Console.Error.WriteLine($"[CreateIoStoreBundle] Creating IoStore bundle: {outputBase}");
            Console.Error.WriteLine($"[CreateIoStoreBundle]   Files: {files.Count}");
            Console.Error.WriteLine($"[CreateIoStoreBundle]   Compression: {(enableCompression ? "Oodle" : "None")}");
            Console.Error.WriteLine($"[CreateIoStoreBundle]   Encryption: {(enableEncryption ? "AES-256" : "None")}");

            // Create IoStore container
            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                mountPoint,
                enableCompression,
                enableEncryption,
                aesKey);

            var filePaths = new List<string>();

            foreach (var (relPath, absPath) in files)
            {
                byte[] data = File.ReadAllBytes(absPath);

                // Create package ID from filename
                string packageName = relPath;
                if (packageName.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                    packageName = packageName[..^7];
                else if (packageName.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
                    packageName = packageName[..^5];

                var packageId = IoStore.FPackageId.FromName("/" + packageName);
                var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);

                var storeEntry = new IoStore.StoreEntry
                {
                    ExportCount = 1,
                    ExportBundleCount = 1,
                    LoadOrder = 0
                };

                string gamePath = mountPoint + relPath;
                ioStoreWriter.WritePackageChunk(chunkId, gamePath, data, storeEntry);
                filePaths.Add(relPath);

                Console.Error.WriteLine($"  Added: {relPath} ({data.Length} bytes)");
            }

            ioStoreWriter.Complete();

            // Create companion PAK
            IoStore.ChunkNamesPakWriter.Create(pakPath, filePaths, mountPoint, 0, aesKey);

            Console.WriteLine($"SUCCESS: Created IoStore bundle:");
            Console.WriteLine($"  {utocPath}");
            Console.WriteLine($"  {Path.ChangeExtension(utocPath, ".ucas")}");
            Console.WriteLine($"  {pakPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static int CliCreateModIoStore(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool create_mod_iostore <output_base> <input> [input2] ...");
            Console.Error.WriteLine("  Converts legacy assets to Zen format and creates IoStore bundle.");
            Console.Error.WriteLine("  Input can be .uasset files, directories, or .pak files.");
            Console.Error.WriteLine("  This is the complete pipeline for Marvel Rivals mod creation.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --mount-point <path>  - Mount point (default: ../../../)");
            Console.Error.WriteLine("  --game-path <prefix>  - Game path prefix (default: Marvel/Content/)");
            Console.Error.WriteLine("  --compress            - Enable Oodle compression (default: enabled)");
            Console.Error.WriteLine("  --no-compress         - Disable compression");
            Console.Error.WriteLine("  --obfuscate           - Protect mod from extraction tools like FModel");
            Console.Error.WriteLine("  --pak-aes <hex>       - AES key for decrypting input .pak files");
            Console.Error.WriteLine("  --no-material-tags    - Disable MaterialTag injection (enabled by default)");
            Console.Error.WriteLine("  --hybrid              - Embed non-Unreal files (audio, raw .png, .bin, ...) as");
            Console.Error.WriteLine("                          loose entries in the companion PAK instead of dropping them");
            return 1;
        }

        string outputBase = args[1];
        string mountPoint = "../../../";
        string gamePathPrefix = "Marvel/Content/";
        bool enableCompression = true;
        bool enableEncryption = false;
        string? aesKey = null;
        string? pakAesKey = null;
        // Marvel Rivals AES key for obfuscation
        const string MARVEL_RIVALS_AES_KEY = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";
        // Pre-scan for --hybrid so it applies no matter where it appears relative to inputs
        // (inputs are processed inline as the arg loop encounters them).
        bool hybrid = args.Any(a => a.Equals("--hybrid", StringComparison.OrdinalIgnoreCase));
        var uassetFiles = new List<string>();
        var shaderBytecodeFiles = new List<string>();
        // Hybrid raw files: (in-PAK relative path, absolute path on disk).
        var rawFiles = new List<(string inPakPath, string absPath)>();
        var tempDirsToCleanup = new List<string>();

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--no-material-tags")
            {
                ZenPackage.ZenConverter.SetMaterialTagsEnabled(false);
            }
            else if (args[i] == "--mount-point" && i + 1 < args.Length)
            {
                mountPoint = args[++i];
            }
            else if (args[i] == "--game-path" && i + 1 < args.Length)
            {
                gamePathPrefix = args[++i];
            }
            else if (args[i] == "--compress")
            {
                enableCompression = true;
            }
            else if (args[i] == "--no-compress")
            {
                enableCompression = false;
            }
            else if (args[i] == "--obfuscate")
            {
                enableEncryption = true;
                aesKey = MARVEL_RIVALS_AES_KEY;
            }
            else if (args[i] == "--pak-aes" && i + 1 < args.Length)
            {
                pakAesKey = args[++i];
            }
            else if (args[i] == "--hybrid")
            {
                // Already handled by the pre-scan above; consume here so it isn't treated as input.
            }
            else if (args[i].EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && File.Exists(args[i]))
            {
                // Extract legacy assets from .pak file to temp directory
                string pakInputPath = Path.GetFullPath(args[i]);
                string tempDir = Path.Combine(Path.GetTempPath(), "UAssetTool_pak_" + Path.GetFileNameWithoutExtension(pakInputPath) + "_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tempDir);
                tempDirsToCleanup.Add(tempDir);

                Console.Error.WriteLine($"[CreateModIoStore] Extracting legacy PAK: {pakInputPath}");
                try
                {
                    using var pakReader = new IoStore.PakReader(pakInputPath, pakAesKey);
                    int extracted = 0;
                    foreach (var file in pakReader.Files)
                    {
                        // Only extract the Unreal family — UNLESS hybrid mode is on, in which case
                        // extract everything so non-Unreal files can be embedded into the companion PAK.
                        if (!hybrid && !IsUnrealFamilyFile(file))
                            continue;

                        byte[] data = pakReader.Get(file);
                        string relativePath = file.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                        string outPath = Path.Combine(tempDir, relativePath);
                        string? dir = Path.GetDirectoryName(outPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllBytes(outPath, data);
                        extracted++;
                    }
                    Console.Error.WriteLine($"[CreateModIoStore]   Extracted {extracted} files from PAK");

                    // Find all .uasset files in the extracted directory
                    var dirFiles = Directory.GetFiles(tempDir, "*.uasset", SearchOption.AllDirectories);
                    foreach (var f in dirFiles)
                    {
                        uassetFiles.Add(Path.GetFullPath(f));
                    }
                    Console.Error.WriteLine($"[CreateModIoStore]   Found {dirFiles.Length} .uasset files");

                    // Find shader bytecode files
                    var shaderDirFiles = Directory.GetFiles(tempDir, "*.ushaderbytecode", SearchOption.AllDirectories);
                    foreach (var f in shaderDirFiles)
                        shaderBytecodeFiles.Add(Path.GetFullPath(f));
                    if (shaderDirFiles.Length > 0)
                        Console.Error.WriteLine($"[CreateModIoStore]   Found {shaderDirFiles.Length} .ushaderbytecode files");

                    // Hybrid: collect every extracted non-Unreal file for the companion PAK.
                    if (hybrid)
                    {
                        int rawBefore = rawFiles.Count;
                        foreach (var f in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
                        {
                            if (IsUnrealFamilyFile(f)) continue;
                            string rel = Path.GetRelativePath(tempDir, f).Replace('\\', '/');
                            rawFiles.Add((rel, Path.GetFullPath(f)));
                        }
                        Console.Error.WriteLine($"[CreateModIoStore]   Found {rawFiles.Count - rawBefore} raw (non-Unreal) files");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error extracting PAK: {ex.Message}");
                }
            }
            else if (args[i].EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && File.Exists(args[i]))
            {
                uassetFiles.Add(Path.GetFullPath(args[i]));
            }
            else if (args[i].EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase) && File.Exists(args[i]))
            {
                shaderBytecodeFiles.Add(Path.GetFullPath(args[i]));
            }
            else if (Directory.Exists(args[i]))
            {
                // Support directory input - recursively find all .uasset files
                var dirFiles = Directory.GetFiles(args[i], "*.uasset", SearchOption.AllDirectories);
                foreach (var f in dirFiles)
                {
                    uassetFiles.Add(Path.GetFullPath(f));
                }
                Console.Error.WriteLine($"Found {dirFiles.Length} .uasset files in directory: {args[i]}");
                
                // Also find shader bytecode files in the directory
                var shaderDirFiles = Directory.GetFiles(args[i], "*.ushaderbytecode", SearchOption.AllDirectories);
                foreach (var f in shaderDirFiles)
                    shaderBytecodeFiles.Add(Path.GetFullPath(f));
                if (shaderDirFiles.Length > 0)
                    Console.Error.WriteLine($"Found {shaderDirFiles.Length} .ushaderbytecode files in directory: {args[i]}");

                // Hybrid: collect every non-Unreal file in the directory for the companion PAK.
                if (hybrid)
                {
                    int rawBefore = rawFiles.Count;
                    string baseDir = Path.GetFullPath(args[i]);
                    foreach (var f in Directory.GetFiles(args[i], "*", SearchOption.AllDirectories))
                    {
                        if (IsUnrealFamilyFile(f)) continue;
                        string rel = Path.GetRelativePath(baseDir, Path.GetFullPath(f)).Replace('\\', '/');
                        rawFiles.Add((rel, Path.GetFullPath(f)));
                    }
                    if (rawFiles.Count - rawBefore > 0)
                        Console.Error.WriteLine($"Found {rawFiles.Count - rawBefore} raw (non-Unreal) files in directory: {args[i]}");
                }
            }
            else if (File.Exists(args[i]))
            {
                if (hybrid)
                {
                    // Embed an individually-passed non-Unreal file into the companion PAK.
                    string abs = Path.GetFullPath(args[i]);
                    string rel = ResolveInPakPath(abs, null);
                    rawFiles.Add((rel, abs));
                    Console.Error.WriteLine($"[CreateModIoStore] Hybrid: embedding raw file: {rel}");
                }
                else
                {
                    Console.Error.WriteLine($"Warning: Skipping non-.uasset file: {args[i]}");
                }
            }
            else
            {
                Console.Error.WriteLine($"Warning: File not found: {args[i]}");
            }
        }

        if (uassetFiles.Count == 0 && shaderBytecodeFiles.Count == 0 && rawFiles.Count == 0)
        {
            Console.Error.WriteLine("Error: No valid .uasset or .ushaderbytecode files provided");
            return 1;
        }

        try
        {
            string utocPath = outputBase + ".utoc";
            string pakPath = outputBase + ".pak";

            Console.Error.WriteLine($"[CreateModIoStore] Creating IoStore mod bundle: {outputBase}");
            Console.Error.WriteLine($"[CreateModIoStore]   Assets: {uassetFiles.Count}");
            if (shaderBytecodeFiles.Count > 0)
                Console.Error.WriteLine($"[CreateModIoStore]   Shader Libraries: {shaderBytecodeFiles.Count}");
            Console.Error.WriteLine($"[CreateModIoStore]   Compression: {(enableCompression ? "Oodle" : "None")}");
            Console.Error.WriteLine($"[CreateModIoStore]   Protection: {(enableEncryption ? "Obfuscated (FModel-proof)" : "None")}");

            // Phase 1: Parallel conversion to Zen format (CPU-intensive)
            // This is the bottleneck - UAsset parsing, NameMap building, export reordering, hashing
            int threadCount = Math.Max(1, (Environment.ProcessorCount * 3) / 4); // 75% of cores
            Console.Error.WriteLine($"[CreateModIoStore]   Threads: {threadCount}");

            var conversionResults = new System.Collections.Concurrent.ConcurrentBag<(string assetName, string uassetPath, byte[]? zenData, string packagePath, ZenPackage.FZenPackage? zenPackage, byte[]? ubulkData, byte[]? uptnlData, byte[]? mubulkData, string? error)>();
            int processedCount = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            Parallel.ForEach(uassetFiles, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, uassetPath =>
            {
                string assetName = Path.GetFileNameWithoutExtension(uassetPath);
                try
                {
                    var (zenData, packagePath, zenPackage) = ZenPackage.ZenConverter.ConvertLegacyToZenFull(
                        uassetPath, containerVersion: ZenPackage.EIoContainerHeaderVersion.NoExportInfo);

                    byte[]? ubulkData = null;
                    string ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
                    if (File.Exists(ubulkPath)) ubulkData = File.ReadAllBytes(ubulkPath);

                    byte[]? uptnlData = null;
                    string uptnlPath = Path.ChangeExtension(uassetPath, ".uptnl");
                    if (File.Exists(uptnlPath)) uptnlData = File.ReadAllBytes(uptnlPath);

                    byte[]? mubulkData = null;
                    string mubulkPath = Path.ChangeExtension(uassetPath, ".m.ubulk");
                    if (File.Exists(mubulkPath)) mubulkData = File.ReadAllBytes(mubulkPath);

                    conversionResults.Add((assetName, uassetPath, zenData, packagePath, zenPackage, ubulkData, uptnlData, mubulkData, null));

                    int count = Interlocked.Increment(ref processedCount);
                    if (count % 50 == 0 || count == uassetFiles.Count)
                    {
                        Console.Error.WriteLine($"[CreateModIoStore] Converted {count}/{uassetFiles.Count}...");
                    }
                }
                catch (Exception ex)
                {
                    conversionResults.Add((assetName, uassetPath, null, "", null, null, null, null, ex.Message));
                    Interlocked.Increment(ref processedCount);
                }
            });

            Console.Error.WriteLine($"[CreateModIoStore] Parallel conversion done in {sw.Elapsed.TotalSeconds:F1}s. Writing IoStore...");

            // Phase 2: Sequential write to IoStore (fast I/O, not parallelizable)
            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                mountPoint,
                enableCompression,
                enableEncryption,
                aesKey);

            var filePaths = new List<string>();
            int convertedCount = 0;
            var errors = new List<string>();

            foreach (var result in conversionResults)
            {
                if (result.error != null || result.zenData == null)
                {
                    if (result.error != null)
                    {
                        errors.Add($"{result.assetName}: {result.error}");
                        Console.Error.WriteLine($"  ERROR: {result.assetName}: {result.error}");
                    }
                    continue;
                }

                try
                {
                    var (assetName, uassetPath, zenData, packagePath, zenPackage, ubulkData, uptnlData, mubulkData, _) = result;

                    // Create package ID using the /Game/... format
                    string gamePackagePath;
                    if (packagePath.StartsWith("Marvel/Content/"))
                    {
                        gamePackagePath = "/Game/" + packagePath.Substring("Marvel/Content/".Length);
                    }
                    else
                    {
                        gamePackagePath = "/" + packagePath;
                    }

                    var packageId = IoStore.FPackageId.FromName(gamePackagePath);
                    var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);

                    // Create store entry with imported packages from the Zen package
                    var storeEntry = new IoStore.StoreEntry
                    {
                        ExportCount = zenPackage.ExportMap.Count,
                        ExportBundleCount = 1,
                        LoadOrder = 0
                    };

                    foreach (ulong importedPkgId in zenPackage.ImportedPackages)
                    {
                        storeEntry.ImportedPackages.Add(new IoStore.FPackageId(importedPkgId));
                    }

                    // Write to IoStore
                    string fullPath = mountPoint + packagePath + ".uasset";
                    ioStoreWriter.WritePackageChunk(chunkId, fullPath, zenData, storeEntry);

                    filePaths.Add(packagePath + ".uasset");
                    filePaths.Add(packagePath + ".uexp");

                    // Handle .ubulk if exists (already loaded during parallel phase)
                    if (ubulkData != null)
                    {
                        var bulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.BulkData);
                        string bulkFullPath = mountPoint + packagePath + ".ubulk";
                        ioStoreWriter.WriteChunk(bulkChunkId, bulkFullPath, ubulkData);
                        filePaths.Add(packagePath + ".ubulk");
                    }

                    // Handle .uptnl if exists
                    if (uptnlData != null)
                    {
                        var optBulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.OptionalBulkData);
                        string optBulkFullPath = mountPoint + packagePath + ".uptnl";
                        ioStoreWriter.WriteChunk(optBulkChunkId, optBulkFullPath, uptnlData);
                        filePaths.Add(packagePath + ".uptnl");
                    }

                    // Handle .m.ubulk if exists
                    if (mubulkData != null)
                    {
                        var memBulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.MemoryMappedBulkData);
                        string memBulkFullPath = mountPoint + packagePath + ".m.ubulk";
                        ioStoreWriter.WriteChunk(memBulkChunkId, memBulkFullPath, mubulkData);
                        filePaths.Add(packagePath + ".m.ubulk");
                    }

                    convertedCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{result.assetName}: {ex.Message}");
                    Console.Error.WriteLine($"  ERROR writing {result.assetName}: {ex.Message}");
                }
            }

            // Process shader bytecode files
            int shaderLibsConverted = 0;
            foreach (var shaderFile in shaderBytecodeFiles)
            {
                try
                {
                    byte[] shaderData = File.ReadAllBytes(shaderFile);
                    string shaderLibPath = mountPoint + gamePathPrefix + Path.GetFileName(shaderFile);
                    IoStore.ShaderLibraryConverter.ConvertAndWrite(shaderData, shaderLibPath, ioStoreWriter);
                    filePaths.Add(gamePathPrefix + Path.GetFileName(shaderFile));
                    shaderLibsConverted++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Shader:{Path.GetFileName(shaderFile)}: {ex.Message}");
                    Console.Error.WriteLine($"  ERROR converting shader library {Path.GetFileName(shaderFile)}: {ex.Message}");
                }
            }

            if (filePaths.Count == 0 && rawFiles.Count == 0)
            {
                Console.Error.WriteLine("Error: No assets were successfully converted");
                if (errors.Count > 0)
                    Console.Error.WriteLine($"Errors: {string.Join("; ", errors)}");
                return 1;
            }

            ioStoreWriter.Complete();

            // Read hybrid raw files into memory so they can be embedded into the companion PAK.
            var rawEntries = new List<(string inPakPath, byte[] data)>();
            foreach (var (inPakPath, absPath) in rawFiles)
            {
                try
                {
                    rawEntries.Add((inPakPath, File.ReadAllBytes(absPath)));
                }
                catch (Exception ex)
                {
                    errors.Add($"Raw:{inPakPath}: {ex.Message}");
                    Console.Error.WriteLine($"  ERROR reading raw file {inPakPath}: {ex.Message}");
                }
            }

            // Create companion PAK (with chunknames and any hybrid raw files)
            IoStore.ChunkNamesPakWriter.Create(pakPath, filePaths, mountPoint, 0, aesKey, rawEntries.Count > 0 ? rawEntries : null);

            Console.WriteLine($"SUCCESS: Created IoStore mod bundle:");
            Console.WriteLine($"  {utocPath}");
            Console.WriteLine($"  {Path.ChangeExtension(utocPath, ".ucas")}");
            Console.WriteLine($"  {pakPath}");
            Console.WriteLine($"  Assets converted: {filePaths.Count}");
            if (rawEntries.Count > 0)
                Console.WriteLine($"  Raw (hybrid) files in companion PAK: {rawEntries.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
        finally
        {
            // Clean up temp directories created from .pak extraction
            foreach (var tempDir in tempDirsToCleanup)
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                        Console.Error.WriteLine($"[CreateModIoStore] Cleaned up temp directory: {tempDir}");
                    }
                }
                catch { /* Best effort cleanup */ }
            }
        }
    }

    private static int CliExtractIoStore(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool extract_iostore <utoc_path> <output_dir> [--chunk-id <id>] [--package <name>] [--aes <hex>]");
            Console.Error.WriteLine("Examples:");
            Console.Error.WriteLine("  UAssetTool extract_iostore pakchunk0-WindowsClient.utoc ./extracted");
            Console.Error.WriteLine("  UAssetTool extract_iostore pakchunk0-WindowsClient.utoc ./extracted --package /Game/Marvel/Characters/1033/1033001/Weapons/Stick_L/Meshes/SM_WP_1033001_Stick_L");
            Console.Error.WriteLine("  UAssetTool extract_iostore pakchunk0-WindowsClient.utoc ./extracted --aes 0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74 --package /Game/...");
            return 1;
        }

        string utocPath = args[1];
        string outputDir = args[2];
        string? packageName = null;
        string? chunkIdHex = null;
        string? aesKeyHex = null;

        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--package" && i + 1 < args.Length)
                packageName = args[++i];
            else if (args[i] == "--chunk-id" && i + 1 < args.Length)
                chunkIdHex = args[++i];
            else if ((args[i] == "--aes" || args[i] == "--aes-key") && i + 1 < args.Length)
                aesKeyHex = args[++i];
        }

        try
        {
            // Default to Marvel Rivals AES key if none provided
            aesKeyHex ??= "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";
            byte[] aesKey = Convert.FromHexString(aesKeyHex);
            using var reader = new IoStore.IoStoreReader(utocPath, aesKey);
            Console.WriteLine($"Opened IoStore: {reader.ContainerName}");
            Console.WriteLine($"  TOC Version: {reader.Toc.Version}");
            Console.WriteLine($"  Chunks: {reader.Toc.Chunks.Count}");
            Console.WriteLine($"  Compression Methods: {string.Join(", ", reader.Toc.CompressionMethods)}");

            Directory.CreateDirectory(outputDir);

            if (packageName != null)
            {
                // Extract specific package
                var packageId = IoStore.FPackageId.FromName(packageName);
                var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);

                if (reader.HasChunk(chunkId))
                {
                    byte[] data = reader.ReadChunk(chunkId);
                    string outputPath = Path.Combine(outputDir, Path.GetFileName(packageName) + ".uasset");
                    File.WriteAllBytes(outputPath, data);
                    Console.WriteLine($"Extracted: {outputPath} ({data.Length} bytes)");
                }
                else
                {
                    Console.Error.WriteLine($"Package not found: {packageName}");
                    return 1;
                }
            }
            else if (chunkIdHex != null)
            {
                // Extract specific chunk by ID
                byte[] chunkIdBytes = Convert.FromHexString(chunkIdHex);
                var chunkId = IoStore.FIoChunkId.FromBytes(chunkIdBytes);

                byte[] data = reader.ReadChunk(chunkId);
                string outputPath = Path.Combine(outputDir, $"chunk_{chunkIdHex}.bin");
                File.WriteAllBytes(outputPath, data);
                Console.WriteLine($"Extracted: {outputPath} ({data.Length} bytes)");
            }
            else
            {
                // List all chunks
                Console.WriteLine("\nChunks in container:");
                int count = 0;
                foreach (var chunk in reader.GetChunks())
                {
                    string? path = reader.GetChunkPath(chunk);
                    Console.WriteLine($"  [{count}] {chunk} -> {path ?? "(no path)"}");
                    count++;
                    if (count >= 100)
                    {
                        Console.WriteLine($"  ... and {reader.Toc.Chunks.Count - count} more");
                        break;
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Check if an IoStore container is compressed.
    /// Equivalent to retoc::is_iostore_compressed()
    /// </summary>
    private static int CliIsIoStoreCompressed(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool is_iostore_compressed <utoc_path>");
            return 1;
        }

        string utocPath = args[1];
        
        try
        {
            bool isCompressed = IoStore.IoStoreReader.IsCompressed(utocPath);
            Console.WriteLine(isCompressed ? "true" : "false");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Check if an IoStore container is encrypted (obfuscated).
    /// </summary>
    private static int CliIsIoStoreEncrypted(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool is_iostore_encrypted <utoc_path>");
            return 1;
        }

        string utocPath = args[1];
        
        try
        {
            bool isEncrypted = IoStore.IoStoreReader.IsEncrypted(utocPath);
            Console.WriteLine(isEncrypted ? "true" : "false");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Extract ScriptObjects.bin from game IoStore containers.
    /// Equivalent to retoc::extract_script_objects()
    /// </summary>
    private static int CliExtractScriptObjects(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool extract_script_objects <paks_path> <output_file>");
            Console.Error.WriteLine("Example: UAssetTool extract_script_objects \"C:/Games/MarvelRivals/MarvelGame/Marvel/Content/Paks\" ScriptObjects.bin");
            return 1;
        }

        string paksPath = args[1];
        string outputPath = args[2];

        try
        {
            byte[]? data = IoStore.IoStoreReader.ExtractScriptObjects(paksPath);
            if (data == null)
            {
                Console.Error.WriteLine("ScriptObjects not found in any IoStore container");
                return 1;
            }

            File.WriteAllBytes(outputPath, data);
            Console.WriteLine($"Extracted ScriptObjects.bin: {data.Length} bytes");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Recompress an IoStore container with Oodle compression.
    /// Equivalent to retoc::recompress_iostore()
    /// </summary>
    private static int CliRecompressIoStore(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool recompress_iostore <utoc_path>");
            Console.Error.WriteLine("Recompresses an IoStore container with Oodle compression");
            return 1;
        }

        string utocPath = args[1];

        try
        {
            // Check if already compressed
            if (IoStore.IoStoreReader.IsCompressed(utocPath))
            {
                Console.WriteLine("IoStore is already compressed, skipping");
                return 0;
            }

            string result = IoStore.IoStoreRecompressor.Recompress(utocPath);
            Console.WriteLine($"Recompressed: {result}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Compute CityHash64 of a string (UTF-16LE encoded, lowercase).
    /// This replaces the external hash_helper.exe tool.
    /// Usage: UAssetTool cityhash <string>
    /// </summary>
    private static int CliCityHash(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool cityhash <string>");
            Console.Error.WriteLine("Computes CityHash64 of lowercase UTF-16LE encoded string");
            return 1;
        }
        
        string input = args[1];
        ulong hash = IoStore.CityHash.CityHash64(input.ToLowerInvariant());
        
        // Output just the hash in hex format (same as hash_helper.exe)
        Console.WriteLine($"{hash:X16}");
        return 0;
    }

    /// <summary>
    /// Extract IoStore packages to legacy .uasset/.uexp format using native C# conversion.
    /// Usage: UAssetTool extract_iostore_legacy <utoc_path> <output_dir> [--script-objects <path>]
    /// </summary>
    public static int CliExtractIoStoreLegacy(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool extract_iostore_legacy <paks_directory> <output_dir> [options]");
            Console.Error.WriteLine("Extracts IoStore packages and converts them to legacy .uasset/.uexp format");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Arguments:");
            Console.Error.WriteLine("  <paks_directory>         Path to game's Paks directory (loads all .utoc files)");
            Console.Error.WriteLine("  <output_dir>             Output directory for extracted assets");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --script-objects <path>  Path to ScriptObjects.bin for import resolution");
            Console.Error.WriteLine("  --global <path>          Path to global.utoc for script objects");
            Console.Error.WriteLine("  --container <path>       Additional container to load for cross-package imports");
            Console.Error.WriteLine("  --aes <hex>              AES key for decryption (can specify multiple times)");
            Console.Error.WriteLine("  --filter <patterns...>   Only extract packages matching patterns (space-separated)");
            Console.Error.WriteLine("                           Can also pass a .txt file containing one pattern per line");
            Console.Error.WriteLine("  --with-deps              Also extract imported/referenced packages");
            Console.Error.WriteLine("  --mod <path>             Path to modded .utoc file or directory containing .utoc files.");
            Console.Error.WriteLine("                           Extracts from mod containers, uses game paks for import resolution.");
            Console.Error.WriteLine("                           If path is a .utoc file, loads that single bundle.");
            Console.Error.WriteLine("                           If path is a directory, loads all .utoc files in it.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Examples:");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --filter SK_1014 SK_1057 SK_1036");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --filter Characters/1014 Characters/1057");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --mod \"C:/Mods/my_mod.utoc\" --filter SK_1014");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --mod \"C:/Mods/\" --with-deps");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --filter filters.txt");
            return 1;
        }

        string paksPath = args[1];
        string outputDir = args[2];
        string? scriptObjectsPath = null;
        string? globalUtocPath = null;
        List<string> additionalContainers = new();
        List<string> filterPatterns = new();
        List<string> modPaths = new(); // Mod utoc files or directories
        List<string> aesKeys = new();
        bool extractDependencies = false;

        for (int i = 3; i < args.Length; i++)
        {
            if ((args[i] == "--aes" || args[i] == "--aes-key") && i + 1 < args.Length)
                aesKeys.Add(args[++i]);
            else if (args[i] == "--script-objects" && i + 1 < args.Length)
                scriptObjectsPath = args[++i];
            else if (args[i] == "--global" && i + 1 < args.Length)
                globalUtocPath = args[++i];
            else if (args[i] == "--container" && i + 1 < args.Length)
                additionalContainers.Add(args[++i]);
            else if (args[i] == "--mod")
            {
                // Collect all following args until next option (starts with --)
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    modPaths.Add(args[++i]);
                }
            }
            else if (args[i] == "--filter" || args[i] == "--package")
            {
                // Collect all following args until next option (starts with --)
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    string filterArg = args[++i];
                    // If the argument is a .txt file, read patterns from it (one per line)
                    if (filterArg.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && File.Exists(filterArg))
                    {
                        var lines = File.ReadAllLines(filterArg)
                            .Select(l => l.Trim())
                            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#") && !l.StartsWith("//"));
                        filterPatterns.AddRange(lines);
                        Console.Error.WriteLine($"[Filter] Loaded {lines.Count()} patterns from {filterArg}");
                    }
                    else
                    {
                        filterPatterns.Add(filterArg);
                    }
                }
            }
            else if (args[i] == "-deps" || args[i] == "--with-deps")
                extractDependencies = true;
        }

        // Validate paks path
        if (!Directory.Exists(paksPath))
        {
            Console.Error.WriteLine($"Paks directory not found: {paksPath}");
            return 1;
        }
        
        try
        {
            // Create package context for proper import resolution
            using var context = new ZenPackage.FZenPackageContext();
            
            // Set AES keys - use provided keys or default Marvel Rivals key
            if (aesKeys.Count > 0)
            {
                foreach (var key in aesKeys)
                    context.AddAesKey(key);
            }
            else
            {
                context.SetAesKey("0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74");
            }
            
            Console.WriteLine($"Loading game containers from: {paksPath}");
            
            // Load global.utoc first for script objects
            string globalPath = Path.Combine(paksPath, "global.utoc");
            if (File.Exists(globalPath))
            {
                Console.WriteLine($"  Loading global.utoc...");
                context.LoadContainer(globalPath);
                context.LoadScriptObjectsFromContainer(0);
            }
            
            // Load other game containers (only top-level, not subfolders)
            // Include optional chunks when extracting dependencies
            var utocFiles = Directory.GetFiles(paksPath, "*.utoc", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith("global.utoc", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();
            
            Console.WriteLine($"  Loading {utocFiles.Count} game containers...");
            foreach (var utocFile in utocFiles)
            {
                try
                {
                    context.LoadContainer(utocFile);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Warning: Failed to load {Path.GetFileName(utocFile)}: {ex.Message}");
                }
            }
            
            // Load global container first (for script objects) - explicit path overrides game path
            if (!string.IsNullOrEmpty(globalUtocPath) && File.Exists(globalUtocPath))
            {
                Console.WriteLine($"Loading global container: {globalUtocPath}");
                context.LoadContainer(globalUtocPath);
                context.LoadScriptObjectsFromContainer(context.ContainerCount - 1);
            }
            
            // Load script objects from file if provided
            if (!string.IsNullOrEmpty(scriptObjectsPath) && File.Exists(scriptObjectsPath))
            {
                Console.WriteLine($"Loading ScriptObjects from: {scriptObjectsPath}");
                context.LoadScriptObjects(scriptObjectsPath);
            }
            
            // Load additional containers for cross-package imports
            foreach (var containerPath in additionalContainers)
            {
                if (File.Exists(containerPath))
                {
                    Console.WriteLine($"Loading additional container: {containerPath}");
                    context.LoadContainer(containerPath);
                }
            }
            
            // Track mod container indices for extraction source filtering
            HashSet<int> modContainerIndices = new();
            
            // Load mod containers with priority (they override game packages)
            if (modPaths.Count > 0)
            {
                Console.WriteLine($"\nLoading mod containers...");
                foreach (var modPath in modPaths)
                {
                    if (modPath.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) && File.Exists(modPath))
                    {
                        // Single utoc file specified
                        int containerIdx = context.ContainerCount;
                        context.LoadContainerWithPriority(modPath);
                        modContainerIndices.Add(containerIdx);
                    }
                    else if (Directory.Exists(modPath))
                    {
                        // Directory - load all utoc files in it
                        var modUtocFiles = Directory.GetFiles(modPath, "*.utoc", SearchOption.TopDirectoryOnly);
                        if (modUtocFiles.Length == 0)
                        {
                            Console.Error.WriteLine($"Warning: No .utoc files found in mod directory: {modPath}");
                        }
                        foreach (var modUtoc in modUtocFiles)
                        {
                            try
                            {
                                int containerIdx = context.ContainerCount;
                                context.LoadContainerWithPriority(modUtoc);
                                modContainerIndices.Add(containerIdx);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"  Warning: Failed to load mod {Path.GetFileName(modUtoc)}: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine($"Warning: Mod path not found: {modPath}");
                    }
                }
                Console.WriteLine($"Loaded {modContainerIndices.Count} mod container(s)");
            }
            
            Console.WriteLine($"Total containers loaded: {context.ContainerCount}");
            Console.WriteLine($"Total packages indexed: {context.PackageCount}");

            Directory.CreateDirectory(outputDir);

            int converted = 0;
            int failed = 0;
            int skipped = 0;
            
            // Track extracted packages to avoid duplicates (ConcurrentDictionary used as concurrent set)
            var extractedPackages = new System.Collections.Concurrent.ConcurrentDictionary<ulong, bool>();
            HashSet<ulong> pendingDependencies = new();
            
            // Get package IDs to extract
            List<ulong> packageIds;
            bool skipFilterCheck = false; // Skip filter check in ExtractPackage if we already filtered
            bool extractFromModOnly = modContainerIndices.Count > 0; // When mods are specified, extract from mods
            
            if (extractFromModOnly && filterPatterns.Count == 0)
            {
                // No filter specified but mods are loaded - extract all packages from mod containers
                packageIds = new List<ulong>();
                foreach (var containerIdx in modContainerIndices)
                {
                    foreach (var pkgId in context.GetPackageIdsFromContainer(containerIdx))
                    {
                        if (!packageIds.Contains(pkgId))
                            packageIds.Add(pkgId);
                    }
                }
                skipFilterCheck = true;
                Console.WriteLine($"Extracting all {packageIds.Count} packages from mod container(s)");
            }
            else if (filterPatterns.Count > 0)
            {
                packageIds = new List<ulong>();
                
                foreach (var filterPattern in filterPatterns)
                {
                    // Check if filter looks like an exact package path (starts with /Game/)
                    if (filterPattern.StartsWith("/Game/"))
                    {
                        // Direct lookup by package path - much faster than iterating all packages
                        ulong packageId = ZenPackage.FPackageId.FromName(filterPattern);
                        if (context.HasPackage(packageId))
                        {
                            if (!packageIds.Contains(packageId))
                                packageIds.Add(packageId);
                            Console.WriteLine($"Direct lookup: found package {filterPattern}");
                        }
                        else
                        {
                            // Try partial match as fallback
                            var found = context.FindPackageIdByPath(filterPattern);
                            if (found.HasValue && !packageIds.Contains(found.Value))
                            {
                                packageIds.Add(found.Value);
                                Console.WriteLine($"Found package by partial match: {context.GetPackagePath(found.Value)}");
                            }
                            else if (!found.HasValue)
                            {
                                Console.Error.WriteLine($"Warning: Package not found: {filterPattern}");
                            }
                        }
                    }
                    else
                    {
                        // Partial filter - search through all packages (slower)
                        int matchCount = 0;
                        foreach (var pkgId in context.GetAllPackageIds())
                        {
                            string? path = context.GetPackagePath(pkgId);
                            if (!string.IsNullOrEmpty(path) && path.Contains(filterPattern, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!packageIds.Contains(pkgId))
                                {
                                    packageIds.Add(pkgId);
                                    matchCount++;
                                }
                            }
                        }
                        Console.WriteLine($"Filter '{filterPattern}' matched {matchCount} packages");
                    }
                }
                
                skipFilterCheck = true; // Already filtered
                Console.WriteLine($"Total packages matching filters [{string.Join(", ", filterPatterns)}]: {packageIds.Count}");
            }
            else
            {
                // No filter specified - require at least one filter to avoid extracting entire game
                Console.Error.WriteLine("Error: No filter specified. Use --filter to specify which packages to extract.");
                Console.Error.WriteLine("Example: --filter SK_1014 SK_1057");
                return 1;
            }

            bool debugMode = Environment.GetEnvironmentVariable("DEBUG") == "1";

            // Helper: resolve output path from package name
            static string ResolveOutputPath(string packageName)
            {
                string relPath = packageName;
                if (relPath.Contains("/../"))
                {
                    string tempRoot = Path.GetTempPath();
                    string tempPath = Path.Combine(tempRoot, relPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    string resolved = Path.GetFullPath(tempPath);
                    relPath = resolved.Substring(tempRoot.Length).Replace(Path.DirectorySeparatorChar, '/');
                    if (!relPath.StartsWith("/")) relPath = "/" + relPath;
                    int contentIdx = relPath.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                    if (contentIdx >= 0)
                        relPath = "/Game" + relPath.Substring(contentIdx + "/Content".Length);
                }
                if (relPath.StartsWith("/Game/"))
                    relPath = "Marvel/Content/" + relPath.Substring(6);
                else if (relPath.StartsWith("/"))
                    relPath = relPath.Substring(1);
                relPath = relPath.Replace('/', Path.DirectorySeparatorChar);
                if (!relPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                    relPath += ".uasset";
                return relPath;
            }

            // Helper function to extract a single package (thread-safe)
            List<ulong> ExtractPackage(ulong packageId, bool isDependency)
            {
                List<ulong> imports = new();

                if (extractedPackages.ContainsKey(packageId))
                    return imports;

                string? fullPath = context.GetPackagePath(packageId);

                if (!isDependency && !skipFilterCheck && filterPatterns.Count > 0)
                {
                    bool matchesAnyFilter = filterPatterns.Any(filter =>
                        !string.IsNullOrEmpty(fullPath) && fullPath.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    if (!matchesAnyFilter)
                        return imports;
                }

                var cached = context.GetCachedPackage(packageId);
                if (cached == null)
                {
                    string skipMsg = !string.IsNullOrEmpty(fullPath) ? fullPath : $"package ID {packageId:X16}";
                    if (isDependency || debugMode)
                        Console.WriteLine($"  Skipped (not found): {skipMsg}");
                    Interlocked.Increment(ref skipped);
                    return imports;
                }

                string packageName = !string.IsNullOrEmpty(fullPath) ? fullPath : cached.Header.PackageName();

                try
                {
                    string prefix = isDependency ? "[DEP] " : "";
                    if (debugMode)
                        Console.WriteLine($"  {prefix}Converting: {packageName} ({cached.RawData.Length} bytes)");

                    var converter = new ZenPackage.ZenToLegacyConverter(context, packageId);
                    if (debugMode) converter.SetDebugMode(true);
                    var legacyBundle = converter.Convert();

                    if (extractDependencies)
                    {
                        if (debugMode)
                        {
                            Console.WriteLine($"    Imports for {packageName}:");
                            foreach (var (id, name) in converter.GetImportedPackageInfo())
                                Console.WriteLine($"      {id:X16} = {name}");
                        }
                        imports.AddRange(converter.GetImportedPackageIds());
                    }

                    string relPath = ResolveOutputPath(packageName);
                    string outputAssetPath = Path.Combine(outputDir, relPath);
                    string? outputAssetDir = Path.GetDirectoryName(outputAssetPath);
                    if (!string.IsNullOrEmpty(outputAssetDir))
                        Directory.CreateDirectory(outputAssetDir);

                    File.WriteAllBytes(outputAssetPath, legacyBundle.AssetData);
                    File.WriteAllBytes(Path.ChangeExtension(outputAssetPath, ".uexp"), legacyBundle.ExportsData);

                    if (legacyBundle.BulkData != null && legacyBundle.BulkData.Length > 0)
                        File.WriteAllBytes(Path.ChangeExtension(outputAssetPath, ".ubulk"), legacyBundle.BulkData);
                    if (legacyBundle.OptionalBulkData != null && legacyBundle.OptionalBulkData.Length > 0)
                        File.WriteAllBytes(Path.ChangeExtension(outputAssetPath, ".uptnl"), legacyBundle.OptionalBulkData);
                    if (legacyBundle.MemoryMappedBulkData != null && legacyBundle.MemoryMappedBulkData.Length > 0)
                        File.WriteAllBytes(Path.ChangeExtension(outputAssetPath, ".m.ubulk"), legacyBundle.MemoryMappedBulkData);

                    extractedPackages.TryAdd(packageId, true);
                    Interlocked.Increment(ref converted);
                    if (debugMode)
                        Console.WriteLine($"{prefix}Converted: {packageName}");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Console.Error.WriteLine($"  Failed: {packageName}: {ex.Message}");
                    if (debugMode) Console.Error.WriteLine(ex.StackTrace);
                }

                return imports;
            }

            // Pre-load all packages into the cache sequentially before parallel conversion.
            // Retoc does the same (FZenPackageContext::create pre-populates the cache).
            // This avoids I/O contention during the parallel phase: Convert() only needs CPU.
            foreach (var packageId in packageIds)
                context.GetPackage(packageId);

            // Process primary packages in parallel (each ZenToLegacyConverter is independent)
            var allImports = new System.Collections.Concurrent.ConcurrentBag<ulong>();
            Parallel.ForEach(packageIds, packageId =>
            {
                var imports = ExtractPackage(packageId, isDependency: false);
                foreach (var id in imports) allImports.Add(id);
            });

            if (extractDependencies)
            {
                foreach (var id in allImports)
                    if (!extractedPackages.ContainsKey(id)) pendingDependencies.Add(id);
            }

            // Process dependencies sequentially (order matters for dep graph traversal)
            if (extractDependencies && pendingDependencies.Count > 0)
            {
                Console.WriteLine($"\nExtracting {pendingDependencies.Count} dependencies...");
                while (pendingDependencies.Count > 0)
                {
                    var currentBatch = pendingDependencies.ToList();
                    pendingDependencies.Clear();
                    foreach (var depId in currentBatch)
                    {
                        var newImports = ExtractPackage(depId, isDependency: true);
                        foreach (var importId in newImports)
                            if (!extractedPackages.ContainsKey(importId))
                                pendingDependencies.Add(importId);
                    }
                }
            }

            Console.WriteLine($"\nExtraction complete: {converted} converted, {failed} failed, {skipped} skipped");
            return failed > 0 && converted == 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int CliDetect(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool detect <path> [usmap_path]");
            Console.Error.WriteLine("  <path> can be a .uasset file or a directory");
            return 1;
        }

        string inputPath = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;

        // Single file
        if (File.Exists(inputPath))
        {
            var asset = LoadAsset(inputPath, usmapPath);
            var assetType = DetectAssetType(asset);
            
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                path = inputPath,
                asset_type = assetType,
                export_count = asset.Exports.Count,
                import_count = asset.Imports.Count
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        // Directory (batch)
        if (Directory.Exists(inputPath))
        {
            Usmap? mappings = LoadMappings(usmapPath);
            var uassetFiles = Directory.GetFiles(inputPath, "*.uasset", SearchOption.AllDirectories);
            Console.Error.WriteLine($"Scanning {uassetFiles.Length} .uasset files...");

            var results = new List<object>();
            foreach (var uassetPath in uassetFiles)
            {
                try
                {
                    var asset = LoadAssetWithMappings(uassetPath, mappings);
                    string assetType = DetectAssetType(asset);
                    results.Add(new
                    {
                        path = uassetPath,
                        asset_type = assetType,
                        file_name = Path.GetFileName(uassetPath)
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to process {uassetPath}: {ex.Message}");
                }
            }

            var grouped = results.GroupBy(r => ((dynamic)r).asset_type)
                                .Select(g => new { asset_type = g.Key, count = g.Count(), files = g.ToList() })
                                .ToList();

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                total_files = uassetFiles.Length,
                by_type = grouped
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.Error.WriteLine($"Path not found: {inputPath}");
        return 1;
    }
    
    private static int CliFix(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool fix <uasset_path> [usmap_path]");
            return 1;
        }

        string uassetPath = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;

        if (!File.Exists(uassetPath))
        {
            Console.Error.WriteLine($"File not found: {uassetPath}");
            return 1;
        }

        var result = FixSerializeSize(uassetPath, usmapPath);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    
    private static int CliBatchDetect(string[] args) => CliDetect(args);
    
    private static int CliDump(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool dump <uasset_path> <usmap_path>");
            return 1;
        }

        string uassetPath = args[1];
        string usmapPath = args[2];

        if (!File.Exists(uassetPath))
        {
            Console.Error.WriteLine($"File not found: {uassetPath}");
            return 1;
        }

        var asset = LoadAsset(uassetPath, usmapPath);
        DumpAssetInfo(asset, uassetPath);
        return 0;
    }
    
    private static int CliFromJson(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool from_json <json_path_or_dir> <output_path_or_dir> [usmap_path]");
            Console.Error.WriteLine("  <json_path_or_dir>   - Path to a .json file or directory containing .json files");
            Console.Error.WriteLine("  <output_path_or_dir> - Output .uasset path (single file) or output directory (batch)");
            Console.Error.WriteLine("  [usmap_path]         - Optional path to .usmap mappings file");
            return 1;
        }

        string inputPath = args[1];
        string outputPath = args[2];
        string? usmapPath = args.Length > 3 ? args[3] : null;

        // Load mappings once if provided
        Usmap? mappings = null;
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            mappings = new Usmap(usmapPath);

        int successCount = 0;
        int failCount = 0;

        if (Directory.Exists(inputPath))
        {
            // Batch mode: process all .json files in directory
            var files = Directory.GetFiles(inputPath, "*.json", SearchOption.AllDirectories);
            Console.WriteLine($"Found {files.Length} .json files in {inputPath}");

            foreach (var file in files)
            {
                try
                {
                    // Preserve relative directory structure in output
                    string relativePath = Path.GetRelativePath(inputPath, file);
                    string relativeDir = Path.GetDirectoryName(relativePath) ?? "";
                    string outputSubDir = Path.Combine(outputPath, relativeDir);
                    Directory.CreateDirectory(outputSubDir);
                    string uassetOutputPath = Path.Combine(outputSubDir, Path.GetFileNameWithoutExtension(file) + ".uasset");

                    string jsonData = File.ReadAllText(file, System.Text.Encoding.UTF8);
                    var asset = UAsset.DeserializeJson(jsonData);
                    if (asset == null)
                    {
                        Console.Error.WriteLine($"Failed to deserialize: {file}");
                        failCount++;
                        continue;
                    }

                    asset.Mappings = mappings;
                    asset.FilePath = Path.GetFullPath(uassetOutputPath);
                    PreloadReferencedAssetsForSchemas(asset);
                    asset.Write(uassetOutputPath);
                    Console.WriteLine($"Converted: {file} -> {uassetOutputPath}");
                    successCount++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to convert {file}: {ex.Message}");
                    failCount++;
                }
            }

            Console.WriteLine($"\nBatch conversion complete: {successCount} succeeded, {failCount} failed");
        }
        else if (File.Exists(inputPath))
        {
            // Single file mode
            try
            {
                string jsonData = File.ReadAllText(inputPath, System.Text.Encoding.UTF8);
                var asset = UAsset.DeserializeJson(jsonData);
                if (asset == null)
                {
                    Console.Error.WriteLine("Failed to deserialize JSON");
                    return 1;
                }

                asset.Mappings = mappings;
                asset.FilePath = Path.GetFullPath(outputPath);
                PreloadReferencedAssetsForSchemas(asset);

                string? outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outDir))
                    Directory.CreateDirectory(outDir);

                asset.Write(outputPath);
                Console.WriteLine($"Asset imported from JSON and saved to {outputPath}");
                successCount = 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to convert: {ex.Message}");
                return 1;
            }
        }
        else
        {
            Console.Error.WriteLine($"Path not found: {inputPath}");
            return 1;
        }

        return failCount > 0 ? 1 : 0;
    }
    
    private static int CliToJson(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool to_json <path> [usmap_path] [output_dir] [--compact]");
            Console.Error.WriteLine("  <path>       - Path to a .uasset file or directory containing .uasset files");
            Console.Error.WriteLine("  [usmap_path] - Optional path to .usmap mappings file");
            Console.Error.WriteLine("  [output_dir] - Optional output directory (default: same as input)");
            Console.Error.WriteLine("  --compact    - Output compact CUE4Parse-style JSON (read-only, no roundtrip)");
            return 1;
        }

        bool compact = args.Any(a => a == "--compact");
        var positionalArgs = args.Where(a => !a.StartsWith("--")).ToArray();
        string inputPath = positionalArgs[1];
        string? usmapPath = positionalArgs.Length > 2 ? positionalArgs[2] : null;
        string? outputDir = positionalArgs.Length > 3 ? positionalArgs[3] : null;

        int successCount = 0;
        int failCount = 0;

        if (Directory.Exists(inputPath))
        {
            // Batch mode: process all .uasset files in directory
            var files = Directory.GetFiles(inputPath, "*.uasset", SearchOption.AllDirectories);
            Console.WriteLine($"Found {files.Length} .uasset files in {inputPath}");

            foreach (var file in files)
            {
                try
                {
                    string jsonOutputPath;
                    if (!string.IsNullOrEmpty(outputDir))
                    {
                        // Preserve relative directory structure in output
                        string relativePath = Path.GetRelativePath(inputPath, file);
                        string relativeDir = Path.GetDirectoryName(relativePath) ?? "";
                        string outputSubDir = Path.Combine(outputDir, relativeDir);
                        Directory.CreateDirectory(outputSubDir);
                        jsonOutputPath = Path.Combine(outputSubDir, Path.GetFileNameWithoutExtension(file) + ".json");
                    }
                    else
                    {
                        jsonOutputPath = Path.ChangeExtension(file, ".json");
                    }

                    var asset = LoadAsset(file, usmapPath);
                    PreloadReferencedAssetsForSchemas(asset);
                    string json = compact
                        ? CompactJsonSerializer.Serialize(asset)
                        : asset.SerializeJson(true);
                    File.WriteAllText(jsonOutputPath, json, System.Text.Encoding.UTF8);
                    Console.WriteLine($"Converted{(compact ? " (compact)" : "")}: {file} -> {jsonOutputPath}");
                    successCount++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to convert {file}: {ex.Message}");
                    failCount++;
                }
            }

            Console.WriteLine($"\nBatch conversion complete: {successCount} succeeded, {failCount} failed");
        }
        else if (File.Exists(inputPath))
        {
            // Single file mode
            try
            {
                string jsonOutputPath;
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                    jsonOutputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(inputPath) + ".json");
                }
                else
                {
                    jsonOutputPath = Path.ChangeExtension(inputPath, ".json");
                }

                var asset = LoadAsset(inputPath, usmapPath);
                PreloadReferencedAssetsForSchemas(asset);
                string json = compact
                    ? CompactJsonSerializer.Serialize(asset)
                    : asset.SerializeJson(true);
                File.WriteAllText(jsonOutputPath, json, System.Text.Encoding.UTF8);
                Console.WriteLine($"Asset exported to JSON{(compact ? " (compact)" : "")}: {jsonOutputPath}");
                successCount = 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to convert: {ex.Message}");
                return 1;
            }
        }
        else
        {
            Console.Error.WriteLine($"Path not found: {inputPath}");
            return 1;
        }

        return failCount > 0 ? 1 : 0;
    }

    /// <summary>
    /// Proof of concept: Selective parsing for Niagara color editing.
    /// Two-pass approach:
    ///   Pass 1: Load with SkipParsingExports to read header + export map (cheap, ~raw bytes only)
    ///   Pass 2: Reload with manualSkips (all indices) + forceReads (color indices only)
    /// </summary>
    private static int CliNiagaraPoc(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_poc <asset_path> [usmap_path]");
            return 1;
        }

        string assetPath = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;

        if (!File.Exists(assetPath))
        {
            Console.Error.WriteLine($"File not found: {assetPath}");
            return 1;
        }

        // Color-relevant Niagara export class types
        var colorClassTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NiagaraDataInterfaceColorCurve",
            "NiagaraDataInterfaceArrayColor",
            "NiagaraDataInterfaceVector4Curve",
            "NiagaraDataInterfaceVectorCurve",
            "NiagaraDataInterfaceArrayFloat4",
            "NiagaraDataInterfaceArrayFloat3",
            "NiagaraDataInterfaceCurve",
            "NiagaraDataInterfaceVector2DCurve",
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();

        Usmap? mappings = null;
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            mappings = new Usmap(usmapPath);

        // ── Pass 1: header-only load to discover export class types ──
        var headerAsset = new UAsset(assetPath, EngineVersion.VER_UE5_3, mappings,
            customSerializationFlags: CustomSerializationFlags.SkipParsingExports);

        long afterHeaderMs = sw.ElapsedMilliseconds;
        long ramAfterHeader = GC.GetTotalMemory(false);

        Console.WriteLine($"=== Niagara POC: Selective Parse ===");
        Console.WriteLine($"Asset: {Path.GetFileName(assetPath)}");
        Console.WriteLine($"Total exports: {headerAsset.Exports.Count}");
        Console.WriteLine($"Pass 1 (header): {afterHeaderMs}ms, RAM: {ramAfterHeader / 1024}KB");

        // Identify color-relevant exports by class type
        var colorExportIndices = new List<int>();
        for (int i = 0; i < headerAsset.Exports.Count; i++)
        {
            string classType = headerAsset.Exports[i].GetExportClassType().Value.Value;
            if (colorClassTypes.Contains(classType))
            {
                colorExportIndices.Add(i);
                Console.WriteLine($"  [COLOR] Export {i}: {classType} — \"{headerAsset.Exports[i].ObjectName}\"");
            }
        }

        Console.WriteLine($"\nColor-relevant exports: {colorExportIndices.Count} / {headerAsset.Exports.Count}");

        if (colorExportIndices.Count == 0)
        {
            Console.WriteLine("No color exports found.");
            return 0;
        }

        // ── Pass 2: reload with manualSkips + forceReads ──
        // manualSkips = ALL indices (generous upper bound), forceReads = color indices only
        int exportCount = headerAsset.Exports.Count;
        headerAsset = null;
        GC.Collect();

        var asset = new UAsset();
        asset.FilePath = assetPath;
        asset.Mappings = mappings;
        asset.CustomSerializationFlags = CustomSerializationFlags.None;
        asset.SetEngineVersion(EngineVersion.VER_UE5_3);

        int[] manualSkips = Enumerable.Range(0, exportCount).ToArray();
        int[] forceReads = colorExportIndices.ToArray();
        string uexpPath = Path.ChangeExtension(assetPath, ".uexp");

        long beforePass2 = sw.ElapsedMilliseconds;
        asset.Read(asset.PathToReader(assetPath), manualSkips, forceReads);
        long afterPass2 = sw.ElapsedMilliseconds;
        long ramAfterPass2 = GC.GetTotalMemory(false);

        Console.WriteLine($"Pass 2 (selective): {afterPass2 - beforePass2}ms, RAM: {ramAfterPass2 / 1024}KB");
        Console.WriteLine($"Exports: {asset.Exports.Count} total, {asset.Exports.Count(e => e is not RawExport)} parsed, {asset.Exports.Count(e => e is RawExport)} raw");

        // ── Dump color data from parsed exports ──
        foreach (int idx in colorExportIndices)
        {
            if (idx >= asset.Exports.Count) continue;
            var export = asset.Exports[idx];
            Console.WriteLine($"\n  Export {idx}: {export.GetType().Name} — {export.GetExportClassType().Value.Value}");

            if (export is NormalExport normalExport)
            {
                string classType = normalExport.GetExportClassType().Value.Value;

                // ShaderLUT
                var shaderLut = FindProperty(normalExport.Data, "ShaderLUT");
                if (shaderLut is ArrayPropertyData lutArray)
                {
                    Console.WriteLine($"    ShaderLUT: {lutArray.Value.Length} floats");
                    int channels = classType.Contains("Color") ? 4 :
                                   classType.Contains("Vector4") ? 4 :
                                   classType.Contains("VectorCurve") ? 3 :
                                   classType.Contains("Vector2D") ? 2 : 1;
                    int samples = Math.Min(3, lutArray.Value.Length / Math.Max(channels, 1));
                    for (int s = 0; s < samples; s++)
                    {
                        var vals = new List<string>();
                        for (int c = 0; c < channels && (s * channels + c) < lutArray.Value.Length; c++)
                        {
                            if (lutArray.Value[s * channels + c] is FloatPropertyData fp)
                                vals.Add($"{fp.Value:F4}");
                        }
                        Console.WriteLine($"    Sample {s}: [{string.Join(", ", vals)}]");
                    }
                }

                // ColorData
                var colorData = FindProperty(normalExport.Data, "ColorData");
                if (colorData is ArrayPropertyData colorArray)
                {
                    Console.WriteLine($"    ColorData: {colorArray.Value.Length} entries");
                    int show = Math.Min(3, colorArray.Value.Length);
                    for (int s = 0; s < show; s++)
                    {
                        if (colorArray.Value[s] is StructPropertyData colorStruct)
                        {
                            float r = 0, g = 0, b = 0, a = 0;
                            foreach (var prop in colorStruct.Value)
                            {
                                if (prop is FloatPropertyData fp)
                                {
                                    if (prop.Name.Value.Value == "R") r = fp.Value;
                                    else if (prop.Name.Value.Value == "G") g = fp.Value;
                                    else if (prop.Name.Value.Value == "B") b = fp.Value;
                                    else if (prop.Name.Value.Value == "A") a = fp.Value;
                                }
                            }
                            Console.WriteLine($"    Color {s}: ({r:F4}, {g:F4}, {b:F4}, {a:F4})");
                        }
                    }
                }

                // LUT metadata
                var lutMinTime = FindProperty(normalExport.Data, "LUTMinTime");
                var lutMaxTime = FindProperty(normalExport.Data, "LUTMaxTime");
                var numSamples = FindProperty(normalExport.Data, "LUTNumSamplesMinusOne");
                if (lutMinTime is FloatPropertyData minT && lutMaxTime is FloatPropertyData maxT)
                    Console.WriteLine($"    LUT range: [{minT.Value:F4}, {maxT.Value:F4}]");
                if (numSamples is FloatPropertyData nsp)
                    Console.WriteLine($"    LUT samples: {(int)nsp.Value + 1}");

                // RichCurve keys
                var curveNames = new[] { "RedCurve", "GreenCurve", "BlueCurve", "AlphaCurve",
                                         "XCurve", "YCurve", "ZCurve", "WCurve", "Curve" };
                foreach (var curveName in curveNames)
                {
                    var curve = FindProperty(normalExport.Data, curveName);
                    if (curve is StructPropertyData curveStruct)
                    {
                        var keys = FindProperty(curveStruct.Value, "Keys");
                        if (keys is ArrayPropertyData keysArray)
                            Console.WriteLine($"    {curveName}: {keysArray.Value.Length} keys");
                    }
                }
            }
            else
            {
                Console.WriteLine($"    (RawExport — parse failed, {((RawExport)export).Data?.Length ?? 0} bytes)");
            }
        }

        sw.Stop();
        Console.WriteLine($"\nTotal time: {sw.ElapsedMilliseconds}ms");

        // ── Verify Write() roundtrip ──
        string tempPath = assetPath + ".poc_test";
        try
        {
            asset.Write(tempPath);
            var origUasset = File.ReadAllBytes(assetPath);
            var newUasset = File.ReadAllBytes(tempPath);
            bool uassetMatch = origUasset.Length == newUasset.Length && origUasset.SequenceEqual(newUasset);

            var origUexp = File.ReadAllBytes(uexpPath);
            var newUexpBytes = File.ReadAllBytes(Path.ChangeExtension(tempPath, ".uexp"));
            bool uexpMatch = origUexp.Length == newUexpBytes.Length && origUexp.SequenceEqual(newUexpBytes);

            Console.WriteLine($"\nRoundtrip .uasset: {(uassetMatch ? "IDENTICAL ✓" : $"DIFFERS (orig={origUasset.Length}, new={newUasset.Length})")}");
            Console.WriteLine($"Roundtrip .uexp:   {(uexpMatch ? "IDENTICAL ✓" : $"DIFFERS (orig={origUexp.Length}, new={newUexpBytes.Length})")}");

            if (!uexpMatch)
            {
                int minLen = Math.Min(origUexp.Length, newUexpBytes.Length);
                int diffCount = 0;
                int firstDiff = -1;
                for (int i = 0; i < minLen; i++)
                {
                    if (origUexp[i] != newUexpBytes[i])
                    {
                        if (firstDiff < 0) firstDiff = i;
                        diffCount++;
                    }
                }
                diffCount += Math.Abs(origUexp.Length - newUexpBytes.Length);
                Console.WriteLine($"  First diff at byte {firstDiff}: orig=0x{origUexp[firstDiff]:X2} new=0x{newUexpBytes[firstDiff]:X2}, total diffs: {diffCount}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Write test failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
            try { File.Delete(Path.ChangeExtension(tempPath, ".uexp")); } catch { }
        }

        return 0;
    }

    /// <summary>
    /// niagara_audit: Deep audit of ALL export class types + color properties in an NS asset.
    /// Parses renderer/emitter/system exports to find any color values we might be missing.
    /// </summary>
    private static int CliNiagaraAudit(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_audit <asset_path> [usmap_path]");
            return 1;
        }
        string assetPath = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;
        if (!File.Exists(assetPath)) { Console.Error.WriteLine($"File not found: {assetPath}"); return 1; }
        Usmap? mappings = null;
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath)) mappings = new Usmap(usmapPath);

        // Types we want to deep-inspect for color properties
        var inspectTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NiagaraSpriteRendererProperties",
            "NiagaraMeshRendererProperties",
            "NiagaraRibbonRendererProperties",
            "NiagaraLightRendererProperties",
            "NiagaraDecalRendererProperties",
            "NiagaraEmitter",
            "NiagaraSystem",
        };

        // Pass 1: header scan
        var headerAsset = new UAsset(assetPath, EngineVersion.VER_UE5_3, mappings,
            customSerializationFlags: CustomSerializationFlags.SkipParsingExports);
        int exportCount = headerAsset.Exports.Count;

        var inspectIndices = new List<int>();
        for (int i = 0; i < exportCount; i++)
        {
            string ct = headerAsset.Exports[i].GetExportClassType().Value.Value;
            Console.WriteLine($"[{i}] {ct}");
            if (inspectTypes.Contains(ct))
                inspectIndices.Add(i);
        }
        headerAsset = null;

        if (inspectIndices.Count == 0) return 0;

        // Pass 2: selectively parse only the inspect types
        var asset = new UAsset();
        asset.FilePath = assetPath;
        asset.Mappings = mappings;
        asset.CustomSerializationFlags = CustomSerializationFlags.None;
        asset.SetEngineVersion(EngineVersion.VER_UE5_3);
        int[] manualSkips = Enumerable.Range(0, exportCount).ToArray();
        int[] forceReads = inspectIndices.ToArray();
        asset.Read(asset.PathToReader(assetPath), manualSkips, forceReads);

        // Deep-inspect parsed exports for any color-carrying properties
        foreach (int idx in inspectIndices)
        {
            if (idx >= asset.Exports.Count) continue;
            var export = asset.Exports[idx];
            string ct = export.GetExportClassType().Value.Value;
            if (export is NormalExport ne)
            {
                Console.WriteLine($"\n=== [{idx}] {ct} (parsed, {ne.Data?.Count ?? 0} props) ===");
                ScanPropsForColor(ne.Data, "  ");
            }
            else
            {
                Console.WriteLine($"\n=== [{idx}] {ct} (RAW — parse failed) ===");
            }
        }
        return 0;
    }

    /// <summary>
    /// Recursively scan properties for any FLinearColor or color-related values.
    /// </summary>
    private static void ScanPropsForColor(List<PropertyData>? props, string indent)
    {
        if (props == null) return;
        foreach (var p in props)
        {
            string name = p.Name?.Value?.Value ?? "?";

            // Direct FLinearColor fields
            if (p is StructPropertyData sp)
            {
                string structType = sp.StructType?.Value?.Value ?? "";
                if (structType == "LinearColor" || name.Contains("Color", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract RGBA
                    float r = 0, g = 0, b = 0, a = 0;
                    if (sp.Value != null)
                    {
                        foreach (var sub in sp.Value)
                        {
                            if (sub is FloatPropertyData fp)
                            {
                                var sn = sub.Name?.Value?.Value;
                                if (sn == "R") r = fp.Value;
                                else if (sn == "G") g = fp.Value;
                                else if (sn == "B") b = fp.Value;
                                else if (sn == "A") a = fp.Value;
                            }
                        }
                    }
                    Console.WriteLine($"{indent}COLOR: {name} ({structType}) = ({r:F4}, {g:F4}, {b:F4}, {a:F4})");
                }
                else
                {
                    // Recurse into sub-structs
                    if (sp.Value != null && sp.Value.Count > 0)
                        ScanPropsForColor(sp.Value, indent + "  ");
                }
            }
            else if (p is ArrayPropertyData arr)
            {
                // Check if array contains color structs
                bool hasColor = false;
                if (arr.Value != null)
                {
                    foreach (var entry in arr.Value)
                    {
                        if (entry is StructPropertyData arrSp)
                        {
                            string st = arrSp.StructType?.Value?.Value ?? "";
                            if (st == "LinearColor" || st.Contains("Color") ||
                                st == "NiagaraRendererMaterialVectorParameter")
                            {
                                hasColor = true;
                                break;
                            }
                        }
                    }
                }
                if (hasColor)
                {
                    Console.WriteLine($"{indent}ARRAY: {name} ({arr.Value?.Length ?? 0} entries) — contains color data:");
                    if (arr.Value != null)
                    {
                        for (int i = 0; i < Math.Min(5, arr.Value.Length); i++)
                        {
                            if (arr.Value[i] is StructPropertyData es)
                                ScanPropsForColor(es.Value, indent + "  ");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// niagara_details: Output JSON describing all color-relevant exports in a Niagara asset.
    /// Usage: UAssetTool niagara_details &lt;asset_path&gt; --usmap &lt;usmap_path&gt;
    /// </summary>
    private static int CliNiagaraDetails(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_details <asset_path> --usmap <usmap_path>");
            return 1;
        }

        string assetPath = args[1];
        string? usmapPath = null;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--usmap") { usmapPath = args[i + 1]; break; }
        }

        if (!File.Exists(assetPath))
        {
            Console.Error.WriteLine($"File not found: {assetPath}");
            return 1;
        }

        Usmap? mappings = null;
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            mappings = new Usmap(usmapPath);

        string json = NiagaraService.GetColorDetails(assetPath, mappings);
        Console.WriteLine(json);
        return 0;
    }

    /// <summary>
    /// niagara_edit: Edit color data in a Niagara asset.
    /// Usage: UAssetTool niagara_edit &lt;asset_path&gt; --usmap &lt;usmap_path&gt; --output &lt;output_path&gt; --edits &lt;edits_json&gt;
    /// Edits JSON format: [{"exportIndex":4,"flatLut":[r,g,b,a,r,g,b,a,...]}]
    /// </summary>
    private static int CliNiagaraEdit(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_edit <asset_path> --usmap <usmap_path> --output <output_path> --edits <edits_json>");
            return 1;
        }

        string assetPath = args[1];
        string? usmapPath = null;
        string? outputPath = null;
        string? editsJson = null;

        string? editsFile = null;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--usmap") usmapPath = args[i + 1];
            else if (args[i] == "--output") outputPath = args[i + 1];
            else if (args[i] == "--edits") editsJson = args[i + 1];
            else if (args[i] == "--edits-file") editsFile = args[i + 1];
        }

        if (!File.Exists(assetPath))
        {
            Console.Error.WriteLine($"File not found: {assetPath}");
            return 1;
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            // Default: overwrite in place
            outputPath = assetPath;
        }

        // Load edits from file if --edits-file is provided
        if (!string.IsNullOrEmpty(editsFile))
        {
            if (!File.Exists(editsFile))
            {
                Console.Error.WriteLine($"Edits file not found: {editsFile}");
                return 1;
            }
            editsJson = File.ReadAllText(editsFile);
        }

        if (string.IsNullOrEmpty(editsJson))
        {
            Console.Error.WriteLine("Missing --edits or --edits-file parameter.");
            return 1;
        }

        Usmap? mappings = null;
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            mappings = new Usmap(usmapPath);

        var editOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var edits = JsonSerializer.Deserialize<List<NiagaraService.NiagaraEditRequest>>(editsJson, editOpts);
        if (edits == null || edits.Count == 0)
        {
            Console.Error.WriteLine("No valid edits in --edits JSON.");
            return 1;
        }

        NiagaraService.EditColors(assetPath, outputPath, mappings, edits);
        Console.WriteLine($"Edited {edits.Count} export(s), saved to: {outputPath}");
        return 0;
    }

    /// <summary>
    /// Find a property by name in a list of PropertyData.
    /// </summary>
    private static PropertyData? FindProperty(List<PropertyData> properties, string name)
    {
        if (properties == null) return null;
        foreach (var prop in properties)
        {
            if (prop.Name?.Value?.Value == name) return prop;
        }
        return null;
    }

    #endregion

    #region Interactive JSON Mode
    
    private static async Task<int> RunInteractiveMode()
    {
        try
        {
            // Use StreamReader with explicit UTF-8 encoding to properly handle Unicode characters
            // This is necessary because Console.In may not properly decode UTF-8 from piped input
            using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try 
                {
                    var request = JsonSerializer.Deserialize<UAssetRequest>(line);
                    if (request == null)
                    {
                        WriteJsonResponse(false, "Invalid JSON request");
                        continue;
                    }

                    var response = ProcessRequest(request);
                    var responseJson = JsonSerializer.Serialize(response);
                    Console.WriteLine(responseJson.Replace("\r", "").Replace("\n", ""));
                    Console.Out.Flush(); // Ensure response is sent immediately
                }
                catch (JsonException)
                {
                    WriteJsonResponse(false, "Invalid JSON format");
                    Console.Out.Flush();
                }
            }
        }
        catch (Exception ex)
        {
            WriteJsonResponse(false, $"Unhandled exception: {ex.Message}");
        }
        
        return 0;
    }
    
    public static UAssetResponse ProcessRequest(UAssetRequest request)
    {
        try
        {
            return request.Action switch
            {
                // Unified type detection - returns proper UE class names
                "detect_type" => DetectTypeUnified(request.FilePath, request.FilePaths, request.UsmapPath),
                
                // Legacy single file detection (backward compat)
                "detect_texture" => DetectSingleAsset(request.FilePath, request.UsmapPath, "texture"),
                "detect_mesh" => DetectSingleAsset(request.FilePath, request.UsmapPath, "skeletal_mesh"),
                "detect_skeletal_mesh" => DetectSingleAsset(request.FilePath, request.UsmapPath, "skeletal_mesh"),
                "detect_static_mesh" => DetectSingleAsset(request.FilePath, request.UsmapPath, "static_mesh"),
                "detect_blueprint" => DetectSingleAsset(request.FilePath, request.UsmapPath, "blueprint"),
                
                // Legacy batch detection (backward compat)
                "batch_detect_skeletal_mesh" => BatchDetectAssetType(request.FilePaths, request.UsmapPath, "skeletal_mesh"),
                "batch_detect_static_mesh" => BatchDetectAssetType(request.FilePaths, request.UsmapPath, "static_mesh"),
                "batch_detect_texture" => BatchDetectAssetType(request.FilePaths, request.UsmapPath, "texture"),
                "batch_detect_blueprint" => BatchDetectAssetType(request.FilePaths, request.UsmapPath, "blueprint"),
                
                // Texture operations
                "get_texture_info" => GetTextureInfo(request.FilePath, request.UsmapPath),
                "strip_mipmaps_native" => StripMipmapsNative(request.FilePath, request.UsmapPath),
                "batch_strip_mipmaps_native" => BatchStripMipmapsNative(request.FilePaths, request.UsmapPath, request.Parallel),
                "has_inline_texture_data" => HasInlineTextureData(request.FilePath, request.UsmapPath),
                "batch_has_inline_texture_data" => BatchHasInlineTextureData(request.FilePaths, request.UsmapPath),
                
                // Mesh operations
                "patch_mesh" => PatchMesh(request.FilePath, request.UexpPath),
                "get_mesh_info" => GetMeshInfo(request.FilePath, request.UsmapPath),
                "fix_serialize_size" => FixSerializeSizeJson(request.FilePath, request.UsmapPath),
                
                // Zen conversion operations
                "convert_to_zen" => ConvertToZen(request.FilePath, request.UsmapPath),
                "convert_from_zen" => ConvertFromZen(request.FilePath, request.UsmapPath),
                
                // GUI Backend - Asset Structure
                "get_asset_summary" => GetAssetSummary(request.FilePath, request.UsmapPath),
                "get_name_map" => GetNameMap(request.FilePath, request.UsmapPath),
                "get_imports" => GetImports(request.FilePath, request.UsmapPath),
                "get_exports" => GetExports(request.FilePath, request.UsmapPath),
                "get_export_properties" => GetExportProperties(request.FilePath, request.UsmapPath, request.ExportIndex),
                "get_export_raw_data" => GetExportRawData(request.FilePath, request.UsmapPath, request.ExportIndex),
                
                // GUI Backend - Property Editing
                "set_property_value" => SetPropertyValue(request.FilePath, request.UsmapPath, request.ExportIndex, request.PropertyPath, request.PropertyValue),
                "add_property" => AddProperty(request.FilePath, request.UsmapPath, request.ExportIndex, request.PropertyName, request.PropertyType, request.PropertyValue),
                "remove_property" => RemoveProperty(request.FilePath, request.UsmapPath, request.ExportIndex, request.PropertyPath),
                
                // GUI Backend - Save/Export
                "save_asset" => SaveAsset(request.FilePath, request.UsmapPath, request.OutputPath),
                "export_to_json" => ExportToJson(request.FilePath, request.UsmapPath),
                "import_from_json" => ImportFromJson(request.FilePath, request.UsmapPath, request.JsonData),
                
                // Debug
                "debug_asset_info" => DebugAssetInfo(request.FilePath),
                
                // PAK operations
                "list_pak_files" => ListPakFiles(request.FilePath, request.AesKey),
                "list_pak" => ListPakJson(request.FilePath, request.AesKey, request.FilterPatterns),
                "extract_pak_file" => ExtractPakFile(request.FilePath, request.InternalPath, request.OutputPath, request.AesKey),
                "extract_pak_all" => ExtractPakAll(request.FilePath, request.OutputPath, request.AesKey),
                "create_pak" => CreatePakJson(request.OutputPath, request.FilePaths, request.MountPoint, request.PathHashSeed, request.AesKey, request.BasePath),
                "create_companion_pak" => CreateCompanionPakJson(request.OutputPath, request.FilePaths, request.MountPoint, request.PathHashSeed, request.AesKey),
                
                // IoStore operations
                "list_iostore_files" => ListIoStoreFiles(request.FilePath, request.AesKey),
                "create_iostore" => CreateIoStoreJson(request.OutputPath, request.InputDir, request.Compress, request.AesKey),
                "is_iostore_compressed" => IsIoStoreCompressed(request.FilePath),
                "is_iostore_encrypted" => IsIoStoreEncrypted(request.FilePath),
                "recompress_iostore" => RecompressIoStore(request.FilePath),
                "extract_iostore" => ExtractIoStoreJson(request.FilePath, request.OutputPath, request.AesKey),
                "extract_iostore_legacy" => ExtractIoStoreLegacyJson(request.GamePaks, request.ModPath, request.OutputPath, request.FilterPatterns, request.AesKey, request.WithDeps),
                "extract_script_objects" => ExtractScriptObjectsJson(request.FilePath, request.OutputPath),
                "create_mod_iostore" => CreateModIoStoreJson(request.OutputPath, request.InputDir, request.InputPak, request.MountPoint, request.Compress, request.AesKey, request.Parallel, request.Obfuscate, request.Hybrid),
                
                // Additional CLI-equivalent operations for parity
                "dump" => DumpAssetJson(request.FilePath, request.UsmapPath),
                "skeletal_mesh_info" => GetSkeletalMeshInfoJson(request.FilePath, request.UsmapPath),
                "to_json" or "batch_to_json" => ToJsonJson(request.FilePath, request.FilePaths, request.UsmapPath, request.OutputPath, request.Compact, request.BasePath),
                "compact_json" => ToJsonJson(request.FilePath, request.FilePaths, request.UsmapPath, request.OutputPath, compact: true, basePath: request.BasePath),
                "from_json" or "batch_from_json" => FromJsonJson(request.FilePath, request.FilePaths, request.OutputPath, request.UsmapPath, request.BasePath),
                "cityhash" => CityHashJson(request.FilePath),
                "clone_mod_iostore" => CloneModIoStoreJson(request.FilePath, request.OutputPath),
                "inspect_zen" => InspectZenJson(request.FilePath),
                "parse_locres" => ParseLocresJson(request.FilePath, request.FilePaths),
                "extract_texture_png" => ExtractTextureToPngJson(request.FilePath, request.OutputPath, request.UsmapPath, request.MipIndex, request.Format),
                "batch_extract_texture_png" => BatchExtractTexturePngJson(request.FilePaths, request.OutputPath, request.UsmapPath, request.MipIndex, request.Format, request.Parallel, request.BasePath),
                
                _ => new UAssetResponse { Success = false, Message = $"Unknown action: {request.Action}" }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    #endregion
    
    #region GUI Backend Methods
    
    /// <summary>
    /// Get a summary of the asset including header info, counts, and detected type
    /// </summary>
    private static UAssetResponse GetAssetSummary(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        var summary = new Dictionary<string, object?>
        {
            ["file_path"] = filePath,
            ["file_name"] = Path.GetFileName(filePath),
            ["detected_type"] = DetectAssetType(asset),
            ["name_count"] = asset.GetNameMapIndexList().Count,
            ["import_count"] = asset.Imports?.Count ?? 0,
            ["export_count"] = asset.Exports?.Count ?? 0,
            ["has_unversioned_properties"] = asset.HasUnversionedProperties,
            ["package_flags"] = asset.PackageFlags.ToString(),
            ["file_version_ue4"] = asset.ObjectVersion.ToString(),
            ["file_version_ue5"] = asset.ObjectVersionUE5.ToString(),
            ["uses_event_driven_loader"] = asset.UsesEventDrivenLoader,
            ["package_guid"] = asset.PackageGuid.ToString()
        };
        
        return new UAssetResponse { Success = true, Message = "Asset summary retrieved", Data = summary };
    }
    
    /// <summary>
    /// Get the name map (list of all FNames in the asset)
    /// </summary>
    private static UAssetResponse GetNameMap(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        var names = new List<Dictionary<string, object>>();
        var nameMap = asset.GetNameMapIndexList();
        for (int i = 0; i < nameMap.Count; i++)
        {
            names.Add(new Dictionary<string, object>
            {
                ["index"] = i,
                ["value"] = nameMap[i]
            });
        }
        
        return new UAssetResponse { Success = true, Message = $"Retrieved {names.Count} names", Data = names };
    }
    
    /// <summary>
    /// Get all imports with their details
    /// </summary>
    private static UAssetResponse GetImports(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        var imports = new List<Dictionary<string, object?>>();
        if (asset.Imports != null)
        {
            for (int i = 0; i < asset.Imports.Count; i++)
            {
                var imp = asset.Imports[i];
                imports.Add(new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["class_package"] = imp.ClassPackage?.ToString(),
                    ["class_name"] = imp.ClassName?.ToString(),
                    ["object_name"] = imp.ObjectName?.ToString(),
                    ["outer_index"] = imp.OuterIndex.Index,
                    ["is_optional"] = imp.bImportOptional
                });
            }
        }
        
        return new UAssetResponse { Success = true, Message = $"Retrieved {imports.Count} imports", Data = imports };
    }
    
    /// <summary>
    /// Get all exports with their details
    /// </summary>
    private static UAssetResponse GetExports(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        var exports = new List<Dictionary<string, object?>>();
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var exp = asset.Exports[i];
            string className = GetExportClassName(asset, exp);
            
            exports.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["object_name"] = exp.ObjectName?.ToString(),
                ["class_name"] = className,
                ["class_index"] = exp.ClassIndex.Index,
                ["super_index"] = exp.SuperIndex.Index,
                ["outer_index"] = exp.OuterIndex.Index,
                ["serial_size"] = exp.SerialSize,
                ["serial_offset"] = exp.SerialOffset,
                ["object_flags"] = exp.ObjectFlags.ToString(),
                ["export_type"] = exp.GetType().Name,
                ["property_count"] = (exp is NormalExport ne) ? ne.Data?.Count ?? 0 : 0
            });
        }
        
        return new UAssetResponse { Success = true, Message = $"Retrieved {exports.Count} exports", Data = exports };
    }
    
    /// <summary>
    /// Get properties of a specific export
    /// </summary>
    private static UAssetResponse GetExportProperties(string? filePath, string? usmapPath, int exportIndex)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (exportIndex < 0)
            return new UAssetResponse { Success = false, Message = "Export index is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        if (exportIndex >= asset.Exports.Count)
            return new UAssetResponse { Success = false, Message = $"Export index {exportIndex} out of range (max: {asset.Exports.Count - 1})" };
        
        var export = asset.Exports[exportIndex];
        var properties = new List<Dictionary<string, object?>>();
        
        if (export is NormalExport normalExport && normalExport.Data != null)
        {
            foreach (var prop in normalExport.Data)
            {
                properties.Add(SerializeProperty(prop, 0));
            }
        }
        
        return new UAssetResponse { Success = true, Message = $"Retrieved {properties.Count} properties", Data = properties };
    }
    
    /// <summary>
    /// Serialize a property to a dictionary for JSON output
    /// </summary>
    private static Dictionary<string, object?> SerializeProperty(PropertyData prop, int depth)
    {
        var result = new Dictionary<string, object?>
        {
            ["name"] = prop.Name?.ToString(),
            ["type"] = prop.PropertyType?.ToString(),
            ["array_index"] = prop.ArrayIndex
        };
        
        try
        {
            // Add value based on property type
            if (prop is IntPropertyData intProp)
                result["value"] = intProp.Value;
            else if (prop is FloatPropertyData floatProp)
                result["value"] = floatProp.Value;
            else if (prop is BoolPropertyData boolProp)
                result["value"] = boolProp.Value;
            else if (prop is StrPropertyData strProp)
                result["value"] = strProp.Value?.ToString();
            else if (prop is NamePropertyData nameProp)
                result["value"] = nameProp.Value?.ToString();
            else if (prop is ObjectPropertyData objProp)
                result["value"] = objProp.Value?.Index;
            else if (prop is SoftObjectPropertyData softObjProp)
                result["value"] = softObjProp.Value.AssetPath.ToString();
            else if (prop is EnumPropertyData enumProp)
                result["value"] = enumProp.Value?.ToString();
            else if (prop is BytePropertyData byteProp)
                result["value"] = byteProp.ByteType == BytePropertyType.Byte ? byteProp.Value : byteProp.EnumValue?.ToString();
            else if (prop is ArrayPropertyData arrayProp)
            {
                var items = new List<object?>();
                if (arrayProp.Value != null && depth < 3)
                {
                    foreach (var item in arrayProp.Value)
                    {
                        items.Add(SerializeProperty(item, depth + 1));
                    }
                }
                result["value"] = items;
                result["array_type"] = arrayProp.ArrayType?.ToString();
            }
            else if (prop is StructPropertyData structProp)
            {
                var structItems = new List<object?>();
                if (structProp.Value != null && depth < 3)
                {
                    foreach (var item in structProp.Value)
                    {
                        structItems.Add(SerializeProperty(item, depth + 1));
                    }
                }
                result["value"] = structItems;
                result["struct_type"] = structProp.StructType?.ToString();
            }
            else if (prop is MapPropertyData mapProp)
            {
                result["value"] = $"[Map with {mapProp.Value?.Count ?? 0} entries]";
                result["key_type"] = mapProp.KeyType?.ToString();
                result["value_type"] = mapProp.ValueType?.ToString();
            }
            else
            {
                try { result["value"] = prop.ToString(); }
                catch { result["value"] = $"[{prop.GetType().Name}]"; }
            }
        }
        catch (Exception)
        {
            result["value"] = $"[Error serializing {prop.GetType().Name}]";
        }
        
        return result;
    }
    
    /// <summary>
    /// Get raw binary data of an export (hex encoded)
    /// </summary>
    private static UAssetResponse GetExportRawData(string? filePath, string? usmapPath, int exportIndex)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (exportIndex < 0)
            return new UAssetResponse { Success = false, Message = "Export index is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        if (exportIndex >= asset.Exports.Count)
            return new UAssetResponse { Success = false, Message = $"Export index {exportIndex} out of range" };
        
        var export = asset.Exports[exportIndex];
        
        // Get the raw extras data if available
        byte[]? rawData = null;
        if (export is RawExport rawExport)
            rawData = rawExport.Data;
        else if (export is NormalExport normalExport)
            rawData = normalExport.Extras;
        
        var data = new Dictionary<string, object?>
        {
            ["export_index"] = exportIndex,
            ["serial_size"] = export.SerialSize,
            ["serial_offset"] = export.SerialOffset,
            ["has_raw_data"] = rawData != null && rawData.Length > 0,
            ["raw_data_size"] = rawData?.Length ?? 0,
            ["raw_data_hex"] = rawData != null ? Convert.ToBase64String(rawData) : null
        };
        
        return new UAssetResponse { Success = true, Message = "Raw data retrieved", Data = data };
    }
    
    /// <summary>
    /// Set a property value in an export
    /// </summary>
    private static UAssetResponse SetPropertyValue(string? filePath, string? usmapPath, int exportIndex, string? propertyPath, JsonElement? propertyValue)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (exportIndex < 0)
            return new UAssetResponse { Success = false, Message = "Export index is required" };
        if (string.IsNullOrEmpty(propertyPath))
            return new UAssetResponse { Success = false, Message = "Property path is required" };
        if (propertyValue == null)
            return new UAssetResponse { Success = false, Message = "Property value is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        if (exportIndex >= asset.Exports.Count)
            return new UAssetResponse { Success = false, Message = $"Export index {exportIndex} out of range" };
        
        var export = asset.Exports[exportIndex];
        if (export is not NormalExport normalExport || normalExport.Data == null)
            return new UAssetResponse { Success = false, Message = "Export has no editable properties" };
        
        // Find the property by path (supports nested paths like "Property.SubProperty")
        var pathParts = propertyPath.Split('.');
        PropertyData? targetProp = null;
        IList<PropertyData>? parentList = normalExport.Data;
        
        for (int i = 0; i < pathParts.Length; i++)
        {
            string partName = pathParts[i];
            targetProp = parentList?.FirstOrDefault(p => p.Name?.ToString() == partName);
            
            if (targetProp == null)
                return new UAssetResponse { Success = false, Message = $"Property not found: {partName}" };
            
            if (i < pathParts.Length - 1)
            {
                // Navigate into struct
                if (targetProp is StructPropertyData structProp)
                    parentList = structProp.Value;
                else
                    return new UAssetResponse { Success = false, Message = $"Cannot navigate into non-struct property: {partName}" };
            }
        }
        
        if (targetProp == null)
            return new UAssetResponse { Success = false, Message = "Property not found" };
        
        // Set the value based on property type
        try
        {
            if (targetProp is IntPropertyData intProp)
                intProp.Value = propertyValue.Value.GetInt32();
            else if (targetProp is FloatPropertyData floatProp)
                floatProp.Value = propertyValue.Value.GetSingle();
            else if (targetProp is BoolPropertyData boolProp)
                boolProp.Value = propertyValue.Value.GetBoolean();
            else if (targetProp is StrPropertyData strProp)
                strProp.Value = FString.FromString(propertyValue.Value.GetString() ?? "");
            else if (targetProp is NamePropertyData nameProp)
                nameProp.Value = FName.FromString(asset, propertyValue.Value.GetString() ?? "");
            else
                return new UAssetResponse { Success = false, Message = $"Unsupported property type for editing: {targetProp.PropertyType}" };
            
            // Save the asset
            asset.Write(filePath);
            
            return new UAssetResponse { Success = true, Message = $"Property '{propertyPath}' updated successfully" };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to set property value: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Add a new property to an export
    /// </summary>
    private static UAssetResponse AddProperty(string? filePath, string? usmapPath, int exportIndex, string? propertyName, string? propertyType, JsonElement? propertyValue)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (exportIndex < 0)
            return new UAssetResponse { Success = false, Message = "Export index is required" };
        if (string.IsNullOrEmpty(propertyName))
            return new UAssetResponse { Success = false, Message = "Property name is required" };
        if (string.IsNullOrEmpty(propertyType))
            return new UAssetResponse { Success = false, Message = "Property type is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        if (exportIndex >= asset.Exports.Count)
            return new UAssetResponse { Success = false, Message = $"Export index {exportIndex} out of range" };
        
        var export = asset.Exports[exportIndex];
        if (export is not NormalExport normalExport)
            return new UAssetResponse { Success = false, Message = "Export does not support properties" };
        
        normalExport.Data ??= new List<PropertyData>();
        
        // Create the property based on type
        PropertyData? newProp = propertyType.ToLower() switch
        {
            "int" or "intproperty" => new IntPropertyData(FName.FromString(asset, propertyName)) { Value = propertyValue?.GetInt32() ?? 0 },
            "float" or "floatproperty" => new FloatPropertyData(FName.FromString(asset, propertyName)) { Value = propertyValue?.GetSingle() ?? 0f },
            "bool" or "boolproperty" => new BoolPropertyData(FName.FromString(asset, propertyName)) { Value = propertyValue?.GetBoolean() ?? false },
            "str" or "strproperty" => new StrPropertyData(FName.FromString(asset, propertyName)) { Value = FString.FromString(propertyValue?.GetString() ?? "") },
            "name" or "nameproperty" => new NamePropertyData(FName.FromString(asset, propertyName)) { Value = FName.FromString(asset, propertyValue?.GetString() ?? "") },
            _ => null
        };
        
        if (newProp == null)
            return new UAssetResponse { Success = false, Message = $"Unsupported property type: {propertyType}" };
        
        normalExport.Data.Add(newProp);
        asset.Write(filePath);
        
        return new UAssetResponse { Success = true, Message = $"Property '{propertyName}' added successfully" };
    }
    
    /// <summary>
    /// Remove a property from an export
    /// </summary>
    private static UAssetResponse RemoveProperty(string? filePath, string? usmapPath, int exportIndex, string? propertyPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (exportIndex < 0)
            return new UAssetResponse { Success = false, Message = "Export index is required" };
        if (string.IsNullOrEmpty(propertyPath))
            return new UAssetResponse { Success = false, Message = "Property path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        if (exportIndex >= asset.Exports.Count)
            return new UAssetResponse { Success = false, Message = $"Export index {exportIndex} out of range" };
        
        var export = asset.Exports[exportIndex];
        if (export is not NormalExport normalExport || normalExport.Data == null)
            return new UAssetResponse { Success = false, Message = "Export has no properties" };
        
        var prop = normalExport.Data.FirstOrDefault(p => p.Name?.ToString() == propertyPath);
        if (prop == null)
            return new UAssetResponse { Success = false, Message = $"Property not found: {propertyPath}" };
        
        normalExport.Data.Remove(prop);
        asset.Write(filePath);
        
        return new UAssetResponse { Success = true, Message = $"Property '{propertyPath}' removed successfully" };
    }
    
    /// <summary>
    /// Save the asset to a new path
    /// </summary>
    private static UAssetResponse SaveAsset(string? filePath, string? usmapPath, string? outputPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        string savePath = outputPath ?? filePath;
        asset.Write(savePath);
        
        return new UAssetResponse { Success = true, Message = $"Asset saved to {savePath}" };
    }
    
    /// <summary>
    /// Export the asset to JSON format
    /// </summary>
    private static UAssetResponse ExportToJson(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        string jsonPath = Path.ChangeExtension(filePath, ".json");
        string json = asset.SerializeJson();
        File.WriteAllText(jsonPath, json);
        
        return new UAssetResponse { Success = true, Message = $"Asset exported to {jsonPath}", Data = new { path = jsonPath } };
    }
    
    /// <summary>
    /// Import asset data from JSON
    /// </summary>
    private static UAssetResponse ImportFromJson(string? filePath, string? usmapPath, string? jsonData)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (string.IsNullOrEmpty(jsonData))
            return new UAssetResponse { Success = false, Message = "JSON data is required" };
        
        try
        {
            // Load mappings if provided
            Usmap? mappings = null;
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
                mappings = new Usmap(usmapPath);
            
            // Deserialize from JSON
            var asset = UAsset.DeserializeJson(jsonData);
            if (asset == null)
                return new UAssetResponse { Success = false, Message = "Failed to deserialize JSON" };
            
            asset.Mappings = mappings;
            asset.FilePath = Path.GetFullPath(filePath);
            PreloadReferencedAssetsForSchemas(asset);
            
            string? outputDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            
            asset.Write(filePath);
            
            return new UAssetResponse { Success = true, Message = $"Asset imported from JSON and saved to {filePath}" };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to import from JSON: {ex.Message}" };
        }
    }
    
    #endregion

    #region Unified Asset Detection
    
    /// <summary>
    /// Core asset type detection - single unified method for all asset types.
    /// Returns proper UE class names: "Texture2D", "StaticMesh", "SkeletalMesh",
    /// "MaterialInstanceConstant", "MaterialInstance", "WidgetBlueprint", "AnimBlueprint", etc.
    /// Returns "Unknown" if no recognizable export class is found.
    /// </summary>
    private static string DetectAssetType(UAsset asset)
    {
        foreach (var export in asset.Exports)
        {
            string className = GetExportClassName(asset, export);
            if (className == "Unknown") continue;
            
            // Return the actual UE class name for known types
            if (className.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("TextureCube", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("VolumeTexture", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("TextureRenderTarget2D", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("MaterialInstance", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Material", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("AnimSequence", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("AnimMontage", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("AnimComposite", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("BlendSpace", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("BlendSpace1D", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Skeleton", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("PhysicsAsset", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("SoundWave", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("SoundCue", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("DataTable", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("CurveTable", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("StringTable", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("NiagaraSystem", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("NiagaraEmitter", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Font", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("FontFace", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("UserDefinedStruct", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("UserDefinedEnum", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("MapBuildDataRegistry", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("World", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Level", StringComparison.OrdinalIgnoreCase))
                return className;
            
            // Blueprint types - return the actual class name
            if (className.Contains("Blueprint", StringComparison.OrdinalIgnoreCase))
                return className;
            
            // Return as-is for any other recognized class from imports
            return className;
        }
        
        // Fallback: check C# export type name
        foreach (var export in asset.Exports)
        {
            string exportTypeName = export.GetType().Name;
            if (exportTypeName.Contains("Texture", StringComparison.OrdinalIgnoreCase))
                return "Texture2D";
            if (exportTypeName.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase))
                return "StaticMesh";
            if (exportTypeName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                return "SkeletalMesh";
        }
        
        return "Unknown";
    }
    
    /// <summary>
    /// Get the class name for an export (from import reference)
    /// </summary>
    private static string GetExportClassName(UAsset asset, Export export)
    {
        if (export.ClassIndex.IsImport())
        {
            var import = export.ClassIndex.ToImport(asset);
            if (import != null)
            {
                return import.ObjectName?.Value?.Value ?? "Unknown";
            }
        }
        return "Unknown";
    }
    
    /// <summary>
    /// Check if asset matches a specific type.
    /// Accepts both UE class names (e.g. "Texture2D") and legacy target strings (e.g. "texture").
    /// </summary>
    private static bool IsAssetType(UAsset asset, string targetType)
    {
        string detectedType = DetectAssetType(asset);
        
        // Direct match (UE class name or exact)
        if (detectedType.Equals(targetType, StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Map legacy target strings to UE class names
        return targetType.ToLowerInvariant() switch
        {
            "texture" => detectedType.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
                         detectedType.Equals("TextureCube", StringComparison.OrdinalIgnoreCase) ||
                         detectedType.Equals("VolumeTexture", StringComparison.OrdinalIgnoreCase) ||
                         detectedType.Equals("TextureRenderTarget2D", StringComparison.OrdinalIgnoreCase),
            "skeletal_mesh" => detectedType.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase),
            "static_mesh" => detectedType.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase),
            "material_instance" => detectedType.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
                                   detectedType.Equals("MaterialInstance", StringComparison.OrdinalIgnoreCase),
            "material" => detectedType.Equals("Material", StringComparison.OrdinalIgnoreCase),
            "blueprint" => detectedType.Contains("Blueprint", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
    
    /// <summary>
    /// Detect single asset and check if it matches target type
    /// </summary>
    private static UAssetResponse DetectSingleAsset(string? filePath, string? usmapPath, string targetType)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            var asset = LoadAsset(filePath, effectiveUsmapPath);
            
            // For textures, also check if it needs MipGen fix
            if (targetType == "texture")
            {
                bool isTexture = IsAssetType(asset, "texture");
                bool needsFix = isTexture && IsTextureNeedingMipGenFix(asset);
                return new UAssetResponse
                {
                    Success = true,
                    Message = needsFix ? "Texture needs MipGen fix" : (isTexture ? "Texture already has NoMipmaps" : "Not a texture"),
                    Data = needsFix
                };
            }
            
            bool isMatch = IsAssetType(asset, targetType);
            return new UAssetResponse
            {
                Success = true,
                Message = isMatch ? $"File is {targetType}" : $"File is not {targetType}",
                Data = isMatch
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Batch detect - check multiple files for a specific asset type
    /// </summary>
    private static UAssetResponse BatchDetectAssetType(List<string>? filePaths, string? usmapPath, string targetType)
    {
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "file_paths required" };

        try
        {
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Usmap? mappings = LoadMappings(effectiveUsmapPath);

            bool foundMatch = filePaths.AsParallel().Any(filePath =>
            {
                if (!File.Exists(filePath)) return false;
                try
                {
                    var asset = LoadAssetWithMappings(filePath, mappings);
                    
                    // For textures, check if it needs MipGen fix
                    if (targetType == "texture")
                    {
                        return IsAssetType(asset, "texture") && IsTextureNeedingMipGenFix(asset);
                    }
                    
                    return IsAssetType(asset, targetType);
                }
                catch
                {
                    return false;
                }
            });

            return new UAssetResponse
            {
                Success = true,
                Message = foundMatch ? $"Found {targetType} in batch" : $"No {targetType} found",
                Data = foundMatch
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Batch detection error: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Unified type detection for JSON API.
    /// If file_path is provided: detect single file → returns { asset_type, export_count, import_count }
    /// If file_paths is provided: detect batch → returns array of { path, asset_type, file_name }
    /// Both can be provided simultaneously.
    /// Returns proper UE class names (Texture2D, StaticMesh, SkeletalMesh, MaterialInstanceConstant, etc.)
    /// </summary>
    private static UAssetResponse DetectTypeUnified(string? filePath, List<string>? filePaths, string? usmapPath)
    {
        string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
        
        bool hasSingle = !string.IsNullOrEmpty(filePath);
        bool hasBatch = filePaths != null && filePaths.Count > 0;
        
        if (!hasSingle && !hasBatch)
            return new UAssetResponse { Success = false, Message = "file_path or file_paths required" };
        
        try
        {
            var data = new Dictionary<string, object?>();
            
            // Single file detection
            if (hasSingle)
            {
                if (!File.Exists(filePath))
                    return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };
                
                var asset = LoadAsset(filePath!, effectiveUsmapPath);
                string assetType = DetectAssetType(asset);
                
                data["asset_type"] = assetType;
                data["export_count"] = asset.Exports.Count;
                data["import_count"] = asset.Imports.Count;
                data["file_path"] = filePath;
            }
            
            // Batch detection
            if (hasBatch)
            {
                Usmap? mappings = LoadMappings(effectiveUsmapPath);
                var results = new List<Dictionary<string, object?>>();
                
                foreach (var path in filePaths!)
                {
                    try
                    {
                        if (!File.Exists(path))
                        {
                            results.Add(new Dictionary<string, object?>
                            {
                                ["path"] = path,
                                ["asset_type"] = null,
                                ["error"] = "File not found"
                            });
                            continue;
                        }
                        
                        var asset = LoadAssetWithMappings(path, mappings);
                        string assetType = DetectAssetType(asset);
                        
                        results.Add(new Dictionary<string, object?>
                        {
                            ["path"] = path,
                            ["file_name"] = Path.GetFileName(path),
                            ["asset_type"] = assetType
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new Dictionary<string, object?>
                        {
                            ["path"] = path,
                            ["asset_type"] = null,
                            ["error"] = ex.Message
                        });
                    }
                }
                
                data["results"] = results;
                data["total_files"] = filePaths.Count;
            }
            
            return new UAssetResponse
            {
                Success = true,
                Message = hasBatch ? $"Detected types for {filePaths!.Count} files" : $"Detected type: {data["asset_type"]}",
                Data = data
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Detection error: {ex.Message}" };
        }
    }
    
    #endregion

    #region Texture Operations
    
    private static bool IsTextureNeedingMipGenFix(UAsset asset)
    {
        foreach (var export in asset.Exports)
        {
            if (GetExportClassName(asset, export) == "Texture2D" && export is NormalExport normalExport)
            {
                foreach (var property in normalExport.Data)
                {
                    if (property.Name?.Value?.Value == "MipGenSettings")
                    {
                        if (property is EnumPropertyData enumProp)
                        {
                            string value = enumProp.Value?.Value?.Value ?? "";
                            return !value.Equals("TMGS_NoMipmaps", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (property is BytePropertyData byteProp)
                        {
                            return byteProp.Value != 13; // 13 = TMGS_NoMipmaps
                        }
                    }
                }
                // MipGenSettings not found = using default (FromTextureGroup) = needs fix
                return true;
            }
        }
        return false;
    }
    
    /// <summary>
    /// Check if a texture has inline data (no .ubulk needed).
    /// Returns true if texture data is stored inline in the .uexp file.
    /// </summary>
    private static bool CheckTextureHasInlineData(UAsset asset)
    {
        foreach (var export in asset.Exports)
        {
            if (GetExportClassName(asset, export) == "Texture2D" && export is TextureExport textureExport)
            {
                // Check if PlatformData exists and has mips
                if (textureExport.PlatformData?.Mips != null && textureExport.PlatformData.Mips.Count > 0)
                {
                    var mip = textureExport.PlatformData.Mips[0];
                    
                    // Check if the mip has inline data (ForceInlinePayload flag or DataResourceIndex >= 0)
                    if (mip.BulkData?.Header != null)
                    {
                        var header = mip.BulkData.Header;
                        
                        // ForceInlinePayload = 0x40, SingleUse = 0x08
                        // Inline textures typically have flags 0x48 (ForceInlinePayload | SingleUse)
                        bool hasInlineFlag = ((int)header.BulkDataFlags & 0x40) != 0;
                        
                        // Also check if DataResourceIndex is valid (UE5.3+ inline data)
                        bool hasDataResource = header.DataResourceIndex >= 0;
                        
                        // If either condition is true, data is inline
                        if (hasInlineFlag || hasDataResource)
                        {
                            return true;
                        }
                    }
                    
                    // Also check if mip has actual pixel data stored
                    if (mip.BulkData?.Data != null && mip.BulkData.Data.Length > 0)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
    
    private static UAssetResponse HasInlineTextureData(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            var asset = LoadAsset(filePath, usmapPath);
            
            if (!IsAssetType(asset, "texture"))
                return new UAssetResponse { Success = true, Message = "Not a texture", Data = false };
            
            bool hasInline = CheckTextureHasInlineData(asset);
            return new UAssetResponse 
            { 
                Success = true, 
                Message = hasInline ? "Texture has inline data" : "Texture uses external bulk data",
                Data = hasInline 
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    private static UAssetResponse BatchHasInlineTextureData(List<string>? filePaths, string? usmapPath)
    {
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "file_paths required" };

        try
        {
            Usmap? mappings = LoadMappings(usmapPath);
            
            // Return list of files that have inline texture data
            var inlineFiles = filePaths.AsParallel()
                .Where(filePath =>
                {
                    if (!File.Exists(filePath)) return false;
                    try
                    {
                        var asset = LoadAssetWithMappings(filePath, mappings);
                        return IsAssetType(asset, "texture") && CheckTextureHasInlineData(asset);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToList();

            return new UAssetResponse
            {
                Success = true,
                Message = $"Found {inlineFiles.Count} textures with inline data",
                Data = inlineFiles
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Batch detection error: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Strip mipmaps using native UAssetAPI TextureExport.
    /// This is a pure C# implementation based on CUE4Parse's texture parsing.
    /// </summary>
    private static UAssetResponse StripMipmapsNative(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            Console.Error.WriteLine($"[UAssetTool] Native mipmap stripping: {filePath}");
            
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Console.Error.WriteLine($"[UAssetTool] Using USMAP: {effectiveUsmapPath ?? "null"}");
            var asset = LoadAsset(filePath, effectiveUsmapPath);
            
            // Debug: Log export types
            Console.Error.WriteLine($"[UAssetTool] Asset has {asset.Exports.Count} exports");
            foreach (var exp in asset.Exports)
            {
                var className = GetExportClassName(asset, exp);
                Console.Error.WriteLine($"[UAssetTool] Export type: {exp.GetType().Name}, Class: {className}");
            }
            
            // Find TextureExport
            TextureExport? textureExport = null;
            foreach (var export in asset.Exports)
            {
                if (export is TextureExport tex)
                {
                    textureExport = tex;
                    break;
                }
            }
            
            if (textureExport == null)
            {
                return new UAssetResponse { Success = false, Message = "No TextureExport found in asset" };
            }
            
            if (textureExport.PlatformData == null)
            {
                return new UAssetResponse { Success = false, Message = "TextureExport has no PlatformData (texture data not parsed)" };
            }
            
            int originalMipCount = textureExport.MipCount;
            Console.Error.WriteLine($"[UAssetTool] Original mip count: {originalMipCount}");
            
            if (originalMipCount <= 1)
            {
                return new UAssetResponse { Success = true, Message = "Texture already has 1 or fewer mipmaps" };
            }
            
            // Strip mipmaps using UAssetAPI
            bool stripped = textureExport.StripMipmaps();
            if (!stripped)
            {
                return new UAssetResponse { Success = false, Message = "Failed to strip mipmaps" };
            }
            
            Console.Error.WriteLine($"[UAssetTool] Stripped to {textureExport.MipCount} mipmap(s)");
            
            // Update DataResources for inline mip data
            if (asset.DataResources != null && textureExport.PlatformData?.Mips?.Count > 0)
            {
                var mip = textureExport.PlatformData.Mips[0];
                int dataSize = mip.BulkData?.Data?.Length ?? 0;
                
                // Calculate the SerialOffset where pixel data will be in the .uexp
                // Structure: Properties + LightingGuid(16) + StripFlags(4) + bCooked(4) + bSerializeMipData(4)
                //          + PixelFormatFName(8) + ExtraBytes(0-4) + SkipOffset(8)
                //          + Placeholder(16) + SizeX(4) + SizeY(4) + PackedData(4) + PixelFormat FString(4+len)
                //          + FirstMipToSerialize(4) + MipCount(4) + MipHeader(4+12) + bIsVirtual(4)
                // 
                // For Marvel Rivals textures with 1 mip:
                // Properties header: 10 bytes (unversioned header)
                // LightingGuid: 16 bytes
                // StripFlags: 4 bytes  
                // bCooked: 4 bytes
                // bSerializeMipData: 4 bytes
                // PixelFormatFName: 8 bytes
                // ExtraBytes: 4 bytes (Marvel Rivals specific)
                // SkipOffset: 8 bytes
                // Placeholder: 16 bytes
                // SizeX: 4 bytes
                // SizeY: 4 bytes
                // PackedData: 4 bytes
                // PixelFormat FString: 4 + 8 = 12 bytes (for "PF_DXT1\0")
                // FirstMipToSerialize: 4 bytes
                // MipCount: 4 bytes
                // Mip0 header: 4 (data_resource_id) + 4 (SizeX) + 4 (SizeY) + 4 (SizeZ) = 16 bytes
                // bIsVirtual: 4 bytes
                // Total: 10 + 16 + 4 + 4 + 4 + 8 + 4 + 8 + 16 + 4 + 4 + 4 + 12 + 4 + 4 + 16 + 4 = 126 bytes
                
                // Calculate based on pixel format string length
                string pixelFormat = textureExport.PlatformData.PixelFormat ?? "PF_DXT1";
                int pixelFormatLen = pixelFormat.Length + 1; // +1 for null terminator
                
                // Base offset calculation
                int serialOffset = 10  // Properties header (unversioned)
                    + 16  // LightingGuid
                    + 4   // StripFlags
                    + 4   // bCooked
                    + 4   // bSerializeMipData
                    + 8   // PixelFormatFName
                    + (textureExport.ExtraBytes?.Length ?? 0)  // ExtraBytes (Marvel Rivals specific)
                    + 8   // SkipOffset
                    + 16  // Placeholder
                    + 4   // SizeX
                    + 4   // SizeY
                    + 4   // PackedData
                    + 4 + pixelFormatLen  // PixelFormat FString (length + chars)
                    + 4   // FirstMipToSerialize
                    + 4   // MipCount
                    + 16  // Mip0 header (data_resource_id + SizeX + SizeY + SizeZ)
                    + 4;  // bIsVirtual
                
                // Create a new DataResource entry for the inline mip
                var inlineResource = new UAssetAPI.UnrealTypes.FObjectDataResource(
                    (UAssetAPI.UnrealTypes.EObjectDataResourceFlags)0,
                    serialOffset,  // SerialOffset - where pixel data starts in .uexp
                    -1, // DuplicateSerialOffset
                    dataSize, // SerialSize
                    dataSize, // RawSize
                    new UAssetAPI.UnrealTypes.FPackageIndex(1), // OuterIndex
                    0x48 // LegacyBulkDataFlags - ForceInlinePayload | SingleUse
                );
                
                // Clear and add only 1 entry
                asset.DataResources.Clear();
                asset.DataResources.Add(inlineResource);
                
                // Set the mip's DataResourceIndex to 0 (first entry)
                if (mip.BulkData?.Header != null)
                    mip.BulkData.Header.DataResourceIndex = 0;
            }
            
            // Save the modified asset
            asset.Write(filePath);
            
            // Delete .ubulk file if it exists (data is now inline)
            string ubulkPath = Path.ChangeExtension(filePath, ".ubulk");
            if (File.Exists(ubulkPath))
            {
                Console.Error.WriteLine($"[UAssetTool] Deleting .ubulk: {ubulkPath}");
                File.Delete(ubulkPath);
            }
            
            return new UAssetResponse 
            { 
                Success = true, 
                Message = $"Stripped mipmaps: {originalMipCount} -> {textureExport.MipCount}",
                Data = JsonSerializer.SerializeToElement(new { original_mips = originalMipCount, new_mips = textureExport.MipCount })
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UAssetTool] Native strip error: {ex.Message}");
            Console.Error.WriteLine($"[UAssetTool] Stack: {ex.StackTrace}");
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Batch strip mipmaps from multiple textures using native UAssetAPI TextureExport.
    /// This processes all files in a single call for better performance.
    /// When parallel=true, uses Parallel.ForEach for concurrent processing.
    /// </summary>
    private static UAssetResponse BatchStripMipmapsNative(List<string>? filePaths, string? usmapPath, bool parallel = false)
    {
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "file_paths required" };

        try
        {
            Console.Error.WriteLine($"[UAssetTool] Batch stripping mipmaps for {filePaths.Count} files (parallel={parallel})");
            
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Console.Error.WriteLine($"[UAssetTool] Using USMAP: {effectiveUsmapPath ?? "null"}");
            Usmap? mappings = LoadMappings(effectiveUsmapPath);
            
            // Use thread-safe collections for parallel processing
            var results = new System.Collections.Concurrent.ConcurrentBag<object>();
            int successCount = 0;
            int skipCount = 0;
            int errorCount = 0;
            
            // Process single file - returns (success, skip, error) counts
            (int success, int skip, int error) ProcessSingleTexture(string filePath)
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    results.Add(new { path = filePath, success = false, message = "File not found" });
                    return (0, 0, 1);
                }
                
                try
                {
                    var asset = LoadAssetWithMappings(filePath, mappings);
                    
                    // Find TextureExport
                    TextureExport? textureExport = null;
                    foreach (var export in asset.Exports)
                    {
                        if (export is TextureExport tex)
                        {
                            textureExport = tex;
                            break;
                        }
                    }
                    
                    if (textureExport == null)
                    {
                        results.Add(new { path = filePath, success = false, message = "No TextureExport found" });
                        return (0, 0, 1);
                    }
                    
                    if (textureExport.PlatformData == null)
                    {
                        results.Add(new { path = filePath, success = false, message = "No PlatformData (texture not parsed)" });
                        return (0, 0, 1);
                    }
                    
                    int originalMipCount = textureExport.MipCount;
                    
                    if (originalMipCount <= 1)
                    {
                        results.Add(new { path = filePath, success = true, message = "Already has 1 mipmap", skipped = true });
                        return (0, 1, 0);
                    }
                    
                    // Target data_resource_id = 5 for Marvel Rivals textures
                    int targetDataResourceId = 5;
                    
                    // Strip mipmaps
                    bool stripped = textureExport.StripMipmaps();
                    if (!stripped)
                    {
                        results.Add(new { path = filePath, success = false, message = "Failed to strip mipmaps" });
                        return (0, 0, 1);
                    }
                    
                    // Update DataResources
                    if (asset.DataResources != null && textureExport.PlatformData?.Mips?.Count > 0)
                    {
                        var mip = textureExport.PlatformData.Mips[0];
                        int dataSize = mip.BulkData?.Data?.Length ?? 0;
                        
                        var inlineResource = new UAssetAPI.UnrealTypes.FObjectDataResource(
                            (UAssetAPI.UnrealTypes.EObjectDataResourceFlags)0,
                            0,
                            -1,
                            dataSize,
                            dataSize,
                            new UAssetAPI.UnrealTypes.FPackageIndex(1),
                            0x48
                        );
                        
                        asset.DataResources.Clear();
                        asset.DataResources.Add(inlineResource);
                        if (mip.BulkData?.Header != null)
                            mip.BulkData.Header.DataResourceIndex = targetDataResourceId;
                    }
                    
                    // Save the modified asset (first pass)
                    asset.Write(filePath);
                    
                    // Second pass: Find inline data offset and update DataResource
                    string uexpPath = Path.ChangeExtension(filePath, ".uexp");
                    if (File.Exists(uexpPath) && asset.DataResources != null && asset.DataResources.Count > 0)
                    {
                        var mip = textureExport.PlatformData?.Mips?[0];
                        if (mip?.BulkData?.Data != null && mip.BulkData.Data.Length >= 4)
                        {
                            int drIndex = mip.BulkData.Header.DataResourceIndex;
                            if (drIndex < 0 || drIndex >= asset.DataResources.Count)
                                drIndex = asset.DataResources.Count - 1;
                            
                            byte[] uexpData = File.ReadAllBytes(uexpPath);
                            byte[] searchPattern = new byte[4];
                            Array.Copy(mip.BulkData.Data, 0, searchPattern, 0, 4);
                            
                            long inlineOffset = -1;
                            for (int i = 0; i < uexpData.Length - 4; i++)
                            {
                                if (uexpData[i] == searchPattern[0] && 
                                    uexpData[i+1] == searchPattern[1] &&
                                    uexpData[i+2] == searchPattern[2] &&
                                    uexpData[i+3] == searchPattern[3])
                                {
                                    inlineOffset = i;
                                    break;
                                }
                            }
                            
                            if (inlineOffset >= 0)
                            {
                                var dr = asset.DataResources[drIndex];
                                asset.DataResources[drIndex] = new UAssetAPI.UnrealTypes.FObjectDataResource(
                                    dr.Flags,
                                    inlineOffset,
                                    dr.DuplicateSerialOffset,
                                    dr.SerialSize,
                                    dr.RawSize,
                                    dr.OuterIndex,
                                    dr.LegacyBulkDataFlags,
                                    dr.CookedIndex
                                );
                                asset.Write(filePath);
                            }
                        }
                    }
                    
                    // Patch data_resource_id in .uexp
                    if (File.Exists(uexpPath))
                    {
                        byte[] uexpBytes = File.ReadAllBytes(uexpPath);
                        int targetValue = targetDataResourceId;
                        
                        if (targetValue > 0 && targetValue < 100)
                        {
                            int firstPos = -1;
                            int secondPos = -1;
                            
                            for (int i = 100; i < Math.Min(uexpBytes.Length - 8, 300); i++)
                            {
                                int val1 = BitConverter.ToInt32(uexpBytes, i);
                                int val2 = BitConverter.ToInt32(uexpBytes, i + 4);
                                
                                if (val1 == 1 && val2 == 0 && firstPos == -1)
                                    firstPos = i + 4;
                                else if (val1 == 1 && val2 == targetValue && secondPos == -1 && firstPos >= 0)
                                    secondPos = i + 4;
                            }
                            
                            if (firstPos >= 0 && secondPos >= 0 && (secondPos - firstPos) == 64)
                            {
                                byte[] targetBytes = BitConverter.GetBytes(targetValue);
                                byte[] zeroBytes = BitConverter.GetBytes(0);
                                
                                Array.Copy(targetBytes, 0, uexpBytes, firstPos, 4);
                                Array.Copy(zeroBytes, 0, uexpBytes, secondPos, 4);
                                
                                File.WriteAllBytes(uexpPath, uexpBytes);
                            }
                        }
                    }
                    
                    // Delete .ubulk file
                    string ubulkPath = Path.ChangeExtension(filePath, ".ubulk");
                    if (File.Exists(ubulkPath))
                    {
                        File.Delete(ubulkPath);
                    }
                    
                    results.Add(new { 
                        path = filePath, 
                        success = true, 
                        message = $"Stripped {originalMipCount} -> {textureExport.MipCount}",
                        original_mips = originalMipCount,
                        new_mips = textureExport.MipCount
                    });
                    
                    Console.Error.WriteLine($"[UAssetTool] Stripped: {Path.GetFileName(filePath)} ({originalMipCount} -> {textureExport.MipCount})");
                    return (1, 0, 0);
                }
                catch (Exception ex)
                {
                    results.Add(new { path = filePath, success = false, message = ex.Message });
                    Console.Error.WriteLine($"[UAssetTool] Error processing {Path.GetFileName(filePath)}: {ex.Message}");
                    return (0, 0, 1);
                }
            }
            
            // Process files either in parallel or sequentially
            if (parallel && filePaths.Count > 1)
            {
                Console.Error.WriteLine($"[UAssetTool] Using parallel processing with {Environment.ProcessorCount} cores");
                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
                
                Parallel.ForEach(filePaths, parallelOptions, filePath =>
                {
                    var (s, sk, e) = ProcessSingleTexture(filePath);
                    Interlocked.Add(ref successCount, s);
                    Interlocked.Add(ref skipCount, sk);
                    Interlocked.Add(ref errorCount, e);
                });
            }
            else
            {
                // Sequential processing
                foreach (var filePath in filePaths)
                {
                    var (s, sk, e) = ProcessSingleTexture(filePath);
                    successCount += s;
                    skipCount += sk;
                    errorCount += e;
                }
            }
            
            Console.Error.WriteLine($"[UAssetTool] Batch complete: {successCount} stripped, {skipCount} skipped, {errorCount} errors");
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Batch processed {filePaths.Count} files: {successCount} stripped, {skipCount} skipped, {errorCount} errors",
                Data = new { 
                    total = filePaths.Count,
                    success_count = successCount,
                    skip_count = skipCount,
                    error_count = errorCount,
                    results = results.ToList()
                }
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UAssetTool] Batch strip error: {ex.Message}");
            return new UAssetResponse { Success = false, Message = $"Batch error: {ex.Message}" };
        }
    }
    
    private static UAssetResponse GetTextureInfo(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Console.Error.WriteLine($"[UAssetTool] USMAP path: {effectiveUsmapPath ?? "null"}");
            
            if (!string.IsNullOrEmpty(effectiveUsmapPath) && File.Exists(effectiveUsmapPath))
            {
                Console.Error.WriteLine($"[UAssetTool] USMAP file found");
            }
            else
            {
                Console.Error.WriteLine($"[UAssetTool] USMAP file not found or path is null");
            }
            
            var asset = LoadAsset(filePath, effectiveUsmapPath);
            asset.UseSeparateBulkDataFiles = true;
            
            Console.Error.WriteLine($"[UAssetTool] Mappings loaded: {asset.Mappings != null}");
            
            var info = ExtractTextureInfo(asset);
            return new UAssetResponse { Success = true, Message = "Texture info retrieved", Data = info };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UAssetTool] Error in GetTextureInfo: {ex.Message}");
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    private static Dictionary<string, object> ExtractTextureInfo(UAsset asset)
    {
        // Use snake_case for consistency with other responses
        var info = new Dictionary<string, object>
        {
            ["is_texture"] = false,
            ["format"] = "Unknown",
            ["pixel_format"] = "Unknown",
            ["width"] = 0,
            ["height"] = 0,
            ["mip_count"] = 0,
            ["mip_gen_settings"] = "Unknown",
            ["compression_settings"] = "Unknown",
            ["has_inline_data"] = false,
            ["size_bytes"] = 0L
        };
        
        foreach (var export in asset.Exports)
        {
            string className = GetExportClassName(asset, export);
            if (className == "Texture2D" || className == "TextureCube" || className == "VolumeTexture")
            {
                info["is_texture"] = true;
                
                // Try to get detailed info from TextureExport
                if (export is TextureExport textureExport && textureExport.PlatformData != null)
                {
                    var platformData = textureExport.PlatformData;
                    info["pixel_format"] = platformData.PixelFormat ?? "Unknown";
                    info["format"] = $"PF_{platformData.PixelFormat ?? "Unknown"}";
                    info["width"] = platformData.SizeX;
                    info["height"] = platformData.SizeY;
                    info["mip_count"] = platformData.Mips?.Count ?? 0;
                    info["has_inline_data"] = !textureExport.HasExternalBulkData;
                    info["size_bytes"] = platformData.GetTotalMipDataSize();
                }
                
                // Extract properties from NormalExport
                if (export is NormalExport normalExport && normalExport.Data != null)
                {
                    var properties = new List<Dictionary<string, string>>();
                    foreach (var prop in normalExport.Data)
                    {
                        var propInfo = new Dictionary<string, string>
                        {
                            ["name"] = prop.Name?.Value?.Value ?? "Unknown",
                            ["type"] = prop.GetType().Name
                        };
                        
                        if (prop is EnumPropertyData enumProp)
                            propInfo["value"] = enumProp.Value?.Value?.Value ?? "null";
                        else if (prop is BytePropertyData byteProp)
                            propInfo["value"] = byteProp.Value.ToString();
                        else if (prop is IntPropertyData intProp)
                            propInfo["value"] = intProp.Value.ToString();
                        else if (prop is BoolPropertyData boolProp)
                            propInfo["value"] = boolProp.Value.ToString();
                        else
                            propInfo["value"] = "(complex)";
                        
                        properties.Add(propInfo);
                        
                        // Extract specific texture settings
                        string propName = prop.Name?.Value?.Value ?? "";
                        if (propName == "MipGenSettings")
                        {
                            if (prop is EnumPropertyData ep)
                                info["mip_gen_settings"] = ep.Value?.Value?.Value ?? "Unknown";
                            else if (prop is BytePropertyData bp)
                                info["mip_gen_settings"] = $"ByteValue_{bp.Value}";
                        }
                        else if (propName == "CompressionSettings")
                        {
                            if (prop is EnumPropertyData ep)
                                info["compression_settings"] = ep.Value?.Value?.Value ?? "Unknown";
                        }
                    }
                    info["properties"] = properties;
                }
                break;
            }
        }
        
        return info;
    }
    
    #endregion

    #region Mesh Operations
    
    private static UAssetResponse PatchMesh(string? filePath, string? uexpPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (string.IsNullOrEmpty(uexpPath))
            return new UAssetResponse { Success = false, Message = "UEXP path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };
        if (!File.Exists(uexpPath))
            return new UAssetResponse { Success = false, Message = $"UEXP not found: {uexpPath}" };

        try
        {
            File.Copy(filePath, filePath + ".backup", true);
            File.Copy(uexpPath, uexpPath + ".backup", true);
            
            // TODO: Implement actual mesh patching
            return new UAssetResponse { Success = true, Message = "Mesh patch placeholder (backups created)" };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    private static UAssetResponse GetMeshInfo(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Console.Error.WriteLine($"[UAssetTool] GetMeshInfo USMAP path: {effectiveUsmapPath ?? "null"}");
            
            var asset = LoadAsset(filePath, effectiveUsmapPath);
            asset.UseSeparateBulkDataFiles = true;
            
            // Use snake_case for consistency with other responses
            var info = new Dictionary<string, object>
            {
                ["mesh_type"] = "Unknown",
                ["vertex_count"] = 0,
                ["triangle_count"] = 0,
                ["lod_count"] = 0,
                ["material_slots"] = 0,
                ["bone_count"] = 0,
                ["has_morph_targets"] = false,
                ["has_vertex_colors"] = false
            };
            
            foreach (var export in asset.Exports)
            {
                string className = GetExportClassName(asset, export);
                
                if (className == "SkeletalMesh")
                {
                    info["mesh_type"] = "SkeletalMesh";
                    // Extract skeletal mesh properties if available
                    if (export is NormalExport normalExport && normalExport.Data != null)
                    {
                        foreach (var prop in normalExport.Data)
                        {
                            string propName = prop.Name?.Value?.Value ?? "";
                            if (propName == "Materials" && prop is ArrayPropertyData arrayProp)
                            {
                                info["material_slots"] = arrayProp.Value?.Length ?? 0;
                            }
                        }
                    }
                    break;
                }
                else if (className == "StaticMesh")
                {
                    info["mesh_type"] = "StaticMesh";
                    // Extract static mesh properties if available
                    if (export is NormalExport normalExport && normalExport.Data != null)
                    {
                        foreach (var prop in normalExport.Data)
                        {
                            string propName = prop.Name?.Value?.Value ?? "";
                            if (propName == "StaticMaterials" && prop is ArrayPropertyData arrayProp)
                            {
                                info["material_slots"] = arrayProp.Value?.Length ?? 0;
                            }
                        }
                    }
                    break;
                }
            }
            
            return new UAssetResponse { Success = true, Message = "Mesh info retrieved", Data = info };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UAssetTool] Error in GetMeshInfo: {ex.Message}");
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    private static UAssetResponse FixSerializeSizeJson(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        var result = FixSerializeSize(filePath, usmapPath);
        return new UAssetResponse
        {
            Success = (bool)(result.GetType().GetProperty("success")?.GetValue(result) ?? false),
            Message = (string)(result.GetType().GetProperty("message")?.GetValue(result) ?? ""),
            Data = result
        };
    }
    
    private static object FixSerializeSize(string uassetPath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(usmapPath) || !File.Exists(usmapPath))
        {
            return new { success = false, message = "USmap file required for SerializeSize fix", fixed_count = 0 };
        }

        var asset = LoadAsset(uassetPath, usmapPath);
        asset.UseSeparateBulkDataFiles = true;
        
        string uexpPath = Path.ChangeExtension(uassetPath, ".uexp");
        if (!File.Exists(uexpPath))
        {
            return new { success = false, message = "No .uexp file found", fixed_count = 0 };
        }

        long uexpSize = new FileInfo(uexpPath).Length;
        long headerSize = asset.Exports.Min(e => e.SerialOffset);
        var sortedExports = asset.Exports.OrderBy(e => e.SerialOffset).ToList();
        
        // Check for .ubulk file - if present, we need to add its size + overhead to the last export
        // This is because the game reads bulk data inline with the export in Zen/IoStore format
        string ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
        long bulkDataAdjustment = 0;
        if (File.Exists(ubulkPath))
        {
            long ubulkSize = new FileInfo(ubulkPath).Length;
            // The game reads bulk data inline with serialization overhead
            // Overhead = FBulkData headers + alignment padding
            // TODO: Calculate this properly based on number of bulk data entries
            // For now, 432 bytes works for tested StaticMesh assets
            const long BULK_DATA_OVERHEAD = 432;
            bulkDataAdjustment = ubulkSize + BULK_DATA_OVERHEAD;
            Console.Error.WriteLine($"[FixSerializeSize] Found .ubulk ({ubulkSize} bytes), adding {bulkDataAdjustment} (overhead={BULK_DATA_OVERHEAD}) to last export");
        }
        
        var fixes = new List<object>();
        int fixedCount = 0;

        for (int i = 0; i < sortedExports.Count; i++)
        {
            var export = sortedExports[i];
            long startInUexp = export.SerialOffset - headerSize;
            // For the last export, exclude the 4-byte PACKAGE_FILE_TAG (0x9E2A83C1) at the end of .uexp
            // The tag is not part of the export data and should not be included in SerialSize
            long endInUexp = (i < sortedExports.Count - 1) 
                ? sortedExports[i + 1].SerialOffset - headerSize 
                : uexpSize - 4;  // Subtract 4 bytes for PACKAGE_FILE_TAG
            
            long actualSize = endInUexp - startInUexp;
            
            // For the last export, add bulk data adjustment if .ubulk exists
            if (i == sortedExports.Count - 1 && bulkDataAdjustment > 0)
            {
                actualSize += bulkDataAdjustment;
            }
            
            long headerSize_current = export.SerialSize;
            
            if (actualSize != headerSize_current)
            {
                // Use actual size directly - no padding added
                // This ensures SerialSize matches the real export data size
                export.SerialSize = actualSize;
                fixes.Add(new
                {
                    export_name = export.ObjectName?.Value?.Value ?? $"Export_{i}",
                    old_size = headerSize_current,
                    new_size = actualSize,
                    difference = actualSize - headerSize_current
                });
                fixedCount++;
            }
        }

        // Write the fixed asset back to disk if any fixes were made
        if (fixedCount > 0)
        {
            asset.Write(uassetPath);
            Console.Error.WriteLine($"[FixSerializeSize] Wrote {fixedCount} SerialSize fixes to {uassetPath}");
        }

        return new
        {
            success = true,
            message = fixedCount > 0 ? $"Fixed {fixedCount} SerialSize mismatches" : "No fixes needed",
            fixed_count = fixedCount,
            fixes = fixes
        };
    }
    
    #endregion

    #region Debug Operations
    
    private static UAssetResponse DebugAssetInfo(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            string? usmapPath = Environment.GetEnvironmentVariable("USMAP_PATH");
            var asset = LoadAsset(filePath, usmapPath);
            
            var info = new Dictionary<string, object>();
            
            var exports = new List<Dictionary<string, string>>();
            foreach (var export in asset.Exports)
            {
                exports.Add(new Dictionary<string, string>
                {
                    ["ExportType"] = export.GetType().Name,
                    ["ObjectName"] = export.ObjectName?.Value?.Value ?? "null",
                    ["ClassName"] = GetExportClassName(asset, export)
                });
            }
            info["Exports"] = exports;
            
            var imports = asset.Imports.Select(i => i.ObjectName?.Value?.Value ?? "null").ToList();
            info["Imports"] = imports;
            info["DetectedType"] = DetectAssetType(asset);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Asset info for {Path.GetFileName(filePath)}",
                Data = info
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    private static void DumpAssetInfo(UAsset asset, string filePath)
    {
        Console.WriteLine($"=== Asset Dump: {Path.GetFileName(filePath)} ===");
        Console.WriteLine($"Detected Type: {DetectAssetType(asset)}");
        Console.WriteLine($"Exports: {asset.Exports.Count}");
        Console.WriteLine($"Imports: {asset.Imports.Count}");
        Console.WriteLine();
        
        Console.WriteLine("=== Exports ===");
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            Console.WriteLine($"  [{i}] {export.ObjectName?.Value?.Value} (Class: {GetExportClassName(asset, export)})");
            Console.WriteLine($"      SerialOffset: 0x{export.SerialOffset:X}, SerialSize: {export.SerialSize}");
        }
        
        Console.WriteLine();
        Console.WriteLine("=== Imports ===");
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            Console.WriteLine($"  [{i}] {import.ObjectName?.Value?.Value}");
        }
    }
    
    #endregion

    #region Asset Loading Helpers
    
    private static UAsset LoadAsset(string filePath, string? usmapPath)
    {
        Console.Error.WriteLine($"[UAssetTool] Loading asset: {filePath}");
        Console.Error.WriteLine($"[UAssetTool] USMAP path: {usmapPath ?? "null"}");
        Usmap? mappings = LoadMappings(usmapPath);
        Console.Error.WriteLine($"[UAssetTool] Mappings loaded: {mappings != null}");
        if (mappings != null)
        {
            Console.Error.WriteLine($"[UAssetTool] Mappings has {mappings.Schemas?.Count ?? 0} schemas");
        }
        return LoadAssetWithMappings(filePath, mappings);
    }
    
    public static UAsset LoadAssetWithMappings(string filePath, Usmap? mappings)
    {
        var asset = new UAsset(filePath, EngineVersion.VER_UE5_3, mappings);
        asset.UseSeparateBulkDataFiles = true;
        Console.Error.WriteLine($"[UAssetTool] Asset loaded: HasUnversionedProperties={asset.HasUnversionedProperties}, Exports={asset.Exports?.Count ?? 0}");
        return asset;
    }
    
    /// <summary>
    /// Pre-load all referenced assets to populate schema cache for proper JSON serialization.
    /// This fixes the "Failed to find a valid schema for parent name" error when converting
    /// Blueprint assets that inherit from classes defined in other asset files.
    /// </summary>
    private static void PreloadReferencedAssetsForSchemas(UAsset asset)
    {
        if (asset.Mappings == null || asset.Imports == null) return;
        
        int loadedCount = 0;
        var processedPaths = new HashSet<string>();
        
        // Iterate through all imports and try to load referenced assets
        foreach (var import in asset.Imports)
        {
            // Skip script imports (native classes)
            if (import.ClassPackage?.Value?.Value?.StartsWith("/Script") == true) continue;
            
            // Get the package path from the outer chain
            FName? packagePath = null;
            if (import.OuterIndex.IsImport())
            {
                var outer = import.OuterIndex.ToImport(asset);
                // Walk up the outer chain to find the package
                while (outer != null)
                {
                    if (outer.OuterIndex.Index == 0)
                    {
                        // This is the package
                        packagePath = outer.ObjectName;
                        break;
                    }
                    else if (outer.OuterIndex.IsImport())
                    {
                        outer = outer.OuterIndex.ToImport(asset);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            
            if (packagePath != null && !processedPaths.Contains(packagePath.Value.Value))
            {
                processedPaths.Add(packagePath.Value.Value);
                
                // Try to load the referenced asset
                if (asset.PullSchemasFromAnotherAsset(packagePath, import.ObjectName))
                {
                    loadedCount++;
                }
            }
        }
        
        if (loadedCount > 0)
        {
            Console.Error.WriteLine($"[UAssetTool] Pre-loaded {loadedCount} referenced assets for schema resolution");
        }
        
        if (asset.OtherAssetsFailedToAccess.Count > 0)
        {
            Console.Error.WriteLine($"[UAssetTool] Warning: Could not find {asset.OtherAssetsFailedToAccess.Count} referenced assets on disk:");
            foreach (var missingPath in asset.OtherAssetsFailedToAccess)
            {
                Console.Error.WriteLine($"[UAssetTool]   - {missingPath.Value.Value}");
            }
            Console.Error.WriteLine($"[UAssetTool] To fix schema errors, extract these parent Blueprint assets to the same Content folder structure.");
        }
    }
    
    private static int CountOccurrences(byte[] data, byte[] pattern)
    {
        int count = 0;
        int end = data.Length - pattern.Length;
        for (int i = 0; i <= end; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) { count++; i += pattern.Length - 1; }
        }
        return count;
    }

    public static Usmap? LoadMappings(string? usmapPath)
    {
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
        {
            Console.Error.WriteLine($"[UAssetTool] Loading USMAP from: {usmapPath}");
            try
            {
                var usmap = new Usmap(usmapPath);
                Console.Error.WriteLine($"[UAssetTool] USMAP loaded successfully");
                return usmap;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UAssetTool] Failed to load USMAP: {ex.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine($"[UAssetTool] USMAP file not found or path is null");
        }
        return null;
    }
    
    private static void WriteJsonResponse(bool success, string message, object? data = null)
    {
        var response = new UAssetResponse { Success = success, Message = message, Data = data };
        Console.WriteLine(JsonSerializer.Serialize(response));
        Console.Out.Flush(); // Ensure response is sent immediately
    }
    
    #endregion

    #region Zen Package Conversion
    
    private static UAssetResponse ConvertToZen(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            byte[] zenData = ZenPackage.ZenConverter.ConvertLegacyToZen(filePath);
            
            string outputPath = Path.ChangeExtension(filePath, ".uzenasset");
            File.WriteAllBytes(outputPath, zenData);
            
            return new UAssetResponse 
            { 
                Success = true, 
                Message = $"Converted to Zen format: {outputPath}",
                Data = new { output_path = outputPath, size = zenData.Length }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Conversion failed: {ex.Message}" };
        }
    }
    
    private static UAssetResponse ConvertFromZen(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            // TODO: Implement Zen to Legacy conversion
            return new UAssetResponse 
            { 
                Success = false, 
                Message = "Zen to Legacy conversion not yet implemented" 
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Conversion failed: {ex.Message}" };
        }
    }
    
    #endregion
    
    /// <summary>
    /// Dump raw Zen package data from game IoStore for comparison
    /// </summary>
    private static int CliDumpZenFromGame(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool dump_zen_from_game <paks_path> <package_path> [output_file]");
            Console.Error.WriteLine("Example: UAssetTool dump_zen_from_game \"/path/to/Game/Paks\" \"/Game/Marvel/Characters/1057/1057001/Meshes/SK_1057_1057001\" original.bin");
            return 1;
        }

        string paksPath = args[1];
        string packagePath = args[2];
        string? outputFile = args.Length > 3 ? args[3] : null;

        if (!Directory.Exists(paksPath))
        {
            Console.Error.WriteLine($"Paks directory not found: {paksPath}");
            return 1;
        }

        try
        {
            // Find all .utoc files
            var utocFiles = Directory.GetFiles(paksPath, "*.utoc", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f)
                .ToList();

            Console.WriteLine($"Searching {utocFiles.Count} containers for package: {packagePath}");

            // Calculate package ID
            var packageId = IoStore.FPackageId.FromName(packagePath);
            Console.WriteLine($"Package ID: 0x{packageId.Value:X16}");

            // Parse AES key for Marvel Rivals
            byte[] aesKey = Convert.FromHexString("0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74");

            foreach (var utocFile in utocFiles)
            {
                try
                {
                    using var reader = new IoStore.IoStoreReader(utocFile, aesKey);
                    
                    // Look for ExportBundleData chunk for this package
                    var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);
                    
                    if (reader.HasChunk(chunkId))
                    {
                        Console.WriteLine($"Found in: {Path.GetFileName(utocFile)}");
                        
                        byte[] zenData = reader.ReadChunk(chunkId);
                        Console.WriteLine($"Zen package size: {zenData.Length} bytes");
                        
                        // Dump header info
                        Console.WriteLine("\n=== Zen Package Header (first 256 bytes) ===");
                        DumpHex(zenData, 0, Math.Min(256, zenData.Length));
                        
                        // Also check for BulkData chunk
                        var bulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.BulkData);
                        if (reader.HasChunk(bulkChunkId))
                        {
                            byte[] bulkData = reader.ReadChunk(bulkChunkId);
                            Console.WriteLine($"\nBulkData chunk 0: {bulkData.Length} bytes");
                        }
                        
                        // Check for additional BulkData chunks
                        for (int i = 1; i < 10; i++)
                        {
                            var bulkChunkIdN = IoStore.FIoChunkId.FromPackageId(packageId, (ushort)i, IoStore.EIoChunkType.BulkData);
                            if (reader.HasChunk(bulkChunkIdN))
                            {
                                byte[] bulkData = reader.ReadChunk(bulkChunkIdN);
                                Console.WriteLine($"BulkData chunk {i}: {bulkData.Length} bytes");
                            }
                        }
                        
                        // Save to file if requested
                        if (!string.IsNullOrEmpty(outputFile))
                        {
                            File.WriteAllBytes(outputFile, zenData);
                            Console.WriteLine($"\nSaved to: {outputFile}");
                        }
                        
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Warning: Error reading {Path.GetFileName(utocFile)}: {ex.Message}");
                }
            }

            Console.Error.WriteLine($"Package not found: {packagePath}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
    
    private static void DumpHex(byte[] data, int offset, int length)
    {
        for (int i = 0; i < length; i += 16)
        {
            Console.Write($"{offset + i:X8}: ");
            
            // Hex bytes
            for (int j = 0; j < 16; j++)
            {
                if (i + j < length)
                    Console.Write($"{data[offset + i + j]:X2} ");
                else
                    Console.Write("   ");
            }
            
            Console.Write(" ");
            
            // ASCII
            for (int j = 0; j < 16 && i + j < length; j++)
            {
                byte b = data[offset + i + j];
                Console.Write(b >= 32 && b < 127 ? (char)b : '.');
            }
            
            Console.WriteLine();
        }
    }
    
    /// <summary>
    /// List IoStore contents showing which packages have accompanying BulkData (.ubulk).
    /// </summary>
    /// <summary>
    /// Diagnostic: locate a package by name substring, then report EVERY chunk that shares its
    /// package id across all containers in a Paks dir — including bare BulkData/OptionalBulkData
    /// (.ubulk/.uptnl) chunks that have no export bundle and are invisible to list_iostore.
    /// Usage: probe_chunks &lt;paks_dir&gt; &lt;name_substr&gt; [--aes &lt;key&gt;]
    /// </summary>
    /// <summary>
    /// Diagnostic: load an asset via the object model and write it back UNMODIFIED to a new path,
    /// so the input/output .uasset/.uexp can be byte-compared (object-model round-trip fidelity test).
    /// Usage: roundtrip &lt;input.uasset&gt; &lt;output.uasset&gt; [--usmap &lt;path&gt;]
    /// </summary>
    private static int CliRoundtrip(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool roundtrip <input.uasset> <output.uasset> [--usmap <path>]");
            return 1;
        }
        string input = args[1], output = args[2];
        string? usmap = null;
        for (int i = 3; i < args.Length; i++)
            if (args[i] == "--usmap" && i + 1 < args.Length) usmap = args[++i];

        var asset = LoadAsset(input, usmap);
        asset.UseSeparateBulkDataFiles = true;
        asset.Write(output);
        Console.WriteLine($"Round-tripped {input} -> {output}");
        return 0;
    }

    /// <summary>Find the first offset of needle within haystack (naive; needle is large/unique here).</summary>
    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return -1;
        int last = haystack.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    /// <summary>Read one DataResource's bytes from the right source file (by LegacyBulkDataFlags).</summary>
    private static byte[] ReadResourceBytes(FObjectDataResource dr, string uexp, string ubulk, string uptnl)
    {
        const uint INLINE = 0x40, INSEP = 0x100, OPT = 0x800;
        int size = (int)dr.RawSize; long off = dr.SerialOffset;
        if (size <= 0) return Array.Empty<byte>();
        string? file = (dr.LegacyBulkDataFlags & INLINE) != 0 ? uexp
                     : (dr.LegacyBulkDataFlags & OPT) != 0 ? uptnl
                     : (dr.LegacyBulkDataFlags & INSEP) != 0 ? ubulk : uexp;
        if (file == null || !File.Exists(file)) return Array.Empty<byte>();
        var bytes = File.ReadAllBytes(file);
        if (off < 0 || off + size > bytes.Length) return Array.Empty<byte>();
        var d = new byte[size]; Array.Copy(bytes, off, d, 0, size); return d;
    }

    /// <summary>
    /// Experiment: rebuild a texture as ALL-INLINE (every mip's pixels populated + DataResources
    /// rebuilt to inline) via the object model, write it, then re-parse to test structural validity.
    /// Usage: test_objinject &lt;in.uasset&gt; &lt;out.uasset&gt; [--usmap &lt;m&gt;]
    /// </summary>
    private static int CliTestObjInject(string[] args)
    {
        string input = args[1], output = args[2]; string? usmap = null;
        for (int i = 3; i < args.Length; i++) if (args[i] == "--usmap" && i + 1 < args.Length) usmap = args[++i];

        var asset = LoadAsset(input, usmap);
        asset.UseSeparateBulkDataFiles = true;
        var tex = asset.Exports.OfType<TextureExport>().FirstOrDefault();
        if (tex?.PlatformData == null) { Console.Error.WriteLine("no platform data"); return 1; }
        var pd = tex.PlatformData;
        var origDR = asset.DataResources!;
        Console.WriteLine($"Original: {pd.SizeX}x{pd.SizeY} {pd.PixelFormat}, {pd.Mips.Count} mips, DataResources={origDR.Count}");

        string uexp = Path.ChangeExtension(input, ".uexp"), ubulk = Path.ChangeExtension(input, ".ubulk"), uptnl = Path.ChangeExtension(input, ".uptnl");
        var outer = origDR[0].OuterIndex;
        var newMips = new List<FTexture2DMipMap>();
        var newDR = new List<FObjectDataResource>();
        for (int i = 0; i < pd.Mips.Count && i < origDR.Count; i++)
        {
            byte[] data = ReadResourceBytes(origDR[i], uexp, ubulk, uptnl);
            int w = Math.Max(1, pd.SizeX >> i), h = Math.Max(1, pd.SizeY >> i);
            Console.WriteLine($"  mip{i}: {w}x{h} -> read {data.Length} bytes");
            var bd = new FByteBulkData(data);
            bd.Header.DataResourceIndex = i;
            bd.Header.BulkDataFlags = EBulkDataFlags.BULKDATA_ForceInlinePayload;
            newMips.Add(new FTexture2DMipMap(bd, w, h, 1));
            newDR.Add(new FObjectDataResource(EObjectDataResourceFlags.Inline, 0, -1, data.Length, data.Length, outer, (uint)EBulkDataFlags.BULKDATA_ForceInlinePayload, 0));
        }
        pd.Mips = newMips; pd.FirstMipToSerialize = 0;
        asset.DataResources = newDR;
        asset.Write(output);

        // Pass 2: the inline pixel data is written sequentially after the mip headers; locate mip0's
        // data in the written .uexp and set each DataResource's SerialOffset to its true position
        // (the game reads inline sequentially and ignores these, but our offset-based extractor needs them).
        string outUexpPath = Path.ChangeExtension(output, ".uexp");
        byte[] outBytes = File.ReadAllBytes(outUexpPath);
        int inlineStart = newMips[0].BulkData.Data.Length > 0 ? IndexOfBytes(outBytes, newMips[0].BulkData.Data) : -1;
        if (inlineStart >= 0)
        {
            long off = inlineStart;
            var fixedDR = new List<FObjectDataResource>();
            foreach (var d in newDR)
            {
                fixedDR.Add(new FObjectDataResource(d.Flags, off, -1, d.SerialSize, d.RawSize, d.OuterIndex, d.LegacyBulkDataFlags, d.CookedIndex));
                off += d.RawSize;
            }
            asset.DataResources = fixedDR;
            asset.Write(output);
            Console.WriteLine($"Pass2: inline data starts at .uexp offset {inlineStart}; SerialOffsets fixed");
        }
        long outUexp = new FileInfo(outUexpPath).Length;
        Console.WriteLine($"Wrote {output}: .uexp={outUexp:N0} bytes");

        // Re-parse to verify structural validity
        var asset2 = LoadAsset(output, usmap);
        var tex2 = asset2.Exports.OfType<TextureExport>().FirstOrDefault();
        if (tex2?.PlatformData == null) Console.WriteLine("RE-PARSE: FAILED (no PlatformData)");
        else Console.WriteLine($"RE-PARSE: OK -> {tex2.PlatformData.SizeX}x{tex2.PlatformData.SizeY}, {tex2.PlatformData.Mips.Count} mips, DataResources={asset2.DataResources?.Count}");
        return 0;
    }

    private static int CliProbeChunks(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool probe_chunks <paks_dir> <name_substr> [--aes <key>]");
            return 1;
        }
        string paksDir = args[1];
        string needle = args[2];
        byte[]? aes = null;
        for (int i = 3; i < args.Length; i++)
            if (args[i] == "--aes" && i + 1 < args.Length) aes = IoStore.IoStoreReader.ParseAesKey(args[++i]);

        var utocs = Directory.Exists(paksDir)
            ? Directory.GetFiles(paksDir, "*.utoc")
            : new[] { paksDir };

        var readers = new List<(string name, IoStore.IoStoreReader r)>();
        foreach (var u in utocs)
        {
            try { readers.Add((Path.GetFileName(u), new IoStore.IoStoreReader(u, aes))); }
            catch
            {
                try { readers.Add((Path.GetFileName(u), new IoStore.IoStoreReader(u, (byte[]?)null))); }
                catch (Exception ex) { Console.Error.WriteLine($"  (skip {Path.GetFileName(u)}: {ex.Message})"); }
            }
        }

        // Step 1: resolve the package id from the ExportBundleData chunk whose path matches the needle
        ulong pkgId = 0; string? pkgPath = null; string? foundIn = null;
        foreach (var (name, r) in readers)
        {
            foreach (var c in r.GetChunks())
            {
                if (c.GetChunkType() != IoStore.EIoChunkType.ExportBundleData) continue;
                var p = r.GetChunkPath(c);
                if (p != null && p.Contains(needle, StringComparison.OrdinalIgnoreCase))
                { pkgId = c.Id; pkgPath = p; foundIn = name; break; }
            }
            if (pkgId != 0) break;
        }
        if (pkgId == 0)
        {
            Console.WriteLine($"No ExportBundleData package matching '{needle}' found in {readers.Count} containers.");
            foreach (var (_, r) in readers) r.Dispose();
            return 1;
        }
        Console.WriteLine($"Package match: id=0x{pkgId:X16}  container={foundIn}");
        Console.WriteLine($"  path={pkgPath}");
        Console.WriteLine($"Chunks sharing this package id across all {readers.Count} containers:");

        // Step 2: every chunk (any type) with this id, in any container
        int total = 0;
        foreach (var (name, r) in readers)
        {
            foreach (var c in r.GetChunks())
            {
                if (c.Id != pkgId) continue;
                long size = -1;
                try { size = r.ReadChunk(c).Length; } catch { }
                Console.WriteLine($"  [{name}] type={c.GetChunkType()} index={c.Index} size={(size >= 0 ? size.ToString("N0") : "?")}");
                total++;
            }
        }
        Console.WriteLine($"Total chunks for package: {total}");
        foreach (var (_, r) in readers) r.Dispose();
        return 0;
    }

    private static int CliListIoStore(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool list_iostore <utoc_path_or_dir> [--aes <key>] [--filter <pattern>]");
            Console.Error.WriteLine("Lists all packages in an IoStore with their chunk types (ExportBundleData, BulkData, etc.)");
            return 1;
        }

        string inputPath = args[1];
        string? aesKey = null;
        string? filterPattern = null;

        for (int i = 2; i < args.Length; i++)
        {
            if ((args[i] == "--aes" || args[i] == "--aes-key") && i + 1 < args.Length)
                aesKey = args[++i];
            else if (args[i] == "--filter" && i + 1 < args.Length)
                filterPattern = args[++i];
        }

        try
        {
            // Collect utoc files
            var utocFiles = new List<string>();
            if (Directory.Exists(inputPath))
            {
                utocFiles.AddRange(Directory.GetFiles(inputPath, "*.utoc", SearchOption.TopDirectoryOnly));
            }
            else if (File.Exists(inputPath) && inputPath.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
            {
                utocFiles.Add(inputPath);
            }
            else
            {
                Console.Error.WriteLine($"Not found: {inputPath}");
                return 1;
            }

            foreach (var utocPath in utocFiles)
            {
                Console.WriteLine($"=== {Path.GetFileName(utocPath)} ===");

                IoStore.IoStoreReader reader;
                if (!string.IsNullOrEmpty(aesKey))
                {
                    reader = new IoStore.IoStoreReader(utocPath, IoStore.IoStoreReader.ParseAesKey(aesKey));
                }
                else
                {
                    // Try without key first, auto-detect obfuscated containers
                    reader = new IoStore.IoStoreReader(utocPath, (byte[]?)null);
                    if (reader.Toc.IsEncrypted)
                    {
                        // Obfuscated mod - reload with default Marvel Rivals AES key
                        reader.Dispose();
                        Console.Error.WriteLine("[ListIoStore] Detected obfuscated container, using game AES key...");
                        reader = new IoStore.IoStoreReader(utocPath, IoStore.IoStoreReader.ParseAesKey("0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74"));
                    }
                }

                // Group chunks by package ID
                var packageChunks = new Dictionary<ulong, List<IoStore.FIoChunkId>>();
                foreach (var chunk in reader.GetChunks())
                {
                    if (!packageChunks.ContainsKey(chunk.Id))
                        packageChunks[chunk.Id] = new List<IoStore.FIoChunkId>();
                    packageChunks[chunk.Id].Add(chunk);
                }

                int totalPackages = 0;
                int withBulk = 0;
                int withOptionalBulk = 0;
                int withoutBulk = 0;

                foreach (var (packageId, chunks) in packageChunks.OrderBy(kvp => kvp.Key))
                {
                    bool hasExportBundle = chunks.Any(c => c.GetChunkType() == IoStore.EIoChunkType.ExportBundleData);
                    if (!hasExportBundle) continue; // Skip non-package chunks (ContainerHeader, ScriptObjects, etc.)

                    string? path = reader.GetChunkPath(chunks.First(c => c.GetChunkType() == IoStore.EIoChunkType.ExportBundleData));
                    if (path == null) path = $"<unknown:{packageId:X16}>";

                    if (filterPattern != null && !path.Contains(filterPattern, StringComparison.OrdinalIgnoreCase))
                        continue;

                    totalPackages++;

                    bool hasBulk = chunks.Any(c => c.GetChunkType() == IoStore.EIoChunkType.BulkData);
                    bool hasOptBulk = chunks.Any(c => c.GetChunkType() == IoStore.EIoChunkType.OptionalBulkData);
                    bool hasMemMapped = chunks.Any(c => c.GetChunkType() == IoStore.EIoChunkType.MemoryMappedBulkData);

                    if (hasBulk) withBulk++;
                    else if (hasOptBulk) withOptionalBulk++;
                    else withoutBulk++;

                    // Build chunk type summary
                    var typeFlags = new List<string>();
                    if (hasBulk) typeFlags.Add(".ubulk");
                    if (hasOptBulk) typeFlags.Add(".uptnl");
                    if (hasMemMapped) typeFlags.Add(".m.ubulk");

                    string bulkStatus = typeFlags.Count > 0 ? $" [{string.Join(", ", typeFlags)}]" : " [no bulk]";

                    // Get sizes
                    var exportChunk = chunks.First(c => c.GetChunkType() == IoStore.EIoChunkType.ExportBundleData);
                    int exportSize = 0;
                    try { exportSize = reader.ReadChunk(exportChunk).Length; } catch { }

                    int bulkSize = 0;
                    if (hasBulk)
                    {
                        foreach (var bc in chunks.Where(c => c.GetChunkType() == IoStore.EIoChunkType.BulkData))
                        {
                            try { bulkSize += reader.ReadChunk(bc).Length; } catch { }
                        }
                    }

                    string sizeInfo = hasBulk
                        ? $"export={exportSize:N0}b bulk={bulkSize:N0}b"
                        : $"export={exportSize:N0}b";

                    Console.WriteLine($"  {path}{bulkStatus}  ({sizeInfo})");
                }

                Console.WriteLine();
                Console.WriteLine($"  Summary: {totalPackages} packages, {withBulk} with .ubulk, {withOptionalBulk} with .uptnl, {withoutBulk} without bulk data");
                Console.WriteLine();

                reader.Dispose();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Clone packages directly from game IoStore to create a mod IoStore.
    /// This preserves the exact Zen package structure without going through legacy conversion.
    /// </summary>
    private static int CliCloneModIoStore(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: UAssetTool clone_mod_iostore <paks_path> <output_base> <package_paths...>");
            Console.Error.WriteLine("  Clones packages directly from game IoStore, preserving exact Zen structure.");
            Console.Error.WriteLine("  This avoids the legacy conversion which can add extra names to the name map.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --compress            - Enable Oodle compression (default: enabled)");
            Console.Error.WriteLine("  --no-compress         - Disable compression");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Example:");
            Console.Error.WriteLine("  clone_mod_iostore \"/path/to/Game/Paks\" \"MyMod\" \"/Game/Marvel/Characters/1014/1014001/Meshes/SK_1014_1014001\"");
            return 1;
        }

        string paksPath = args[1];
        string outputBase = args[2];
        bool enableCompression = true;
        var packagePaths = new List<string>();

        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--compress")
                enableCompression = true;
            else if (args[i] == "--no-compress")
                enableCompression = false;
            else if (args[i].StartsWith("/Game/") || args[i].StartsWith("/Script/"))
                packagePaths.Add(args[i]);
            else
                Console.Error.WriteLine($"Warning: Ignoring unknown argument: {args[i]}");
        }

        if (packagePaths.Count == 0)
        {
            Console.Error.WriteLine("Error: No package paths provided");
            return 1;
        }

        if (!Directory.Exists(paksPath))
        {
            Console.Error.WriteLine($"Error: Paks directory not found: {paksPath}");
            return 1;
        }

        try
        {
            // Find all .utoc files
            var utocFiles = Directory.GetFiles(paksPath, "*.utoc", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f)
                .ToList();

            Console.Error.WriteLine($"[CloneModIoStore] Creating IoStore mod bundle: {outputBase}");
            Console.Error.WriteLine($"[CloneModIoStore]   Packages: {packagePaths.Count}");
            Console.Error.WriteLine($"[CloneModIoStore]   Compression: {(enableCompression ? "Oodle" : "None")}");
            Console.Error.WriteLine($"[CloneModIoStore]   Source containers: {utocFiles.Count}");

            // Parse AES key for Marvel Rivals
            byte[] aesKey = Convert.FromHexString("0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74");

            string utocPath = outputBase + ".utoc";
            string pakPath = outputBase + ".pak";
            string mountPoint = "../../../";

            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                mountPoint,
                enableCompression,
                false, // no encryption
                null);

            var filePaths = new List<string>();
            int successCount = 0;

            foreach (var packagePath in packagePaths)
            {
                Console.Error.WriteLine($"  Cloning: {packagePath}");

                var packageId = IoStore.FPackageId.FromName(packagePath);
                bool found = false;

                foreach (var utocFile in utocFiles)
                {
                    try
                    {
                        using var reader = new IoStore.IoStoreReader(utocFile, aesKey);
                        var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);

                        if (reader.HasChunk(chunkId))
                        {
                            // Read original Zen data
                            byte[] zenData = reader.ReadChunk(chunkId);
                            Console.Error.WriteLine($"    Found in: {Path.GetFileName(utocFile)}");
                            Console.Error.WriteLine($"    Zen size: {zenData.Length} bytes");

                            // Parse the Zen header to get export count and imported packages
                            var zenHeader = ZenPackage.FZenPackageHeader.Deserialize(zenData, ZenPackage.EIoContainerHeaderVersion.NoExportInfo);

                            // Create store entry
                            var storeEntry = new IoStore.StoreEntry
                            {
                                ExportCount = zenHeader.ExportMap.Count,
                                ExportBundleCount = 1,
                                LoadOrder = 0
                            };

                            // Add imported packages
                            foreach (ulong importedPkgId in zenHeader.ImportedPackages)
                            {
                                storeEntry.ImportedPackages.Add(new IoStore.FPackageId(importedPkgId));
                            }
                            if (storeEntry.ImportedPackages.Count > 0)
                            {
                                Console.Error.WriteLine($"    Imported packages: {storeEntry.ImportedPackages.Count}");
                            }

                            // Convert package path to file path for directory index
                            // /Game/Marvel/Characters/... -> Marvel/Content/Marvel/Characters/...
                            string filePath;
                            if (packagePath.StartsWith("/Game/"))
                            {
                                filePath = "Marvel/Content/" + packagePath.Substring("/Game/".Length);
                            }
                            else
                            {
                                filePath = packagePath.TrimStart('/');
                            }

                            string fullPath = mountPoint + filePath + ".uasset";
                            ioStoreWriter.WritePackageChunk(chunkId, fullPath, zenData, storeEntry);
                            filePaths.Add(filePath + ".uasset");
                            filePaths.Add(filePath + ".uexp");

                            // Check for BulkData chunk
                            var bulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.BulkData);
                            if (reader.HasChunk(bulkChunkId))
                            {
                                byte[] bulkData = reader.ReadChunk(bulkChunkId);
                                string bulkFullPath = mountPoint + filePath + ".ubulk";
                                ioStoreWriter.WriteChunk(bulkChunkId, bulkFullPath, bulkData);
                                filePaths.Add(filePath + ".ubulk");
                                Console.Error.WriteLine($"    BulkData: {bulkData.Length} bytes");
                            }

                            found = true;
                            successCount++;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Silently continue to next container
                        _ = ex;
                    }
                }

                if (!found)
                {
                    Console.Error.WriteLine($"    ERROR: Package not found in any container");
                }
            }

            // Complete IoStore
            ioStoreWriter.Complete();

            // Create companion PAK with chunk names
            IoStore.ChunkNamesPakWriter.Create(pakPath, filePaths, mountPoint, 0, null);

            Console.Error.WriteLine();
            Console.Error.WriteLine($"SUCCESS: Created IoStore mod bundle:");
            Console.Error.WriteLine($"  {utocPath}");
            Console.Error.WriteLine($"  {Path.ChangeExtension(utocPath, ".ucas")}");
            Console.Error.WriteLine($"  {pakPath}");
            Console.Error.WriteLine($"  Packages cloned: {successCount}/{packagePaths.Count}");

            return successCount > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int CliListPak(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool list_pak <pak_path> [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Lists files inside a PAK without extracting them.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --aes <key>          AES decryption key (hex string, 64 chars)");
            Console.Error.WriteLine("  --filter <patterns>  Only show files matching patterns (space-separated)");
            Console.Error.WriteLine("  --paths-only         Print only the paths (no header, no sizes)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Examples:");
            Console.Error.WriteLine("  list_pak mod.pak");
            Console.Error.WriteLine("  list_pak mod.pak --filter Marvel/Content/WwiseAudio");
            Console.Error.WriteLine("  list_pak mod.pak --paths-only");
            return 1;
        }

        string pakPath = args[1];
        string? aesKey = null;
        var filters = new List<string>();
        bool pathsOnly = false;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--aes" && i + 1 < args.Length)
            {
                aesKey = args[++i];
            }
            else if (args[i] == "--filter")
            {
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    filters.Add(args[++i]);
                }
            }
            else if (args[i] == "--paths-only")
            {
                pathsOnly = true;
            }
        }

        if (!File.Exists(pakPath))
        {
            Console.Error.WriteLine($"PAK file not found: {pakPath}");
            return 1;
        }

        try
        {
            using var pakReader = new IoStore.PakReader(pakPath, aesKey);

            var files = pakReader.Files.ToList();
            if (filters.Count > 0)
            {
                files = files.Where(f =>
                    filters.Any(filter => f.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);

            if (pathsOnly)
            {
                foreach (var f in files)
                    Console.WriteLine(f);
                return 0;
            }

            Console.WriteLine($"PAK: {pakPath}");
            Console.WriteLine($"  Version:         {pakReader.Version}");
            Console.WriteLine($"  Mount Point:     {pakReader.MountPoint}");
            Console.WriteLine($"  Encrypted Index: {pakReader.EncryptedIndex}");
            Console.WriteLine($"  Total Files:     {pakReader.Files.Count}");
            if (filters.Count > 0)
                Console.WriteLine($"  Filtered:        {files.Count} (matching [{string.Join(", ", filters)}])");
            Console.WriteLine();

            ulong totalUncompressed = 0;
            ulong totalCompressed = 0;
            foreach (var f in files)
            {
                var entry = pakReader.GetEntry(f);
                if (entry != null)
                {
                    totalUncompressed += entry.UncompressedSize;
                    totalCompressed += entry.CompressedSize;
                    string flags = entry.IsEncrypted ? " [enc]" : "";
                    string comp = entry.CompressionSlot.HasValue ? " [comp]" : "";
                    Console.WriteLine($"  {f}");
                    Console.WriteLine($"      {entry.UncompressedSize:N0} bytes (stored: {entry.CompressedSize:N0}){flags}{comp}");
                }
                else
                {
                    Console.WriteLine($"  {f}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Total: {files.Count} files, {totalUncompressed:N0} bytes uncompressed, {totalCompressed:N0} bytes stored");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int CliExtractPak(string[] args)
    {
        // Usage: extract_pak <pak_path> <output_dir> [--aes <key>] [--filter <patterns...>]
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool extract_pak <pak_path> <output_dir> [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --aes <key>          AES decryption key (hex string, 64 chars)");
            Console.Error.WriteLine("  --filter <patterns>  Only extract files matching patterns (space-separated)");
            Console.Error.WriteLine("  --list               List files only, don't extract");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Examples:");
            Console.Error.WriteLine("  extract_pak mod.pak output --filter SK_1036 MI_Body");
            Console.Error.WriteLine("  extract_pak mod.pak output --filter Meshes Textures Materials");
            Console.Error.WriteLine("  extract_pak mod.pak output --list");
            return 1;
        }

        string pakPath = args[1];
        string outputDir = args[2];
        string? aesKey = null;
        List<string> filters = new();
        bool listOnly = false;

        // Parse options
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--aes" && i + 1 < args.Length)
            {
                aesKey = args[++i];
            }
            else if (args[i] == "--filter")
            {
                // Collect all following args until next option (starts with --)
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    filters.Add(args[++i]);
                }
            }
            else if (args[i] == "--list")
            {
                listOnly = true;
            }
        }

        if (!File.Exists(pakPath))
        {
            Console.Error.WriteLine($"PAK file not found: {pakPath}");
            return 1;
        }

        try
        {
            Console.Error.WriteLine($"[ExtractPak] Opening PAK: {pakPath}");
            
            using var pakReader = new IoStore.PakReader(pakPath, aesKey);
            
            Console.Error.WriteLine($"[ExtractPak] PAK Version: {pakReader.Version}");
            Console.Error.WriteLine($"[ExtractPak] Mount Point: {pakReader.MountPoint}");
            Console.Error.WriteLine($"[ExtractPak] Encrypted Index: {pakReader.EncryptedIndex}");
            Console.Error.WriteLine($"[ExtractPak] Total Files: {pakReader.Files.Count}");
            
            var filesToExtract = pakReader.Files.ToList();
            
            // Apply filters if specified (file must match ANY of the filters)
            if (filters.Count > 0)
            {
                filesToExtract = filesToExtract.Where(f => 
                    filters.Any(filter => f.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
                Console.Error.WriteLine($"[ExtractPak] Files matching filters [{string.Join(", ", filters)}]: {filesToExtract.Count}");
            }
            
            if (listOnly)
            {
                Console.WriteLine($"Files in PAK ({filesToExtract.Count}):");
                foreach (var file in filesToExtract)
                {
                    var entry = pakReader.GetEntry(file);
                    Console.WriteLine($"  {file}");
                    if (entry != null)
                    {
                        Console.WriteLine($"    Size: {entry.UncompressedSize} bytes, Compressed: {entry.CompressedSize} bytes");
                    }
                }
                return 0;
            }
            
            // Create output directory
            Directory.CreateDirectory(outputDir);
            
            int extracted = 0;
            int failed = 0;
            
            Parallel.ForEach(filesToExtract, file =>
            {
                try
                {
                    byte[] data = pakReader.Get(file);
                    
                    // Determine output path
                    string relativePath = file;
                    if (relativePath.StartsWith("../"))
                    {
                        while (relativePath.StartsWith("../"))
                            relativePath = relativePath.Substring(3);
                    }
                    relativePath = relativePath.TrimStart('/', '\\');
                    
                    string outputPath = Path.Combine(outputDir, relativePath);
                    string? outputDirPath = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(outputDirPath))
                        Directory.CreateDirectory(outputDirPath);
                    
                    File.WriteAllBytes(outputPath, data);
                    int cur = Interlocked.Increment(ref extracted);
                    if (cur % 50 == 0 || cur == filesToExtract.Count)
                    {
                        Console.Error.WriteLine($"[ExtractPak] Extracted {cur}/{filesToExtract.Count} files...");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ExtractPak] Failed to extract '{file}': {ex.Message}");
                    Interlocked.Increment(ref failed);
                }
            });
            
            Console.WriteLine($"Extraction complete: {extracted} extracted, {failed} failed");
            Console.WriteLine($"Output directory: {outputDir}");
            return failed > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
    
    #region PAK/IoStore JSON API Methods
    
    /// <summary>
    /// List all files in a PAK file
    /// </summary>
    private static UAssetResponse ListPakFiles(string? pakPath, string? aesKey)
    {
        if (string.IsNullOrEmpty(pakPath))
            return new UAssetResponse { Success = false, Message = "PAK path is required" };
        
        if (!File.Exists(pakPath))
            return new UAssetResponse { Success = false, Message = $"PAK file not found: {pakPath}" };
        
        try
        {
            using var pakReader = new IoStore.PakReader(pakPath, aesKey);
            
            var files = pakReader.Files.Select(f => new Dictionary<string, object?>
            {
                ["path"] = f,
                ["size"] = pakReader.GetEntry(f)?.UncompressedSize ?? 0,
                ["compressed_size"] = pakReader.GetEntry(f)?.CompressedSize ?? 0
            }).ToList();
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Found {files.Count} files in PAK",
                Data = new Dictionary<string, object?>
                {
                    ["file_count"] = files.Count,
                    ["mount_point"] = pakReader.MountPoint,
                    ["version"] = pakReader.Version,
                    ["encrypted_index"] = pakReader.EncryptedIndex,
                    ["files"] = files
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to read PAK: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Extract a single file from a PAK
    /// </summary>
    private static UAssetResponse ExtractPakFile(string? pakPath, string? internalPath, string? outputPath, string? aesKey)
    {
        if (string.IsNullOrEmpty(pakPath))
            return new UAssetResponse { Success = false, Message = "PAK path is required" };
        if (string.IsNullOrEmpty(internalPath))
            return new UAssetResponse { Success = false, Message = "Internal path is required" };
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        
        if (!File.Exists(pakPath))
            return new UAssetResponse { Success = false, Message = $"PAK file not found: {pakPath}" };
        
        try
        {
            using var pakReader = new IoStore.PakReader(pakPath, aesKey);
            byte[] data = pakReader.Get(internalPath);
            
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            
            File.WriteAllBytes(outputPath, data);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Extracted {internalPath} ({data.Length} bytes)",
                Data = new Dictionary<string, object?>
                {
                    ["output_path"] = outputPath,
                    ["size"] = data.Length
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to extract file: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Extract all files from a PAK to a directory
    /// </summary>
    private static UAssetResponse ExtractPakAll(string? pakPath, string? outputDir, string? aesKey)
    {
        if (string.IsNullOrEmpty(pakPath))
            return new UAssetResponse { Success = false, Message = "PAK path is required" };
        if (string.IsNullOrEmpty(outputDir))
            return new UAssetResponse { Success = false, Message = "Output directory is required" };
        
        if (!File.Exists(pakPath))
            return new UAssetResponse { Success = false, Message = $"PAK file not found: {pakPath}" };
        
        try
        {
            using var pakReader = new IoStore.PakReader(pakPath, aesKey);
            Directory.CreateDirectory(outputDir);
            
            int extracted = 0;
            int failed = 0;
            var extractedFiles = new List<string>();
            
            foreach (var file in pakReader.Files)
            {
                try
                {
                    byte[] data = pakReader.Get(file);
                    string outPath = Path.Combine(outputDir, file.Replace('/', Path.DirectorySeparatorChar));
                    
                    string? dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    
                    File.WriteAllBytes(outPath, data);
                    extractedFiles.Add(file);
                    extracted++;
                }
                catch
                {
                    failed++;
                }
            }
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Extracted {extracted} files, {failed} failed",
                Data = new Dictionary<string, object?>
                {
                    ["extracted_count"] = extracted,
                    ["failed_count"] = failed,
                    ["output_dir"] = outputDir,
                    ["files"] = extractedFiles
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to extract PAK: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Create a PAK file from a list of files and/or directories.
    /// Each entry in <paramref name="filePaths"/> may be:
    ///   - A directory: walked recursively, paths inside the PAK are relative to that directory.
    ///   - A file: the in-PAK path is auto-detected via ResolveInPakPath
    ///     (uses <paramref name="basePath"/> if provided, else looks for a "Marvel/Content/" anchor,
    ///     else falls back to the bare filename).
    /// </summary>
    private static UAssetResponse CreatePakJson(string? outputPath, List<string>? filePaths, string? mountPoint, ulong pathHashSeed, string? aesKey, string? basePath = null)
    {
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "File paths are required" };

        try
        {
            mountPoint ??= "../../../";

            // Resolve all inputs into (in-pak relative path, absolute path) pairs
            var resolved = new List<(string relPath, string absPath)>();
            string? explicitRoot = string.IsNullOrEmpty(basePath) ? null : Path.GetFullPath(basePath);

            foreach (var input in filePaths)
            {
                // Support explicit "relPath=absPath" mapping (used by the Rust binding to
                // preserve the exact in-PAK path the caller already computed). Split on the
                // first '=' so absolute paths that happen to contain '=' on the rhs still work.
                // Only treat as a mapping when the rhs resolves to an existing file/directory,
                // so raw paths that legitimately contain '=' keep working too.
                int eqIdx = input.IndexOf('=');
                if (eqIdx > 0)
                {
                    string maybeRel = input.Substring(0, eqIdx);
                    string maybeAbs = input.Substring(eqIdx + 1);
                    if (File.Exists(maybeAbs))
                    {
                        string absPath = Path.GetFullPath(maybeAbs);
                        string relPath = maybeRel.Replace('\\', '/');
                        resolved.Add((relPath, absPath));
                        continue;
                    }
                    if (Directory.Exists(maybeAbs))
                    {
                        string baseDir = Path.GetFullPath(maybeAbs);
                        string relRoot = maybeRel.Replace('\\', '/').TrimEnd('/');
                        foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
                        {
                            string absPath = Path.GetFullPath(f);
                            string sub = Path.GetRelativePath(baseDir, absPath).Replace('\\', '/');
                            string relPath = string.IsNullOrEmpty(relRoot) ? sub : $"{relRoot}/{sub}";
                            resolved.Add((relPath, absPath));
                        }
                        continue;
                    }
                }
                if (Directory.Exists(input))
                {
                    string baseDir = Path.GetFullPath(input);
                    foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
                    {
                        string absPath = Path.GetFullPath(f);
                        string relPath = Path.GetRelativePath(baseDir, absPath).Replace('\\', '/');
                        resolved.Add((relPath, absPath));
                    }
                }
                else if (File.Exists(input))
                {
                    string absPath = Path.GetFullPath(input);
                    string relPath = ResolveInPakPath(absPath, explicitRoot);
                    resolved.Add((relPath, absPath));
                }
                else
                {
                    return new UAssetResponse { Success = false, Message = $"File or directory not found: {input}" };
                }
            }

            if (resolved.Count == 0)
                return new UAssetResponse { Success = false, Message = "No files were resolved from the provided paths" };

            using var pakWriter = new IoStore.PakWriter(mountPoint, pathHashSeed, aesKey);
            foreach (var (relPath, absPath) in resolved)
            {
                byte[] data = File.ReadAllBytes(absPath);
                pakWriter.AddEntry(relPath, data);
            }
            pakWriter.Write(outputPath);

            return new UAssetResponse
            {
                Success = true,
                Message = $"Created PAK with {resolved.Count} files",
                Data = new Dictionary<string, object?>
                {
                    ["output_path"] = outputPath,
                    ["file_count"] = resolved.Count,
                    ["mount_point"] = mountPoint,
                    ["entries"] = resolved.Select(r => r.relPath).ToList()
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to create PAK: {ex.Message}" };
        }
    }

    /// <summary>
    /// JSON API equivalent of the list_pak CLI: returns a structured listing of a PAK
    /// without extracting anything. Supports filter patterns.
    /// </summary>
    private static UAssetResponse ListPakJson(string? pakPath, string? aesKey, List<string>? filterPatterns)
    {
        if (string.IsNullOrEmpty(pakPath))
            return new UAssetResponse { Success = false, Message = "PAK path is required" };
        if (!File.Exists(pakPath))
            return new UAssetResponse { Success = false, Message = $"PAK file not found: {pakPath}" };

        try
        {
            using var pakReader = new IoStore.PakReader(pakPath, aesKey);

            var paths = pakReader.Files.ToList();
            if (filterPatterns != null && filterPatterns.Count > 0)
            {
                paths = paths.Where(f =>
                    filterPatterns.Any(p => f.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);

            var entries = new List<Dictionary<string, object?>>(paths.Count);
            ulong totalUncompressed = 0;
            ulong totalCompressed = 0;
            foreach (var p in paths)
            {
                var e = pakReader.GetEntry(p);
                if (e != null)
                {
                    totalUncompressed += e.UncompressedSize;
                    totalCompressed += e.CompressedSize;
                }
                entries.Add(new Dictionary<string, object?>
                {
                    ["path"] = p,
                    ["size"] = e?.UncompressedSize ?? 0,
                    ["compressed_size"] = e?.CompressedSize ?? 0,
                    ["encrypted"] = e?.IsEncrypted ?? false,
                    ["compressed"] = e?.CompressionSlot.HasValue ?? false
                });
            }

            return new UAssetResponse
            {
                Success = true,
                Message = $"Found {entries.Count} files in PAK",
                Data = new Dictionary<string, object?>
                {
                    ["pak_path"] = pakPath,
                    ["version"] = pakReader.Version,
                    ["mount_point"] = pakReader.MountPoint,
                    ["encrypted_index"] = pakReader.EncryptedIndex,
                    ["total_files"] = pakReader.Files.Count,
                    ["filtered_count"] = entries.Count,
                    ["total_uncompressed_bytes"] = totalUncompressed,
                    ["total_compressed_bytes"] = totalCompressed,
                    ["files"] = entries
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to list PAK: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Create a companion PAK file for IoStore bundles (contains chunknames)
    /// </summary>
    private static UAssetResponse CreateCompanionPakJson(string? outputPath, List<string>? filePaths, string? mountPoint, ulong pathHashSeed, string? aesKey)
    {
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "File paths are required" };
        
        try
        {
            mountPoint ??= "../../../";
            
            IoStore.ChunkNamesPakWriter.Create(outputPath, filePaths, mountPoint, pathHashSeed, aesKey);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Created companion PAK listing {filePaths.Count} files",
                Data = new Dictionary<string, object?>
                {
                    ["output_path"] = outputPath,
                    ["file_count"] = filePaths.Count
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to create companion PAK: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// List all packages in an IoStore container
    /// </summary>
    private static UAssetResponse ListIoStoreFiles(string? utocPath, string? aesKeyHex)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "UTOC path is required" };
        
        if (!File.Exists(utocPath))
            return new UAssetResponse { Success = false, Message = $"UTOC file not found: {utocPath}" };
        
        try
        {
            byte[]? aesKey = ParseAesKeyOrDefault(aesKeyHex);
            using var reader = new IoStore.IoStoreReader(utocPath, aesKey);
            
            // Get all chunks and their paths, resolved to Marvel/Content/ format
            var chunks = reader.GetChunks()
                .Select((chunkId, idx) => new { Index = idx, ChunkType = chunkId.ChunkType.ToString(), Path = reader.GetChunkPath(chunkId) })
                .Where(c => c.Path != null)
                .ToList();
            
            var resolvedPaths = chunks.Select(c => ResolveGamePathToContent(c.Path!)).ToList();
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Found {chunks.Count} packages in IoStore",
                Data = new Dictionary<string, object?>
                {
                    ["package_count"] = chunks.Count,
                    ["container_name"] = reader.ContainerName,
                    ["files"] = resolvedPaths
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to read IoStore: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Parse AES key from hex string, or return null for default
    /// </summary>
    private static byte[]? ParseAesKeyOrDefault(string? aesKeyHex)
    {
        if (string.IsNullOrEmpty(aesKeyHex))
            return null; // Use default in IoStoreReader
        
        string hex = aesKeyHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? aesKeyHex[2..] : aesKeyHex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
    
    /// <summary>
    /// Resolve a game path to Marvel/Content/ format for on-disk folders and display.
    /// Handles:
    ///   ../../../Marvel/Content/X  → Marvel/Content/X
    ///   /Game/X                    → Marvel/Content/X
    ///   Marvel/Content/X           → Marvel/Content/X (no change)
    /// </summary>
    private static string ResolveGamePathToContent(string path)
    {
        // Strip leading ../../../ (mount point prefix)
        string p = path;
        while (p.StartsWith("../"))
            p = p.Substring(3);
        
        // Convert /Game/X → Marvel/Content/X
        if (p.StartsWith("/Game/"))
            p = "Marvel/Content" + p.Substring(5);
        else if (p.StartsWith("Game/"))
            p = "Marvel/Content/" + p.Substring(5);
        
        // Strip leading /
        p = p.TrimStart('/');
        
        return p;
    }

    /// <summary>
    /// Check if an IoStore container is compressed
    /// </summary>
    private static UAssetResponse IsIoStoreCompressed(string? utocPath)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "UTOC path is required" };
        
        if (!File.Exists(utocPath))
            return new UAssetResponse { Success = false, Message = $"UTOC file not found: {utocPath}" };
        
        try
        {
            bool isCompressed = IoStore.IoStoreReader.IsCompressed(utocPath);
            return new UAssetResponse
            {
                Success = true,
                Message = isCompressed ? "IoStore is compressed" : "IoStore is not compressed",
                Data = new Dictionary<string, object?> { ["compressed"] = isCompressed }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to check compression: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Check if an IoStore container is encrypted (obfuscated)
    /// </summary>
    private static UAssetResponse IsIoStoreEncrypted(string? utocPath)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "UTOC path is required" };
        
        if (!File.Exists(utocPath))
            return new UAssetResponse { Success = false, Message = $"UTOC file not found: {utocPath}" };
        
        try
        {
            bool isEncrypted = IoStore.IoStoreReader.IsEncrypted(utocPath);
            return new UAssetResponse
            {
                Success = true,
                Message = isEncrypted ? "IoStore is encrypted" : "IoStore is not encrypted",
                Data = new Dictionary<string, object?> { ["encrypted"] = isEncrypted }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to check encryption: {ex.Message}" };
        }
    }

    /// <summary>
    /// Recompress an IoStore container with Oodle
    /// </summary>
    private static UAssetResponse RecompressIoStore(string? utocPath)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "UTOC path is required" };
        
        if (!File.Exists(utocPath))
            return new UAssetResponse { Success = false, Message = $"UTOC file not found: {utocPath}" };
        
        try
        {
            // Check if already compressed
            if (IoStore.IoStoreReader.IsCompressed(utocPath))
            {
                return new UAssetResponse
                {
                    Success = true,
                    Message = "IoStore is already compressed",
                    Data = new Dictionary<string, object?> { ["already_compressed"] = true }
                };
            }
            
            string result = IoStore.IoStoreRecompressor.Recompress(utocPath);
            return new UAssetResponse
            {
                Success = true,
                Message = $"Recompressed IoStore: {result}",
                Data = new Dictionary<string, object?> { ["output_path"] = result }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to recompress: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Extract IoStore to legacy format
    /// </summary>
    private static UAssetResponse ExtractIoStoreJson(string? utocPath, string? outputDir, string? aesKeyHex)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "UTOC path is required" };
        if (string.IsNullOrEmpty(outputDir))
            return new UAssetResponse { Success = false, Message = "Output directory is required" };
        
        if (!File.Exists(utocPath))
            return new UAssetResponse { Success = false, Message = $"UTOC file not found: {utocPath}" };
        
        try
        {
            Directory.CreateDirectory(outputDir);
            
            // Create package context for proper Zen-to-Legacy conversion
            using var context = new ZenPackage.FZenPackageContext();
            
            // Set AES key
            string aesKey = string.IsNullOrEmpty(aesKeyHex) 
                ? "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74" 
                : aesKeyHex;
            context.SetAesKey(aesKey);
            
            // Check if this is a mod in ~mods folder - need to load game containers for import resolution
            string? paksDir = null;
            string utocDir = Path.GetDirectoryName(utocPath) ?? "";
            
            // Walk up to find ~mods folder and get base paks directory
            string? current = utocDir;
            while (!string.IsNullOrEmpty(current))
            {
                string dirName = Path.GetFileName(current) ?? "";
                if (dirName.Equals("~mods", StringComparison.OrdinalIgnoreCase))
                {
                    paksDir = Path.GetDirectoryName(current);
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
            
            // If we found a paks directory, load game containers first for import resolution
            if (!string.IsNullOrEmpty(paksDir) && Directory.Exists(paksDir))
            {
                // Load global.utoc first for script objects
                string globalPath = Path.Combine(paksDir, "global.utoc");
                if (File.Exists(globalPath))
                {
                    context.LoadContainer(globalPath);
                    context.LoadScriptObjectsFromContainer(0);
                }
                
                // Load other game containers
                var gameUtocs = Directory.GetFiles(paksDir, "*.utoc", SearchOption.TopDirectoryOnly)
                    .Where(f => !f.EndsWith("global.utoc", StringComparison.OrdinalIgnoreCase))
                    .Where(f => !f.Contains("optional", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f)
                    .ToList();
                
                foreach (var gameUtoc in gameUtocs)
                {
                    try { context.LoadContainer(gameUtoc); }
                    catch { /* Skip failed containers */ }
                }
            }
            
            // Load the target mod container with priority so it overrides game packages
            int modContainerIndex = context.ContainerCount;
            context.LoadContainerWithPriority(utocPath);
            
            int converted = 0;
            int failed = 0;
            var extractedFiles = new List<string>();
            
            // Get all packages from the mod container only
            var packageIds = context.GetPackageIdsFromContainer(modContainerIndex);
            
            foreach (var packageId in packageIds)
            {
                string? fullPath = context.GetPackagePath(packageId);
                var cached = context.GetCachedPackage(packageId);
                if (cached == null) continue;
                
                string packageName = !string.IsNullOrEmpty(fullPath) ? fullPath : cached.Header.PackageName();
                
                try
                {
                    // Convert to legacy format using ZenToLegacyConverter
                    var converter = new ZenPackage.ZenToLegacyConverter(context, packageId);
                    var legacyBundle = converter.Convert();
                    
                    // Normalize path for output
                    string relPath = packageName;
                    
                    // Resolve /../ patterns
                    if (relPath.Contains("/../"))
                    {
                        string tempRoot = Path.GetTempPath();
                        string tempPath = Path.Combine(tempRoot, relPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        string resolved = Path.GetFullPath(tempPath);
                        relPath = resolved.Substring(tempRoot.Length).Replace(Path.DirectorySeparatorChar, '/');
                        if (!relPath.StartsWith("/"))
                            relPath = "/" + relPath;
                        int contentIdx = relPath.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                        if (contentIdx >= 0)
                            relPath = "/Game" + relPath.Substring(contentIdx + "/Content".Length);
                    }
                    
                    // Resolve /Game/ to Marvel/Content/ for on-disk folder structure
                    relPath = ResolveGamePathToContent(relPath);
                    
                    relPath = relPath.Replace('/', Path.DirectorySeparatorChar);
                    if (!relPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                        relPath += ".uasset";
                    
                    string outputAssetPath = Path.Combine(outputDir, relPath);
                    string? outputAssetDir = Path.GetDirectoryName(outputAssetPath);
                    if (!string.IsNullOrEmpty(outputAssetDir))
                        Directory.CreateDirectory(outputAssetDir);
                    
                    // Write .uasset
                    File.WriteAllBytes(outputAssetPath, legacyBundle.AssetData);
                    extractedFiles.Add(relPath);
                    
                    // Write .uexp
                    string outputUexpPath = Path.ChangeExtension(outputAssetPath, ".uexp");
                    File.WriteAllBytes(outputUexpPath, legacyBundle.ExportsData);
                    extractedFiles.Add(Path.ChangeExtension(relPath, ".uexp"));
                    
                    // Write .ubulk if present
                    if (legacyBundle.BulkData != null && legacyBundle.BulkData.Length > 0)
                    {
                        string outputBulkPath = Path.ChangeExtension(outputAssetPath, ".ubulk");
                        File.WriteAllBytes(outputBulkPath, legacyBundle.BulkData);
                        extractedFiles.Add(Path.ChangeExtension(relPath, ".ubulk"));
                    }
                    
                    // Write .uptnl if present
                    if (legacyBundle.OptionalBulkData != null && legacyBundle.OptionalBulkData.Length > 0)
                    {
                        string outputUptnlPath = Path.ChangeExtension(outputAssetPath, ".uptnl");
                        File.WriteAllBytes(outputUptnlPath, legacyBundle.OptionalBulkData);
                        extractedFiles.Add(Path.ChangeExtension(relPath, ".uptnl"));
                    }
                    
                    // Write .m.ubulk if present
                    if (legacyBundle.MemoryMappedBulkData != null && legacyBundle.MemoryMappedBulkData.Length > 0)
                    {
                        string outputMBulkPath = Path.ChangeExtension(outputAssetPath, ".m.ubulk");
                        File.WriteAllBytes(outputMBulkPath, legacyBundle.MemoryMappedBulkData);
                        extractedFiles.Add(Path.ChangeExtension(relPath, ".m.ubulk"));
                    }
                    
                    converted++;
                }
                catch
                {
                    failed++;
                }
            }
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Extracted {converted} packages from IoStore ({failed} failed)",
                Data = new Dictionary<string, object?>
                {
                    ["extracted_count"] = converted,
                    ["failed_count"] = failed,
                    ["output_dir"] = outputDir,
                    ["files"] = extractedFiles
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to extract IoStore: {ex.Message}" };
        }
    }

    /// <summary>
    /// Extract IoStore to legacy format with game paks context (VFX pipeline compatible)
    /// JSON API equivalent of CliExtractIoStoreLegacy
    /// </summary>
    private static UAssetResponse ExtractIoStoreLegacyJson(string? gamePaks, string? modPath, string? outputPath, List<string>? filterPatterns, string? aesKeyHex, bool withDeps)
    {
        if (string.IsNullOrEmpty(gamePaks))
            return new UAssetResponse { Success = false, Message = "game_paks is required" };
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "output_path is required" };

        if (!Directory.Exists(gamePaks))
            return new UAssetResponse { Success = false, Message = $"Game Paks directory not found: {gamePaks}" };

        try
        {
            Directory.CreateDirectory(outputPath);

            // Create package context for proper import resolution
            using var context = new ZenPackage.FZenPackageContext();

            // Set AES key - use provided key or default Marvel Rivals key
            string aesKey = string.IsNullOrEmpty(aesKeyHex)
                ? "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74"
                : aesKeyHex;
            context.SetAesKey(aesKey);

            Console.Error.WriteLine($"[ExtractIoStoreLegacy] Loading game containers from: {gamePaks}");

            // Load global.utoc first for script objects
            string globalPath = Path.Combine(gamePaks, "global.utoc");
            if (File.Exists(globalPath))
            {
                Console.Error.WriteLine("[ExtractIoStoreLegacy] Loading global.utoc...");
                context.LoadContainer(globalPath);
                context.LoadScriptObjectsFromContainer(0);
            }

            // Load other game containers
            var utocFiles = Directory.GetFiles(gamePaks, "*.utoc", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith("global.utoc", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            Console.Error.WriteLine($"[ExtractIoStoreLegacy] Loading {utocFiles.Count} game containers...");
            foreach (var utocFile in utocFiles)
            {
                try { context.LoadContainer(utocFile); }
                catch (Exception ex) { Console.Error.WriteLine($"  Warning: Failed to load {Path.GetFileName(utocFile)}: {ex.Message}"); }
            }

            // Load mod containers if specified
            int modContainerIndex = -1;
            if (!string.IsNullOrEmpty(modPath))
            {
                if (File.Exists(modPath) && modPath.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"[ExtractIoStoreLegacy] Loading mod container: {modPath}");
                    modContainerIndex = context.ContainerCount;
                    context.LoadContainerWithPriority(modPath);
                }
                else if (Directory.Exists(modPath))
                {
                    var modUtocs = Directory.GetFiles(modPath, "*.utoc", SearchOption.TopDirectoryOnly);
                    foreach (var modUtoc in modUtocs)
                    {
                        Console.Error.WriteLine($"[ExtractIoStoreLegacy] Loading mod container: {modUtoc}");
                        modContainerIndex = context.ContainerCount;
                        context.LoadContainerWithPriority(modUtoc);
                    }
                }
            }

            // Determine which packages to extract
            var packagesToExtract = new List<ulong>();

            if (modContainerIndex >= 0)
            {
                // Get packages from mod container
                var modPackageIds = context.GetPackageIdsFromContainer(modContainerIndex);
                packagesToExtract.AddRange(modPackageIds);
                Console.Error.WriteLine($"[ExtractIoStoreLegacy] Found {modPackageIds.Count()} packages in mod container");
            }
            else
            {
                // Get all packages from all containers
                for (int i = 0; i < context.ContainerCount; i++)
                {
                    var packageIds = context.GetPackageIdsFromContainer(i);
                    packagesToExtract.AddRange(packageIds);
                }
            }

            // Apply filter patterns if specified
            if (filterPatterns != null && filterPatterns.Count > 0)
            {
                var filtered = new List<ulong>();
                foreach (var packageId in packagesToExtract)
                {
                    string? path = context.GetPackagePath(packageId);
                    if (string.IsNullOrEmpty(path)) continue;

                    // Check if path matches any filter pattern
                    bool matches = filterPatterns.Any(pattern =>
                        path.Contains(pattern, StringComparison.OrdinalIgnoreCase));

                    if (matches) filtered.Add(packageId);
                }
                packagesToExtract = filtered;
                Console.Error.WriteLine($"[ExtractIoStoreLegacy] Filtered to {packagesToExtract.Count()} packages matching patterns");
            }

            // Extract packages
            int converted = 0;
            int failed = 0;
            var extractedFiles = new List<string>();

            foreach (var packageId in packagesToExtract)
            {
                string? fullPath = context.GetPackagePath(packageId);
                var cached = context.GetCachedPackage(packageId);
                if (cached == null) continue;

                string packageName = !string.IsNullOrEmpty(fullPath) ? fullPath : cached.Header.PackageName();

                try
                {
                    var converter = new ZenPackage.ZenToLegacyConverter(context, packageId);
                    var legacyBundle = converter.Convert();

                    // Resolve path and write files (same logic as ExtractIoStoreJson)
                    string relPath = packageName;
                    if (relPath.Contains("/../"))
                    {
                        string tempRoot = Path.GetTempPath();
                        string tempPath = Path.Combine(tempRoot, relPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        string resolved = Path.GetFullPath(tempPath);
                        relPath = resolved.Substring(tempRoot.Length).Replace(Path.DirectorySeparatorChar, '/');
                        if (!relPath.StartsWith("/"))
                            relPath = "/" + relPath;
                        int contentIdx = relPath.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                        if (contentIdx >= 0)
                            relPath = "/Game" + relPath.Substring(contentIdx + "/Content".Length);
                    }

                    relPath = ResolveGamePathToContent(relPath);
                    relPath = relPath.Replace('/', Path.DirectorySeparatorChar);
                    if (!relPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                        relPath += ".uasset";

                    string outputAssetPath = Path.Combine(outputPath, relPath);
                    string? outputAssetDir = Path.GetDirectoryName(outputAssetPath);
                    if (!string.IsNullOrEmpty(outputAssetDir))
                        Directory.CreateDirectory(outputAssetDir);

                    File.WriteAllBytes(outputAssetPath, legacyBundle.AssetData);
                    extractedFiles.Add(relPath);

                    string outputUexpPath = Path.ChangeExtension(outputAssetPath, ".uexp");
                    File.WriteAllBytes(outputUexpPath, legacyBundle.ExportsData);
                    extractedFiles.Add(Path.ChangeExtension(relPath, ".uexp"));

                    if (legacyBundle.BulkData != null && legacyBundle.BulkData.Length > 0)
                    {
                        string outputBulkPath = Path.ChangeExtension(outputAssetPath, ".ubulk");
                        File.WriteAllBytes(outputBulkPath, legacyBundle.BulkData);
                        extractedFiles.Add(Path.ChangeExtension(relPath, ".ubulk"));
                    }

                    if (legacyBundle.OptionalBulkData != null && legacyBundle.OptionalBulkData.Length > 0)
                    {
                        string outputUptnlPath = Path.ChangeExtension(outputAssetPath, ".uptnl");
                        File.WriteAllBytes(outputUptnlPath, legacyBundle.OptionalBulkData);
                        extractedFiles.Add(Path.ChangeExtension(relPath, ".uptnl"));
                    }

                    if (legacyBundle.MemoryMappedBulkData != null && legacyBundle.MemoryMappedBulkData.Length > 0)
                    {
                        string outputMBulkPath = Path.ChangeExtension(outputAssetPath, ".m.ubulk");
                        File.WriteAllBytes(outputMBulkPath, legacyBundle.MemoryMappedBulkData);
                        extractedFiles.Add(Path.ChangeExtension(relPath, ".m.ubulk"));
                    }

                    converted++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ExtractIoStoreLegacy] Failed to convert {packageName}: {ex.Message}");
                    failed++;
                }
            }

            return new UAssetResponse
            {
                Success = true,
                Message = $"Extracted {converted} packages from IoStore ({failed} failed)",
                Data = new Dictionary<string, object?>
                {
                    ["extracted_count"] = converted,
                    ["failed_count"] = failed,
                    ["output_dir"] = outputPath,
                    ["files"] = extractedFiles
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to extract IoStore (legacy): {ex.Message}" };
        }
    }

    /// <summary>
    /// Extract ScriptObjects.bin from game paks
    /// </summary>
    private static UAssetResponse ExtractScriptObjectsJson(string? paksPath, string? outputPath)
    {
        if (string.IsNullOrEmpty(paksPath))
            return new UAssetResponse { Success = false, Message = "Paks path is required" };
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        
        if (!Directory.Exists(paksPath))
            return new UAssetResponse { Success = false, Message = $"Paks directory not found: {paksPath}" };
        
        try
        {
            byte[]? data = IoStore.IoStoreReader.ExtractScriptObjects(paksPath);
            if (data == null)
            {
                return new UAssetResponse { Success = false, Message = "ScriptObjects not found in any IoStore container" };
            }
            
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            
            File.WriteAllBytes(outputPath, data);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Extracted ScriptObjects.bin ({data.Length} bytes)",
                Data = new Dictionary<string, object?>
                {
                    ["output_path"] = outputPath,
                    ["size"] = data.Length
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to extract ScriptObjects: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Create a mod IoStore bundle from a directory of legacy assets.
    /// This is the JSON API equivalent of retoc's action_to_zen.
    /// Converts .uasset/.uexp files to Zen format and creates .utoc/.ucas/.pak bundle.
    /// Supports direct .pak file input (input_pak) as an alternative to input_dir.
    /// </summary>
    private static UAssetResponse CreateModIoStoreJson(string? outputPath, string? inputDir, string? inputPak, string? mountPoint, bool compress, string? aesKey, bool parallel, bool obfuscate, bool hybrid = false)
    {
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };

        // Marvel Rivals AES key for obfuscation
        const string MARVEL_RIVALS_AES_KEY = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";

        // Separate keys: input PAK reading vs output IoStore encryption.
        // The caller may provide aesKey purely to read an encrypted input PAK, even when
        // they don't want the output IoStore to be encrypted.
        string? inputPakAesKey = aesKey;  // Used only to read input PAK
        string? outputEncryptionKey = obfuscate ? MARVEL_RIVALS_AES_KEY : null;  // Used to encrypt output

        string? effectiveInputDir = inputDir;
        string? tempExtractDir = null;

        // Handle PAK file input - extract to temp directory first
        if (!string.IsNullOrEmpty(inputPak))
        {
            if (!File.Exists(inputPak))
                return new UAssetResponse { Success = false, Message = $"Input PAK file not found: {inputPak}" };

            tempExtractDir = Path.Combine(Path.GetTempPath(), "UAssetTool_pak_" + Path.GetFileNameWithoutExtension(inputPak) + "_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempExtractDir);

            Console.Error.WriteLine($"[CreateModIoStore] Extracting PAK file: {inputPak}");
            Console.Error.WriteLine($"[CreateModIoStore] Temp extraction dir: {tempExtractDir}");

            try
            {
                // PakReader takes AES key as string (hex), not byte array.
                // Use inputPakAesKey (separate from output encryption decision).
                using var pakReader = new IoStore.PakReader(inputPak, inputPakAesKey);
                int extracted = 0;
                foreach (var file in pakReader.Files)
                {
                    try
                    {
                        // Skip non-asset files — UNLESS hybrid mode is on, in which case we extract
                        // everything so non-Unreal files can be embedded into the companion PAK.
                        if (!hybrid &&
                            !file.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                            !file.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase) &&
                            !file.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase) &&
                            !file.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase))
                            continue;

                        byte[] data = pakReader.Get(file);
                        // CRITICAL: TrimStart('/') — PakReader keys begin with '/', and Path.Combine
                        // treats a rooted second arg as absolute and DROPS tempExtractDir.
                        // Without this trim, files write to the drive root (or fail), and the
                        // later Directory.GetFiles in tempExtractDir finds nothing — leading to
                        // a silent "Failed to create mod IoStore" further down. This matches
                        // CliCreateModIoStore's behavior at line ~610.
                        string relativePath = file.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                        string destPath = Path.Combine(tempExtractDir, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        File.WriteAllBytes(destPath, data);
                        extracted++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[CreateModIoStore] Failed to extract {file}: {ex.Message}");
                    }
                }

                Console.Error.WriteLine($"[CreateModIoStore] Extracted {extracted} files from PAK");
                effectiveInputDir = tempExtractDir;
            }
            catch (Exception ex)
            {
                // Cleanup temp dir on failure
                try { if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true); } catch { }
                return new UAssetResponse { Success = false, Message = $"Failed to extract PAK file: {ex.Message}" };
            }
        }

        if (string.IsNullOrEmpty(effectiveInputDir))
            return new UAssetResponse { Success = false, Message = "Either input_dir or input_pak is required" };

        if (!Directory.Exists(effectiveInputDir))
            return new UAssetResponse { Success = false, Message = $"Input directory not found: {effectiveInputDir}" };
        
        try
        {
            // Collect all uasset files
            var uassetFiles = Directory.GetFiles(effectiveInputDir, "*.uasset", SearchOption.AllDirectories).ToList();

            // Collect shader bytecode files
            var shaderFiles = Directory.GetFiles(effectiveInputDir, "*.ushaderbytecode", SearchOption.AllDirectories).ToList();

            // Hybrid mode: collect every non-Unreal-family file to embed raw into the companion PAK.
            // Each entry is (in-PAK relative path, absolute path on disk).
            var rawInputFiles = new List<(string inPakPath, string absPath)>();
            if (hybrid)
            {
                foreach (var f in Directory.EnumerateFiles(effectiveInputDir, "*", SearchOption.AllDirectories))
                {
                    if (IsUnrealFamilyFile(f)) continue;
                    string rel = Path.GetRelativePath(effectiveInputDir, f).Replace('\\', '/');
                    rawInputFiles.Add((rel, Path.GetFullPath(f)));
                }
                Console.Error.WriteLine($"[CreateModIoStore] Hybrid mode: found {rawInputFiles.Count} raw (non-Unreal) file(s) for companion PAK");
            }

            if (uassetFiles.Count == 0 && shaderFiles.Count == 0 && rawInputFiles.Count == 0)
            {
                // Cleanup temp dir if we created one
                if (!string.IsNullOrEmpty(tempExtractDir) && Directory.Exists(tempExtractDir))
                    try { Directory.Delete(tempExtractDir, true); } catch { }
                return new UAssetResponse { Success = false, Message = "No .uasset or .ushaderbytecode files found in input directory" };
            }
            
            if (shaderFiles.Count > 0)
                Console.Error.WriteLine($"[CreateModIoStore] Found {shaderFiles.Count} shader library file(s)");
            
            // Determine output paths
            string outputBase = outputPath.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) ? outputPath.Substring(0, outputPath.Length - 5) : outputPath;
            string utocPath = outputBase + ".utoc";
            string pakPath = outputBase + ".pak";
            string mount = string.IsNullOrEmpty(mountPoint) ? "../../../" : mountPoint;
            
            // Thread count based on parallel flag: 75% when enabled, 50% otherwise
            int threadCount = parallel 
                ? Math.Max(1, (Environment.ProcessorCount * 3) / 4)  // 75% of cores
                : Math.Max(1, Environment.ProcessorCount / 2);       // 50% of cores
            Console.Error.WriteLine($"[CreateModIoStore] Processing {uassetFiles.Count} files using {threadCount} threads (parallel={parallel})...");
            Console.Error.Flush();
            
            // Phase 1: Parallel conversion to Zen format (CPU-intensive)
            var conversionResults = new System.Collections.Concurrent.ConcurrentBag<(string assetName, string uassetPath, byte[]? zenData, string packagePath, ZenPackage.FZenPackage? zenPackage, byte[]? ubulkData, byte[]? uptnlData, byte[]? mubulkData, string? error)>();
            int processedCount = 0;
            
            Parallel.ForEach(uassetFiles, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, uassetPath =>
            {
                string assetName = Path.GetFileNameWithoutExtension(uassetPath);
                try
                {
                    var (zenData, packagePath, zenPackage) = ZenPackage.ZenConverter.ConvertLegacyToZenFull(
                        uassetPath, containerVersion: ZenPackage.EIoContainerHeaderVersion.NoExportInfo);
                    
                    byte[]? ubulkData = null;
                    string ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
                    if (File.Exists(ubulkPath)) ubulkData = File.ReadAllBytes(ubulkPath);

                    byte[]? uptnlData = null;
                    string uptnlPath = Path.ChangeExtension(uassetPath, ".uptnl");
                    if (File.Exists(uptnlPath)) uptnlData = File.ReadAllBytes(uptnlPath);

                    byte[]? mubulkData = null;
                    string mubulkPath = Path.ChangeExtension(uassetPath, ".m.ubulk");
                    if (File.Exists(mubulkPath)) mubulkData = File.ReadAllBytes(mubulkPath);
                    
                    conversionResults.Add((assetName, uassetPath, zenData, packagePath, zenPackage, ubulkData, uptnlData, mubulkData, null));
                    
                    int count = Interlocked.Increment(ref processedCount);
                    if (count % 50 == 0 || count == uassetFiles.Count)
                    {
                        Console.Error.WriteLine($"[CreateModIoStore] Converted {count}/{uassetFiles.Count}...");
                        Console.Error.Flush();
                    }
                }
                catch (Exception ex)
                {
                    conversionResults.Add((assetName, uassetPath, null, "", null, null, null, null, ex.Message));
                    Interlocked.Increment(ref processedCount);
                }
            });
            
            Console.Error.WriteLine($"[CreateModIoStore] Parallel conversion done. Writing IoStore...");
            Console.Error.Flush();
            
            // Phase 2: Sequential write to IoStore.
            // Only encrypt the output IoStore when obfuscation is explicitly requested.
            // Do NOT encrypt just because aesKey was passed (it may be for reading input PAK only).
            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                mount,
                compress,
                obfuscate,
                outputEncryptionKey);
            
            var filePaths = new List<string>();
            int converted = 0;
            var errors = new List<string>();
            
            foreach (var result in conversionResults)
            {
                if (result.error != null || result.zenData == null)
                {
                    if (result.error != null) errors.Add($"{result.assetName}: {result.error}");
                    continue;
                }
                
                try
                {
                    var (assetName, uassetPath, zenData, packagePath, zenPackage, ubulkData, uptnlData, mubulkData, _) = result;
                    
                    // Create package ID using the /Game/... format
                    string gamePackagePath;
                    if (packagePath.StartsWith("Marvel/Content/"))
                    {
                        gamePackagePath = "/Game/" + packagePath.Substring("Marvel/Content/".Length);
                    }
                    else
                    {
                        gamePackagePath = "/" + packagePath;
                    }
                    
                    var packageId = IoStore.FPackageId.FromName(gamePackagePath);
                    var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);
                    
                    // Create store entry with imported packages
                    var storeEntry = new IoStore.StoreEntry
                    {
                        ExportCount = zenPackage.ExportMap.Count,
                        ExportBundleCount = 1,
                        LoadOrder = 0
                    };
                    
                    foreach (ulong importedPkgId in zenPackage.ImportedPackages)
                    {
                        storeEntry.ImportedPackages.Add(new IoStore.FPackageId(importedPkgId));
                    }
                    
                    // Write to IoStore
                    string fullPath = mount + packagePath + ".uasset";
                    ioStoreWriter.WritePackageChunk(chunkId, fullPath, zenData, storeEntry);
                    
                    // Add to chunknames
                    filePaths.Add(packagePath + ".uasset");
                    filePaths.Add(packagePath + ".uexp");
                    
                    // Handle .ubulk if exists (already loaded during parallel phase)
                    if (ubulkData != null)
                    {
                        var bulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.BulkData);
                        string bulkFullPath = mount + packagePath + ".ubulk";
                        ioStoreWriter.WriteChunk(bulkChunkId, bulkFullPath, ubulkData);
                        filePaths.Add(packagePath + ".ubulk");
                    }

                    // Handle .uptnl if exists
                    if (uptnlData != null)
                    {
                        var optBulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.OptionalBulkData);
                        string optBulkFullPath = mount + packagePath + ".uptnl";
                        ioStoreWriter.WriteChunk(optBulkChunkId, optBulkFullPath, uptnlData);
                        filePaths.Add(packagePath + ".uptnl");
                    }

                    // Handle .m.ubulk if exists
                    if (mubulkData != null)
                    {
                        var memBulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.MemoryMappedBulkData);
                        string memBulkFullPath = mount + packagePath + ".m.ubulk";
                        ioStoreWriter.WriteChunk(memBulkChunkId, memBulkFullPath, mubulkData);
                        filePaths.Add(packagePath + ".m.ubulk");
                    }
                    
                    converted++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{result.assetName}: {ex.Message}");
                }
            }
            
            // Phase 3: Process shader library files
            int shaderLibsConverted = 0;
            foreach (var shaderFile in shaderFiles)
            {
                try
                {
                    byte[] shaderData = File.ReadAllBytes(shaderFile);
                    
                    // Determine the UE package path from the file's relative path within the input directory
                    string relativePath = Path.GetRelativePath(effectiveInputDir, shaderFile).Replace('\\', '/');
                    // Strip any leading "Marvel/Content/" prefix if already present, otherwise prefix with mount-relative path
                    string shaderLibPath;
                    if (relativePath.StartsWith("Marvel/Content/", StringComparison.OrdinalIgnoreCase))
                        shaderLibPath = mount + relativePath;
                    else
                        shaderLibPath = mount + "Marvel/Content/" + relativePath;
                    
                    IoStore.ShaderLibraryConverter.ConvertAndWrite(shaderData, shaderLibPath, ioStoreWriter);
                    
                    // Add shader library path to chunknames (without extension, matching retoc behavior)
                    string relNoExt = relativePath;
                    if (relNoExt.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase))
                        relNoExt = relNoExt.Substring(0, relNoExt.Length - ".ushaderbytecode".Length);
                    filePaths.Add(relNoExt + ".ushaderbytecode");
                    
                    shaderLibsConverted++;
                    Console.Error.WriteLine($"[CreateModIoStore] Converted shader library: {Path.GetFileName(shaderFile)}");
                    Console.Error.Flush();
                }
                catch (Exception ex)
                {
                    errors.Add($"Shader:{Path.GetFileName(shaderFile)}: {ex.Message}");
                    Console.Error.WriteLine($"[CreateModIoStore] Error converting shader library {Path.GetFileName(shaderFile)}: {ex.Message}");
                }
            }
            
            if (converted == 0 && shaderLibsConverted == 0 && rawInputFiles.Count == 0)
            {
                // Clean up empty files left by IoStoreWriter constructor
                ioStoreWriter.Dispose();
                try { File.Delete(utocPath); } catch { }
                try { File.Delete(Path.ChangeExtension(utocPath, ".ucas")); } catch { }
                // Cleanup temp dir if we created one
                if (!string.IsNullOrEmpty(tempExtractDir) && Directory.Exists(tempExtractDir))
                    try { Directory.Delete(tempExtractDir, true); } catch { }
                return new UAssetResponse { Success = false, Message = $"No assets were converted. Errors: {string.Join("; ", errors)}" };
            }

            ioStoreWriter.Complete();

            // Read hybrid raw files into memory before temp-dir cleanup so they can be embedded.
            var rawEntries = new List<(string inPakPath, byte[] data)>();
            foreach (var (inPakPath, absPath) in rawInputFiles)
            {
                try
                {
                    rawEntries.Add((inPakPath, File.ReadAllBytes(absPath)));
                }
                catch (Exception ex)
                {
                    errors.Add($"Raw:{inPakPath}: {ex.Message}");
                    Console.Error.WriteLine($"[CreateModIoStore] Error reading raw file {inPakPath}: {ex.Message}");
                }
            }

            // Create companion PAK with chunknames (and any hybrid raw files).
            IoStore.ChunkNamesPakWriter.Create(pakPath, filePaths, mount, 0, aesKey, rawEntries.Count > 0 ? rawEntries : null);
            
            // Resolve file paths to Marvel/Content/ format for the app
            var resolvedFilePaths = filePaths.Select(p => ResolveGamePathToContent(p)).ToList();
            
            // Cleanup temp extraction directory if we created one
            if (!string.IsNullOrEmpty(tempExtractDir) && Directory.Exists(tempExtractDir))
            {
                try
                {
                    Directory.Delete(tempExtractDir, true);
                    Console.Error.WriteLine($"[CreateModIoStore] Cleaned up temp directory: {tempExtractDir}");
                }
                catch (Exception cleanupEx)
                {
                    Console.Error.WriteLine($"[CreateModIoStore] Warning: Failed to cleanup temp directory: {cleanupEx.Message}");
                }
            }

            return new UAssetResponse
            {
                Success = true,
                Message = $"Created IoStore mod bundle with {converted} assets"
                    + (shaderLibsConverted > 0 ? $" and {shaderLibsConverted} shader library(ies)" : "")
                    + (rawEntries.Count > 0 ? $" and {rawEntries.Count} raw file(s) in companion PAK" : ""),
                Data = new Dictionary<string, object?>
                {
                    ["utoc_path"] = utocPath,
                    ["ucas_path"] = Path.ChangeExtension(utocPath, ".ucas"),
                    ["pak_path"] = pakPath,
                    ["converted_count"] = converted,
                    ["file_count"] = filePaths.Count,
                    ["raw_file_count"] = rawEntries.Count,
                    ["file_paths"] = resolvedFilePaths,
                    ["errors"] = errors.Count > 0 ? errors : null
                }
            };
        }
        catch (Exception ex)
        {
            // Cleanup temp dir on any exception
            if (!string.IsNullOrEmpty(tempExtractDir) && Directory.Exists(tempExtractDir))
                try { Directory.Delete(tempExtractDir, true); } catch { }

            // Build a detailed error message including inner exceptions so callers can diagnose silent failures
            var sb = new System.Text.StringBuilder();
            sb.Append("Failed to create mod IoStore: ");
            sb.Append(string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message);
            var inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth < 5)
            {
                sb.Append(" | inner: ");
                sb.Append(string.IsNullOrEmpty(inner.Message) ? inner.GetType().Name : inner.Message);
                inner = inner.InnerException;
                depth++;
            }
            // Also log full stack trace to stderr to aid debugging
            Console.Error.WriteLine($"[CreateModIoStore] EXCEPTION: {ex}");
            return new UAssetResponse { Success = false, Message = sb.ToString() };
        }
    }
    
    /// <summary>
    /// Create an IoStore bundle from a directory of legacy assets
    /// </summary>
    private static UAssetResponse CreateIoStoreJson(string? outputPath, string? inputDir, bool compress, string? aesKey)
    {
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        if (string.IsNullOrEmpty(inputDir))
            return new UAssetResponse { Success = false, Message = "Input directory is required" };
        
        if (!Directory.Exists(inputDir))
            return new UAssetResponse { Success = false, Message = $"Input directory not found: {inputDir}" };
        
        try
        {
            // Collect all uasset files
            var uassetFiles = Directory.GetFiles(inputDir, "*.uasset", SearchOption.AllDirectories).ToList();
            
            if (uassetFiles.Count == 0)
                return new UAssetResponse { Success = false, Message = "No .uasset files found in input directory" };
            
            string utocPath = outputPath.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) ? outputPath : outputPath + ".utoc";
            string pakPath = Path.ChangeExtension(utocPath, ".pak");
            
            // Convert each asset to Zen format and write to IoStore
            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                "../../../",
                compress,
                false,
                aesKey);
            
            var filePaths = new List<string>();
            int converted = 0;
            
            foreach (var uassetPath in uassetFiles)
            {
                try
                {
                    byte[] zenData = ZenPackage.ZenConverter.ConvertLegacyToZen(uassetPath);
                    
                    // Get relative path for the package
                    string relativePath = Path.GetRelativePath(inputDir, uassetPath);
                    string packagePath = "/" + Path.ChangeExtension(relativePath, null).Replace('\\', '/');
                    
                    // Create chunk ID from package path and write
                    var packageId = IoStore.FPackageId.FromName(packagePath);
                    var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);
                    ioStoreWriter.WriteChunk(chunkId, packagePath, zenData);
                    
                    filePaths.Add(relativePath.Replace('\\', '/'));
                    converted++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CreateIoStore] Failed to convert {uassetPath}: {ex.Message}");
                }
            }
            
            ioStoreWriter.Complete();
            
            // Create companion PAK
            IoStore.ChunkNamesPakWriter.Create(pakPath, filePaths, "../../../", 0, aesKey);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Created IoStore bundle with {converted} assets",
                Data = new Dictionary<string, object?>
                {
                    ["utoc_path"] = utocPath,
                    ["ucas_path"] = Path.ChangeExtension(utocPath, ".ucas"),
                    ["pak_path"] = pakPath,
                    ["asset_count"] = converted,
                    ["files"] = filePaths
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to create IoStore: {ex.Message}" };
        }
    }
    
    #endregion
}

#region Request/Response Models

public class UAssetRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";
    
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }
    
    [JsonPropertyName("file_paths")]
    public List<string>? FilePaths { get; set; }
    
    [JsonPropertyName("mip_gen")]
    public string? MipGen { get; set; }
    
    [JsonPropertyName("uexp_path")]
    public string? UexpPath { get; set; }
    
    [JsonPropertyName("usmap_path")]
    public string? UsmapPath { get; set; }
    
    // GUI Backend fields
    [JsonPropertyName("export_index")]
    public int ExportIndex { get; set; } = -1;
    
    [JsonPropertyName("property_path")]
    public string? PropertyPath { get; set; }
    
    [JsonPropertyName("property_name")]
    public string? PropertyName { get; set; }
    
    [JsonPropertyName("property_type")]
    public string? PropertyType { get; set; }
    
    [JsonPropertyName("property_value")]
    public JsonElement? PropertyValue { get; set; }
    
    [JsonPropertyName("output_path")]
    public string? OutputPath { get; set; }
    
    [JsonPropertyName("json_data")]
    public string? JsonData { get; set; }
    
    // PAK/IoStore operations
    [JsonPropertyName("aes_key")]
    public string? AesKey { get; set; }
    
    [JsonPropertyName("internal_path")]
    public string? InternalPath { get; set; }
    
    [JsonPropertyName("mount_point")]
    public string? MountPoint { get; set; }
    
    [JsonPropertyName("path_hash_seed")]
    public ulong PathHashSeed { get; set; } = 0;
    
    [JsonPropertyName("input_dir")]
    public string? InputDir { get; set; }

    /// <summary>
    /// Path to a .pak file to extract and use as input (alternative to input_dir)
    /// </summary>
    [JsonPropertyName("input_pak")]
    public string? InputPak { get; set; }

    [JsonPropertyName("compress")]
    public bool Compress { get; set; } = true;
    
    [JsonPropertyName("filter_patterns")]
    public List<string>? FilterPatterns { get; set; }
    
    /// <summary>
    /// Enable parallel processing for batch operations
    /// </summary>
    [JsonPropertyName("parallel")]
    public bool Parallel { get; set; } = false;
    
    /// <summary>
    /// Enable obfuscation (encrypts with game's AES key to block extraction tools like FModel)
    /// </summary>
    [JsonPropertyName("obfuscate")]
    public bool Obfuscate { get; set; } = false;

    /// <summary>
    /// Hybrid bundle: route Unreal assets into the IoStore (.ucas) as usual, but embed any
    /// non-Unreal files (e.g. .bnk/.wem audio, raw .png, .bin) as loose entries in the
    /// companion PAK so the game mounts them by path directly. (create_mod_iostore only)
    /// </summary>
    [JsonPropertyName("hybrid")]
    public bool Hybrid { get; set; } = false;
    
    /// <summary>
    /// Use compact CUE4Parse-style JSON output (read-only, no roundtrip)
    /// </summary>
    [JsonPropertyName("compact")]
    public bool Compact { get; set; } = false;
    
    /// <summary>
    /// Mip level index for texture extraction (0 = largest)
    /// </summary>
    [JsonPropertyName("mip_index")]
    public int MipIndex { get; set; } = 0;
    
    /// <summary>
    /// Output format for texture extraction (png, tga, dds, bmp). Default: png
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }
    
    /// <summary>
    /// Base path for preserving relative directory structure in batch output.
    /// When set, output files maintain their relative path from this base.
    /// </summary>
    [JsonPropertyName("base_path")]
    public string? BasePath { get; set; }

    /// <summary>
    /// Game Paks directory path for extract_iostore_legacy
    /// </summary>
    [JsonPropertyName("game_paks")]
    public string? GamePaks { get; set; }

    /// <summary>
    /// Mod path for extract_iostore_legacy (utoc file or directory)
    /// </summary>
    [JsonPropertyName("mod_path")]
    public string? ModPath { get; set; }

    /// <summary>
    /// Extract dependencies for extract_iostore_legacy
    /// </summary>
    [JsonPropertyName("with_deps")]
    public bool WithDeps { get; set; } = false;
}

public class UAssetResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
    
    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

public partial class Program
{
    private static int CliSkeletalMeshInfo(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool skeletal_mesh_info <uasset_path> <usmap_path>");
            return 1;
        }

        string uassetPath = args[1];
        string usmapPath = args[2];

        if (!File.Exists(uassetPath))
        {
            Console.Error.WriteLine($"File not found: {uassetPath}");
            return 1;
        }

        var asset = LoadAsset(uassetPath, usmapPath);
        
        Console.WriteLine($"=== Skeletal Mesh Analysis: {Path.GetFileName(uassetPath)} ===\n");
        
        foreach (var export in asset.Exports)
        {
            if (export is SkeletalMeshExport skelMesh)
            {
                // Ensure extra data is parsed (lazy parsing since Extras is populated after Read)
                skelMesh.EnsureExtraDataParsed();
                
                Console.WriteLine($"Export: {export.ObjectName?.Value?.Value ?? "Unknown"}");
                Console.WriteLine($"  ExtraDataParsed: {skelMesh.ExtraDataParsed}");
                Console.WriteLine($"  Extras Length: {skelMesh.Extras?.Length ?? 0} bytes");
                
                // Strip Flags
                if (skelMesh.StripFlags != null)
                {
                    Console.WriteLine($"\n  === Strip Flags ===");
                    Console.WriteLine($"    GlobalStripFlags: 0x{skelMesh.StripFlags.GlobalStripFlags:X2}");
                    Console.WriteLine($"    ClassStripFlags: 0x{skelMesh.StripFlags.ClassStripFlags:X2}");
                    Console.WriteLine($"    IsEditorDataStripped: {skelMesh.StripFlags.IsEditorDataStripped()}");
                    Console.WriteLine($"    IsDataStrippedForServer: {skelMesh.StripFlags.IsDataStrippedForServer()}");
                }
                
                // Bounds
                if (skelMesh.ImportedBounds != null)
                {
                    Console.WriteLine($"\n  === Imported Bounds ===");
                    Console.WriteLine($"    Origin: ({skelMesh.ImportedBounds.Origin.X:F2}, {skelMesh.ImportedBounds.Origin.Y:F2}, {skelMesh.ImportedBounds.Origin.Z:F2})");
                    Console.WriteLine($"    BoxExtent: ({skelMesh.ImportedBounds.BoxExtent.X:F2}, {skelMesh.ImportedBounds.BoxExtent.Y:F2}, {skelMesh.ImportedBounds.BoxExtent.Z:F2})");
                    Console.WriteLine($"    SphereRadius: {skelMesh.ImportedBounds.SphereRadius:F2}");
                }
                
                // Materials
                if (skelMesh.Materials != null && skelMesh.Materials.Count > 0)
                {
                    Console.WriteLine($"\n  === Materials ({skelMesh.Materials.Count}) ===");
                    for (int i = 0; i < skelMesh.Materials.Count; i++)
                    {
                        var mat = skelMesh.Materials[i];
                        Console.WriteLine($"    [{i}] MaterialInterface: {mat.MaterialInterface.Index}");
                        Console.WriteLine($"        SlotName: {mat.MaterialSlotName?.Value?.Value ?? "None"}");
                        Console.WriteLine($"        ImportedSlotName: {mat.ImportedMaterialSlotName?.Value?.Value ?? "None"}");
                        Console.WriteLine($"        GameplayTags: {mat.GameplayTagContainer?.GameplayTags?.Count ?? 0}");
                        if (mat.GameplayTagContainer?.GameplayTags != null && mat.GameplayTagContainer.GameplayTags.Count > 0)
                        {
                            foreach (var tag in mat.GameplayTagContainer.GameplayTags)
                            {
                                Console.WriteLine($"          - {tag.TagName?.Value?.Value ?? "Unknown"}");
                            }
                        }
                    }
                }
                
                // Reference Skeleton
                if (skelMesh.ReferenceSkeleton != null)
                {
                    Console.WriteLine($"\n  === Reference Skeleton ===");
                    Console.WriteLine($"    Bone Count: {skelMesh.ReferenceSkeleton.BoneCount}");
                    Console.WriteLine($"    RefBonePose Count: {skelMesh.ReferenceSkeleton.RefBonePose.Count}");
                    Console.WriteLine($"    NameToIndexMap Count: {skelMesh.ReferenceSkeleton.NameToIndexMap.Count}");
                    
                    // Show first few bones
                    Console.WriteLine($"\n    First 10 Bones:");
                    for (int i = 0; i < Math.Min(10, skelMesh.ReferenceSkeleton.BoneCount); i++)
                    {
                        var bone = skelMesh.ReferenceSkeleton.RefBoneInfo[i];
                        Console.WriteLine($"      [{i}] {bone.Name?.Value?.Value ?? "Unknown"} (Parent: {bone.ParentIndex})");
                    }
                }
                
                // LOD Info
                Console.WriteLine($"\n  === LOD Info ===");
                Console.WriteLine($"    bCooked: {skelMesh.bCooked}");
                Console.WriteLine($"    LODCount: {skelMesh.LODCount}");
                
                // Remaining Extra Data Analysis
                if (skelMesh.RemainingExtraData != null && skelMesh.RemainingExtraData.Length > 0)
                {
                    Console.WriteLine($"\n  === Remaining Extra Data ({skelMesh.RemainingExtraData.Length} bytes) ===");
                    Console.WriteLine($"    This contains LOD render data (sections, vertices, indices, etc.)");
                    Console.WriteLine($"    First 64 bytes (hex): {BitConverter.ToString(skelMesh.RemainingExtraData.Take(Math.Min(64, skelMesh.RemainingExtraData.Length)).ToArray()).Replace("-", " ")}");
                }
                
                // Since ExtraDataParsed failed, analyze raw Extras directly
                if (!skelMesh.ExtraDataParsed && skelMesh.Extras != null && skelMesh.Extras.Length > 0)
                {
                    Console.WriteLine($"\n  === Raw Extras Analysis ({skelMesh.Extras.Length} bytes) ===");
                    Console.WriteLine($"    First 128 bytes (hex):");
                    for (int row = 0; row < 8 && row * 16 < skelMesh.Extras.Length; row++)
                    {
                        int offset = row * 16;
                        string hex = BitConverter.ToString(skelMesh.Extras.Skip(offset).Take(16).ToArray()).Replace("-", " ");
                        Console.WriteLine($"      0x{offset:X4}: {hex}");
                    }
                    
                    // Try to find section data patterns in the raw extras
                    // Look for FSkelMeshRenderSection patterns
                    Console.WriteLine($"\n    Searching for section patterns...");
                    AnalyzeSkeletalMeshSections(skelMesh.Extras, asset);
                }
                
                // Also dump tagged properties that might control visibility
                if (export is NormalExport normalExport && normalExport.Data != null)
                {
                    Console.WriteLine($"\n  === All Tagged Properties ({normalExport.Data.Count}) ===");
                    foreach (var prop in normalExport.Data)
                    {
                        string propName = prop.Name?.Value?.Value ?? "Unknown";
                        Console.WriteLine($"    {propName}: {prop.GetType().Name}");
                        
                        // Dump LODInfo array in detail
                        if (propName == "LODInfo" && prop is ArrayPropertyData lodInfoArray)
                        {
                            Console.WriteLine($"\n  === LODInfo Details ({lodInfoArray.Value?.Length ?? 0} LODs) ===");
                            if (lodInfoArray.Value != null)
                            {
                                for (int lodIdx = 0; lodIdx < lodInfoArray.Value.Length; lodIdx++)
                                {
                                    Console.WriteLine($"    LOD {lodIdx}:");
                                    if (lodInfoArray.Value[lodIdx] is StructPropertyData lodStruct && lodStruct.Value != null)
                                    {
                                        foreach (var lodProp in lodStruct.Value)
                                        {
                                            string lodPropName = lodProp.Name?.Value?.Value ?? "?";
                                            Console.WriteLine($"      {lodPropName}: {lodProp.GetType().Name} = {GetPropertyValueString(lodProp)}");
                                        }
                                    }
                                }
                            }
                        }
                        
                        // Dump MeshClothingAssets
                        if (propName == "MeshClothingAssets" && prop is ArrayPropertyData clothArray)
                        {
                            Console.WriteLine($"\n  === MeshClothingAssets ({clothArray.Value?.Length ?? 0}) ===");
                            if (clothArray.Value != null)
                            {
                                for (int i = 0; i < clothArray.Value.Length; i++)
                                {
                                    Console.WriteLine($"    [{i}]: {clothArray.Value[i]?.GetType().Name}");
                                }
                            }
                        }
                    }
                }
                
                Console.WriteLine();
            }
        }
        
        return 0;
    }
    
    private static string GetPropertyValueString(PropertyData prop)
    {
        return prop switch
        {
            BoolPropertyData b => b.Value.ToString(),
            IntPropertyData i => i.Value.ToString(),
            FloatPropertyData f => f.Value.ToString("F4"),
            DoublePropertyData d => d.Value.ToString("F4"),
            StrPropertyData s => s.Value?.ToString() ?? "null",
            NamePropertyData n => n.Value?.Value?.Value ?? "null",
            ObjectPropertyData o => o.Value?.Index.ToString() ?? "null",
            ArrayPropertyData a => $"[{a.Value?.Length ?? 0} items]",
            StructPropertyData st => $"Struct({st.Value?.Count ?? 0} props)",
            BytePropertyData bp => bp.ByteType == BytePropertyType.Byte ? bp.Value.ToString() : bp.EnumValue?.Value?.Value ?? "null",
            EnumPropertyData e => e.Value?.Value?.Value ?? "null",
            _ => prop.GetType().Name
        };
    }
    
    
    private static void AnalyzeSkeletalMeshSections(byte[] data, UAsset asset)
    {
        // Per CUE4Parse, FSkelMeshRenderSection structure contains:
        // - MaterialIndex (uint16)
        // - BaseIndex (uint32)  
        // - NumTriangles (uint32)
        // - bRecomputeTangent (bool)
        // - RecomputeTangentsVertexMaskChannel (uint8)
        // - bCastShadow (bool)
        // - bVisibleInRayTracing (bool)
        // - BaseVertexIndex (uint32)
        // - ClothMappingDataLODs (array)
        // - BoneMap (array of uint16)
        // - NumVertices (int32)
        // - MaxBoneInfluences (int32)
        // - CorrespondClothAssetIndex (int16)
        // - ClothingData (FClothingSectionData)
        // - DuplicatedVerticesBuffer (FDuplicatedVerticesBuffer)
        // - bDisabled (bool)
        
        Console.WriteLine($"    Looking for FSkelMeshRenderSection patterns...");
        
        // Search for patterns that look like section data
        // MaterialIndex (0-50), followed by BaseIndex, NumTriangles
        int sectionsFound = 0;
        
        for (int i = 0; i < data.Length - 20; i++)
        {
            // Check for potential section start
            if (i + 14 >= data.Length) break;
            
            ushort matIdx = BitConverter.ToUInt16(data, i);
            uint baseIdx = BitConverter.ToUInt32(data, i + 2);
            uint numTris = BitConverter.ToUInt32(data, i + 6);
            
            // Heuristics: material index 0-20, reasonable base index and triangle count
            if (matIdx <= 20 && baseIdx < 10000000 && numTris > 10 && numTris < 500000)
            {
                // Check if there might be bool flags after
                byte possibleFlags = data[i + 10];
                
                // Look for bDisabled flag pattern - it's usually near the end of section
                // For now, just report potential sections
                if (sectionsFound < 20)
                {
                    Console.WriteLine($"      Potential section at 0x{i:X}: MatIdx={matIdx}, BaseIdx={baseIdx}, NumTris={numTris}");
                    
                    // Try to read more fields
                    if (i + 20 < data.Length)
                    {
                        byte bRecomputeTangent = data[i + 10];
                        byte recomputeChannel = data[i + 11];
                        byte bCastShadow = data[i + 12];
                        byte bVisibleInRayTracing = data[i + 13];
                        uint baseVertexIdx = BitConverter.ToUInt32(data, i + 14);
                        
                        Console.WriteLine($"        bRecomputeTangent={bRecomputeTangent}, bCastShadow={bCastShadow}, bVisibleInRayTracing={bVisibleInRayTracing}, BaseVertexIdx={baseVertexIdx}");
                    }
                }
                sectionsFound++;
            }
        }
        
        Console.WriteLine($"    Total potential sections found: {sectionsFound}");
        
        // Also look for bDisabled patterns (byte value 0 or 1 in specific contexts)
        Console.WriteLine($"\n    Looking for bDisabled flag patterns...");
        
        // Search for the string "bDisabled" or patterns that might indicate disabled sections
        // In cooked data, bools are typically single bytes
    }
    
    #region JSON Mode Parity Functions
    
    private static UAssetResponse DumpAssetJson(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "file_path is required" };
        
        try
        {
            var asset = LoadAsset(filePath, usmapPath);
            
            var result = new Dictionary<string, object>
            {
                ["file_path"] = filePath,
                ["package_flags"] = asset.PackageFlags.ToString(),
                ["has_unversioned_properties"] = asset.HasUnversionedProperties,
                ["name_count"] = asset.GetNameMapIndexList().Count,
                ["import_count"] = asset.Imports.Count,
                ["export_count"] = asset.Exports.Count,
                ["exports"] = asset.Exports.Select((e, i) => new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["class_type"] = e.GetExportClassType()?.Value?.Value ?? "Unknown",
                    ["object_name"] = e.ObjectName?.Value?.Value ?? "Unknown",
                    ["serial_size"] = e.SerialSize,
                    ["serial_offset"] = e.SerialOffset
                }).ToList()
            };
            
            return new UAssetResponse 
            { 
                Success = true, 
                Message = "Asset dumped successfully",
                Data = result
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to dump asset: {ex.Message}" };
        }
    }
    
    private static UAssetResponse GetSkeletalMeshInfoJson(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "file_path is required" };
        
        try
        {
            var asset = LoadAsset(filePath, usmapPath);
            
            var skExport = asset.Exports.OfType<UAssetAPI.ExportTypes.SkeletalMeshExport>().FirstOrDefault();
            if (skExport == null)
                return new UAssetResponse { Success = false, Message = "No SkeletalMesh export found" };
            
            var result = new Dictionary<string, object>
            {
                ["material_count"] = skExport.Materials?.Count ?? 0,
                ["bone_count"] = skExport.ReferenceSkeleton?.BoneCount ?? 0,
                ["materials"] = skExport.Materials?.Select((m, i) => new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["slot_name"] = m.MaterialSlotName?.Value?.Value ?? "Unknown",
                    ["material_index"] = m.MaterialInterface?.Index ?? 0
                }).ToList() ?? new List<Dictionary<string, object>>()
            };
            
            return new UAssetResponse { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to get SkeletalMesh info: {ex.Message}" };
        }
    }
    
    private static UAssetResponse ToJsonJson(string? filePath, List<string>? filePaths, string? usmapPath, string? outputPath, bool compact = false, string? basePath = null)
    {
        // Batch mode: file_paths provided → parallel processing
        bool isBatch = filePaths != null && filePaths.Count > 0;
        
        if (!isBatch)
        {
            // Single file mode
            if (string.IsNullOrEmpty(filePath))
                return new UAssetResponse { Success = false, Message = "file_path or file_paths is required" };
            
            try
            {
                var asset = LoadAsset(filePath, usmapPath);
                PreloadReferencedAssetsForSchemas(asset);
                
                string jsonOutput = compact
                    ? CompactJsonSerializer.Serialize(asset)
                    : asset.SerializeJson(Newtonsoft.Json.Formatting.Indented);
                
                if (!string.IsNullOrEmpty(outputPath))
                {
                    string? outDir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(outDir))
                        Directory.CreateDirectory(outDir);
                    File.WriteAllText(outputPath, jsonOutput, System.Text.Encoding.UTF8);
                    return new UAssetResponse { Success = true, Message = $"JSON{(compact ? " (compact)" : "")} saved to {outputPath}" };
                }
                
                return new UAssetResponse { Success = true, Message = jsonOutput };
            }
            catch (Exception ex)
            {
                return new UAssetResponse { Success = false, Message = $"Failed to convert to JSON: {ex.Message}" };
            }
        }
        
        // Batch parallel mode
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "output_path (output directory) is required for batch mode" };
        
        Directory.CreateDirectory(outputPath);
        
        // Load mappings once upfront to avoid file locking in parallel threads
        Usmap? mappings = LoadMappings(usmapPath);
        
        int successCount = 0;
        int failCount = 0;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        Parallel.ForEach(filePaths!, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, fp =>
        {
            try
            {
                var asset = LoadAssetWithMappings(fp, mappings);
                PreloadReferencedAssetsForSchemas(asset);
                
                string jsonOutput = compact
                    ? CompactJsonSerializer.Serialize(asset)
                    : asset.SerializeJson(Newtonsoft.Json.Formatting.Indented);

                // Determine output path: preserve relative directory structure if basePath is set
                string jsonFileName;
                if (!string.IsNullOrEmpty(basePath))
                {
                    string relativePath = Path.GetRelativePath(basePath, fp);
                    jsonFileName = Path.ChangeExtension(relativePath, ".json");
                }
                else
                {
                    jsonFileName = Path.GetFileNameWithoutExtension(fp) + ".json";
                }
                string jsonOutputPath = Path.Combine(outputPath, jsonFileName);
                string? jsonDir = Path.GetDirectoryName(jsonOutputPath);
                if (!string.IsNullOrEmpty(jsonDir))
                    Directory.CreateDirectory(jsonDir);
                File.WriteAllText(jsonOutputPath, jsonOutput, System.Text.Encoding.UTF8);
                
                Interlocked.Increment(ref successCount);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(fp)}: {ex.Message}");
                Interlocked.Increment(ref failCount);
            }
        });
        
        sw.Stop();
        
        var data = new Dictionary<string, object>
        {
            ["success_count"] = successCount,
            ["fail_count"] = failCount,
            ["total"] = filePaths!.Count,
            ["elapsed_ms"] = sw.ElapsedMilliseconds
        };
        if (!errors.IsEmpty)
            data["errors"] = errors.ToList();
        
        return new UAssetResponse
        {
            Success = failCount == 0,
            Message = $"Batch to_json: {successCount}/{filePaths!.Count} succeeded in {sw.ElapsedMilliseconds}ms",
            Data = data
        };
    }
    
    private static UAssetResponse FromJsonJson(string? jsonPath, List<string>? filePaths, string? outputPath, string? usmapPath, string? basePath = null)
    {
        // Batch mode: file_paths provided → parallel processing
        bool isBatch = filePaths != null && filePaths.Count > 0;
        
        if (!isBatch)
        {
            // Single file mode
            if (string.IsNullOrEmpty(jsonPath))
                return new UAssetResponse { Success = false, Message = "file_path or file_paths is required" };
            if (string.IsNullOrEmpty(outputPath))
                return new UAssetResponse { Success = false, Message = "output_path is required" };
            
            try
            {
                string jsonContent = File.ReadAllText(jsonPath);
                var asset = UAsset.DeserializeJson(jsonContent);
                
                if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
                {
                    asset.Mappings = new Usmap(usmapPath);
                }
                
                asset.FilePath = Path.GetFullPath(outputPath);
                PreloadReferencedAssetsForSchemas(asset);
                
                string? outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outDir))
                    Directory.CreateDirectory(outDir);
                
                asset.Write(outputPath);
                return new UAssetResponse { Success = true, Message = $"Asset saved to {outputPath}" };
            }
            catch (Exception ex)
            {
                return new UAssetResponse { Success = false, Message = $"Failed to convert from JSON: {ex.Message}" };
            }
        }
        
        // Batch parallel mode
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "output_path (output directory) is required for batch mode" };
        
        Directory.CreateDirectory(outputPath);
        
        // Load mappings once, shared across threads (read-only after construction)
        Usmap? mappings = null;
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            mappings = new Usmap(usmapPath);
        
        int successCount = 0;
        int failCount = 0;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        Parallel.ForEach(filePaths!, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, jp =>
        {
            try
            {
                string jsonContent = File.ReadAllText(jp, System.Text.Encoding.UTF8);
                var asset = UAsset.DeserializeJson(jsonContent);
                
                if (asset == null)
                {
                    errors.Add($"{Path.GetFileName(jp)}: Failed to deserialize JSON");
                    Interlocked.Increment(ref failCount);
                    return;
                }
                
                asset.Mappings = mappings;

                // Determine output path: preserve relative directory structure if basePath is set
                string uassetFileName;
                if (!string.IsNullOrEmpty(basePath))
                {
                    string relativePath = Path.GetRelativePath(basePath, jp);
                    uassetFileName = Path.ChangeExtension(relativePath, ".uasset");
                }
                else
                {
                    uassetFileName = Path.GetFileNameWithoutExtension(jp) + ".uasset";
                }
                string uassetOutputPath = Path.Combine(outputPath, uassetFileName);
                string? uassetDir = Path.GetDirectoryName(uassetOutputPath);
                if (!string.IsNullOrEmpty(uassetDir))
                    Directory.CreateDirectory(uassetDir);
                asset.FilePath = Path.GetFullPath(uassetOutputPath);
                PreloadReferencedAssetsForSchemas(asset);
                asset.Write(uassetOutputPath);
                
                Interlocked.Increment(ref successCount);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(jp)}: {ex.Message}");
                Interlocked.Increment(ref failCount);
            }
        });
        
        sw.Stop();
        
        var data = new Dictionary<string, object>
        {
            ["success_count"] = successCount,
            ["fail_count"] = failCount,
            ["total"] = filePaths!.Count,
            ["elapsed_ms"] = sw.ElapsedMilliseconds
        };
        if (!errors.IsEmpty)
            data["errors"] = errors.ToList();
        
        return new UAssetResponse
        {
            Success = failCount == 0,
            Message = $"Batch from_json: {successCount}/{filePaths!.Count} succeeded in {sw.ElapsedMilliseconds}ms",
            Data = data
        };
    }
    
    private static UAssetResponse CityHashJson(string? pathString)
    {
        if (string.IsNullOrEmpty(pathString))
            return new UAssetResponse { Success = false, Message = "file_path (path string to hash) is required" };
        
        ulong hash = IoStore.CityHash.CityHash64(pathString.ToLowerInvariant());
        return new UAssetResponse 
        { 
            Success = true, 
            Data = new Dictionary<string, object>
            {
                ["input"] = pathString,
                ["hash_decimal"] = hash,
                ["hash_hex"] = $"0x{hash:X16}"
            }
        };
    }
    
    private static UAssetResponse CloneModIoStoreJson(string? utocPath, string? outputPath)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "file_path (utoc path) is required" };
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "output_path is required" };
        
        try
        {
            // Use the existing CLI implementation
            string[] args = new[] { "clone_mod_iostore", utocPath, outputPath };
            int result = CliCloneModIoStore(args);
            
            if (result == 0)
                return new UAssetResponse { Success = true, Message = $"Cloned to {outputPath}" };
            else
                return new UAssetResponse { Success = false, Message = "Clone failed" };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to clone: {ex.Message}" };
        }
    }
    
    private static UAssetResponse InspectZenJson(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "file_path is required" };
        
        try
        {
            byte[] data = File.ReadAllBytes(filePath);
            var zenHeader = ZenPackage.FZenPackageHeader.Deserialize(data, ZenPackage.EIoContainerHeaderVersion.NoExportInfo);
            
            var result = new Dictionary<string, object>
            {
                ["name_map_count"] = zenHeader.NameMap?.Count ?? 0,
                ["import_map_count"] = zenHeader.ImportMap?.Count ?? 0,
                ["export_map_count"] = zenHeader.ExportMap?.Count ?? 0,
                ["export_bundle_count"] = zenHeader.ExportBundleHeaders?.Count ?? 0,
                ["header_size"] = zenHeader.Summary?.HeaderSize ?? 0
            };
            
            return new UAssetResponse { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to inspect Zen package: {ex.Message}" };
        }
    }
    
    private static int CliInjectTexture(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: UAssetTool inject_texture <base_uasset> <image_file> <output_uasset> [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --no-mips       Mode A: single-mip, inline in .uexp (no .ubulk/.uptnl)");
            Console.Error.WriteLine("                  (default = Mode B: full mip chain, streaming .ubulk/.uptnl + inline)");
            Console.Error.WriteLine("  --usmap <path>  Path to usmap file (required for game-extracted textures)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Note: the pixel format is preserved from the base texture (BC1/BC3/BC5/BC7/etc.).");
            Console.Error.WriteLine("Supported image formats: PNG, TGA, DDS, BMP, JPEG");
            return 1;
        }

        string baseUasset = args[1];
        string imageFile = args[2];
        string outputPath = args[3];

        // Parse options
        bool generateMips = true;
        string? usmapPath = null;

        for (int i = 4; i < args.Length; i++)
        {
            if (args[i] == "--no-mips")
            {
                generateMips = false;
            }
            else if (args[i] == "--usmap" && i + 1 < args.Length)
            {
                usmapPath = args[++i];
            }
        }

        Console.WriteLine($"Injecting texture (object model)...");
        Console.WriteLine($"  Base: {baseUasset}");
        Console.WriteLine($"  Image: {imageFile}");
        Console.WriteLine($"  Output: {outputPath}");
        Console.WriteLine($"  Mode: {(generateMips ? "B (mip chain, streaming)" : "A (single-mip, inline)")}");
        if (usmapPath != null) Console.WriteLine($"  Usmap: {usmapPath}");

        var result = Texture.TextureInjector.InjectObjectModel(baseUasset, imageFile, outputPath, generateMips, usmapPath);
        
        if (result.Success)
        {
            Console.WriteLine();
            Console.WriteLine($"Success!");
            Console.WriteLine($"  Dimensions: {result.Width}x{result.Height}");
            Console.WriteLine($"  Mip Count: {result.MipCount}");
            Console.WriteLine($"  Pixel Format: {result.PixelFormat}");
            Console.WriteLine($"  Total Data Size: {result.TotalDataSize:N0} bytes");
            return 0;
        }
        else
        {
            Console.Error.WriteLine($"Failed: {result.ErrorMessage}");
            return 1;
        }
    }
    
    private static int CliExtractTexture(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool extract_texture <uasset_path> <output_path> [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --format <fmt>  Output format: PNG, TGA, DDS, BMP (default: PNG)");
            Console.Error.WriteLine("  --mip <index>   Mip level to extract (default: 0 = largest)");
            Console.Error.WriteLine("  --usmap <path>  Path to usmap file (required for game-extracted textures)");
            return 1;
        }
        
        string uassetPath = args[1];
        string outputPath = args[2];
        
        // Parse options
        var outputFormat = Texture.TextureOutputFormat.PNG;
        int mipIndex = 0;
        string? usmapPath = null;
        
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--format" && i + 1 < args.Length)
            {
                outputFormat = Texture.TextureExtractor.ParseOutputFormat(args[++i]);
            }
            else if (args[i] == "--mip" && i + 1 < args.Length)
            {
                mipIndex = int.Parse(args[++i]);
            }
            else if (args[i] == "--usmap" && i + 1 < args.Length)
            {
                usmapPath = args[++i];
            }
        }
        
        Console.WriteLine($"Extracting texture...");
        Console.WriteLine($"  Input: {uassetPath}");
        Console.WriteLine($"  Output: {outputPath}");
        Console.WriteLine($"  Format: {outputFormat}");
        Console.WriteLine($"  Mip: {mipIndex}");
        if (usmapPath != null) Console.WriteLine($"  Usmap: {usmapPath}");
        
        var result = Texture.TextureExtractor.Extract(uassetPath, outputPath, outputFormat, mipIndex, usmapPath);
        
        if (result.Success)
        {
            Console.WriteLine();
            Console.WriteLine($"Success!");
            Console.WriteLine($"  Dimensions: {result.Width}x{result.Height}");
            Console.WriteLine($"  Mip Count: {result.MipCount}");
            Console.WriteLine($"  Pixel Format: {result.PixelFormat}");
            Console.WriteLine($"  Output: {result.OutputPath}");
            return 0;
        }
        else
        {
            Console.Error.WriteLine($"Failed: {result.ErrorMessage}");
            return 1;
        }
    }
    
    private static int CliBatchInjectTexture(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: UAssetTool batch_inject_texture <uasset_dir> <image_dir> <output_dir> [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Matches image files to .uasset files by filename stem.");
            Console.Error.WriteLine("Example: T_Skin_D.png in image_dir matches T_Skin_D.uasset in uasset_dir.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --format <fmt>  Compression format: BC7, BC3, BC1, BC5, BC4, BGRA8 (default: BC7)");
            Console.Error.WriteLine("  --no-mips       Don't generate mipmaps");
            Console.Error.WriteLine("  --usmap <path>  Path to usmap file (required for game-extracted textures)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Supported image formats: PNG, TGA, DDS, BMP, JPEG");
            return 1;
        }

        string uassetDir = args[1];
        string imageDir = args[2];
        string outputDir = args[3];

        if (!Directory.Exists(uassetDir))
        {
            Console.Error.WriteLine($"uasset directory not found: {uassetDir}");
            return 1;
        }
        if (!Directory.Exists(imageDir))
        {
            Console.Error.WriteLine($"Image directory not found: {imageDir}");
            return 1;
        }

        // Parse options
        bool generateMips = true;
        string? usmapPath = null;

        for (int i = 4; i < args.Length; i++)
        {
            if (args[i] == "--no-mips")
                generateMips = false;
            else if (args[i] == "--usmap" && i + 1 < args.Length)
                usmapPath = args[++i];
        }

        // Build a lookup: filename stem -> uasset path (recursive)
        var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".tga", ".dds", ".bmp", ".jpg", ".jpeg" };
        var uassetFiles = Directory.GetFiles(uassetDir, "*.uasset", SearchOption.AllDirectories);
        var uassetMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in uassetFiles)
        {
            string stem = Path.GetFileNameWithoutExtension(f);
            if (!uassetMap.ContainsKey(stem))
                uassetMap[stem] = f;
        }

        // Find all image files (recursive)
        var imageFiles = Directory.GetFiles(imageDir, "*.*", SearchOption.AllDirectories)
            .Where(f => imageExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        if (imageFiles.Count == 0)
        {
            Console.Error.WriteLine($"No image files found in: {imageDir}");
            return 1;
        }

        Console.WriteLine($"Found {imageFiles.Count} image file(s) in: {imageDir}");
        Console.WriteLine($"Found {uassetMap.Count} .uasset file(s) in: {uassetDir}");
        Console.WriteLine($"Mode: {(generateMips ? "B (mip chain, streaming)" : "A (single-mip, inline)")}");
        Console.WriteLine();

        Directory.CreateDirectory(outputDir);

        int success = 0, failed = 0, skipped = 0;

        foreach (var imageFile in imageFiles)
        {
            string imageStem = Path.GetFileNameWithoutExtension(imageFile);

            if (!uassetMap.TryGetValue(imageStem, out string? matchedUasset))
            {
                Console.Error.WriteLine($"  SKIP: {Path.GetFileName(imageFile)} - no matching .uasset found");
                skipped++;
                continue;
            }

            // Preserve relative path structure from uasset_dir into output_dir
            string relPath = Path.GetRelativePath(uassetDir, matchedUasset);
            string outputPath = Path.Combine(outputDir, relPath);
            string? outSubDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outSubDir))
                Directory.CreateDirectory(outSubDir);

            // Also copy companion .uexp if it exists alongside the base
            string baseUexp = Path.ChangeExtension(matchedUasset, ".uexp");
            string outputUexp = Path.ChangeExtension(outputPath, ".uexp");

            Console.Write($"  {Path.GetFileName(imageFile)} -> {relPath} ... ");

            try
            {
                var result = Texture.TextureInjector.InjectObjectModel(matchedUasset, imageFile, outputPath, generateMips, usmapPath);
                if (result.Success)
                {
                    Console.WriteLine($"OK ({result.Width}x{result.Height}, {result.MipCount} mips)");
                    success++;
                }
                else
                {
                    Console.WriteLine($"FAILED: {result.ErrorMessage}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Batch inject complete: {success} succeeded, {failed} failed, {skipped} skipped (no match)");
        return failed > 0 ? 1 : 0;
    }

    private static int CliBatchExtractTexture(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool batch_extract_texture <uasset_dir> <output_dir> [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Extracts all Texture2D .uasset files in a directory to images.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --format <fmt>  Output format: PNG, TGA, DDS, BMP (default: PNG)");
            Console.Error.WriteLine("  --mip <index>   Mip level to extract (default: 0 = largest)");
            Console.Error.WriteLine("  --usmap <path>  Path to usmap file (required for game-extracted textures)");
            return 1;
        }

        string uassetDir = args[1];
        string outputDir = args[2];

        if (!Directory.Exists(uassetDir))
        {
            Console.Error.WriteLine($"Directory not found: {uassetDir}");
            return 1;
        }

        // Parse options
        var outputFormat = Texture.TextureOutputFormat.PNG;
        int mipIndex = 0;
        string? usmapPath = null;

        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--format" && i + 1 < args.Length)
                outputFormat = Texture.TextureExtractor.ParseOutputFormat(args[++i]);
            else if (args[i] == "--mip" && i + 1 < args.Length)
                mipIndex = int.Parse(args[++i]);
            else if (args[i] == "--usmap" && i + 1 < args.Length)
                usmapPath = args[++i];
        }

        var uassetFiles = Directory.GetFiles(uassetDir, "*.uasset", SearchOption.AllDirectories);
        if (uassetFiles.Length == 0)
        {
            Console.Error.WriteLine($"No .uasset files found in: {uassetDir}");
            return 1;
        }

        string formatExt = outputFormat switch
        {
            Texture.TextureOutputFormat.PNG => ".png",
            Texture.TextureOutputFormat.TGA => ".tga",
            Texture.TextureOutputFormat.DDS => ".dds",
            Texture.TextureOutputFormat.BMP => ".bmp",
            _ => ".png"
        };

        Console.WriteLine($"Found {uassetFiles.Length} .uasset file(s) in: {uassetDir}");
        Console.WriteLine($"Output format: {outputFormat}, Mip: {mipIndex}");
        Console.WriteLine();

        Directory.CreateDirectory(outputDir);

        int success = 0, failed = 0, skipped = 0;

        foreach (var uassetFile in uassetFiles)
        {
            string relPath = Path.GetRelativePath(uassetDir, uassetFile);
            string outputFileName = Path.ChangeExtension(relPath, formatExt);
            string outputPath = Path.Combine(outputDir, outputFileName);
            string? outSubDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outSubDir))
                Directory.CreateDirectory(outSubDir);

            Console.Write($"  {relPath} ... ");

            try
            {
                var result = Texture.TextureExtractor.Extract(uassetFile, outputPath, outputFormat, mipIndex, usmapPath);
                if (result.Success)
                {
                    Console.WriteLine($"OK ({result.Width}x{result.Height}, {result.PixelFormat})");
                    success++;
                }
                else
                {
                    Console.WriteLine($"SKIP: {result.ErrorMessage}");
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Batch extract complete: {success} succeeded, {failed} errors, {skipped} skipped (not Texture2D)");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// JSON API: Extract a Texture2D .uasset to an image file.
    /// </summary>
    private static UAssetResponse ExtractTextureToPngJson(string? filePath, string? outputPath, string? usmapPath, int mipIndex = 0, string? format = null)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "file_path is required" };
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "output_path is required" };

        var outputFormat = ParseTextureFormat(format);

        try
        {
            var result = Texture.TextureExtractor.Extract(filePath, outputPath, outputFormat, mipIndex, usmapPath);

            if (result.Success)
            {
                return new UAssetResponse
                {
                    Success = true,
                    Message = $"Extracted texture to {outputPath} ({result.Width}x{result.Height}, {result.PixelFormat})",
                    Data = new { width = result.Width, height = result.Height, mip_count = result.MipCount, pixel_format = result.PixelFormat, output_path = result.OutputPath }
                };
            }
            else
            {
                return new UAssetResponse { Success = false, Message = result.ErrorMessage ?? "Texture extraction failed" };
            }
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }

    /// <summary>
    /// JSON API: Batch extract Texture2D .uasset files to images with optional parallel processing.
    /// file_paths: list of .uasset paths
    /// output_path: output directory (images named after input files)
    /// parallel: true to process in parallel
    /// </summary>
    private static UAssetResponse BatchExtractTexturePngJson(List<string>? filePaths, string? outputDir, string? usmapPath, int mipIndex = 0, string? format = null, bool parallel = false, string? basePath = null)
    {
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "file_paths is required (list of .uasset paths)" };
        if (string.IsNullOrEmpty(outputDir))
            return new UAssetResponse { Success = false, Message = "output_path is required (output directory)" };

        var outputFormat = ParseTextureFormat(format);
        string ext = outputFormat switch
        {
            Texture.TextureOutputFormat.TGA => ".tga",
            Texture.TextureOutputFormat.DDS => ".dds",
            Texture.TextureOutputFormat.BMP => ".bmp",
            _ => ".png"
        };

        Directory.CreateDirectory(outputDir);

        var results = new System.Collections.Concurrent.ConcurrentBag<object>();
        int successCount = 0;
        int failCount = 0;
        int skipCount = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        void ProcessFile(string inputPath)
        {
            // Determine output filename: preserve relative directory structure if basePath is set
            string jsonFileName;
            if (!string.IsNullOrEmpty(basePath))
            {
                string relativePath = Path.GetRelativePath(basePath, inputPath);
                jsonFileName = Path.ChangeExtension(relativePath, ext);
            }
            else
            {
                jsonFileName = Path.GetFileNameWithoutExtension(inputPath) + ext;
            }

            string outPath = Path.Combine(outputDir, jsonFileName);
            string? outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            try
            {
                var result = Texture.TextureExtractor.Extract(inputPath, outPath, outputFormat, mipIndex, usmapPath);
                if (result.Success)
                {
                    Interlocked.Increment(ref successCount);
                    results.Add(new { file = inputPath, success = true, output_path = result.OutputPath, width = result.Width, height = result.Height, pixel_format = result.PixelFormat });
                }
                else if (result.ErrorMessage != null && result.ErrorMessage.Contains("TextureExport"))
                {
                    Interlocked.Increment(ref skipCount);
                    results.Add(new { file = inputPath, success = false, skipped = true, error = result.ErrorMessage });
                }
                else
                {
                    Interlocked.Increment(ref failCount);
                    results.Add(new { file = inputPath, success = false, skipped = false, error = result.ErrorMessage ?? "Unknown error" });
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failCount);
                results.Add(new { file = inputPath, success = false, skipped = false, error = ex.Message });
            }
        }

        if (parallel)
        {
            Parallel.ForEach(filePaths, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, ProcessFile);
        }
        else
        {
            foreach (var fp in filePaths)
                ProcessFile(fp);
        }

        sw.Stop();

        return new UAssetResponse
        {
            Success = failCount == 0,
            Message = $"Batch extract: {successCount} succeeded, {failCount} failed, {skipCount} skipped out of {filePaths.Count} total ({sw.ElapsedMilliseconds}ms)",
            Data = new { total = filePaths.Count, success = successCount, failed = failCount, skipped = skipCount, elapsed_ms = sw.ElapsedMilliseconds, results = results.ToArray() }
        };
    }

    private static Texture.TextureOutputFormat ParseTextureFormat(string? format)
    {
        return (format?.ToLowerInvariant()) switch
        {
            "tga" => Texture.TextureOutputFormat.TGA,
            "dds" => Texture.TextureOutputFormat.DDS,
            "bmp" => Texture.TextureOutputFormat.BMP,
            _ => Texture.TextureOutputFormat.PNG
        };
    }

    #endregion

    #region LocRes Parsing

    /// <summary>
    /// CLI: Parse .locres file(s) and output as JSON.
    /// Usage: UAssetTool parse_locres &lt;locres_path_or_dir&gt; [--output &lt;json_path&gt;] [--namespace &lt;ns&gt;] [--key &lt;key&gt;] [--stats]
    /// </summary>
    private static int CliParseLocres(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool parse_locres <locres_path_or_dir> [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Arguments:");
            Console.Error.WriteLine("  <locres_path_or_dir>   Path to a .locres file or directory containing .locres files");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --output <path>        Write JSON output to file instead of stdout");
            Console.Error.WriteLine("  --namespace <ns>       Filter to a specific namespace");
            Console.Error.WriteLine("  --key <key>            Look up a specific key (requires --namespace)");
            Console.Error.WriteLine("  --stats                Show statistics only (namespace count, entry count)");
            Console.Error.WriteLine("  --search <term>        Search for entries containing the term in key or value");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Examples:");
            Console.Error.WriteLine("  parse_locres en/Game.locres");
            Console.Error.WriteLine("  parse_locres en/Game.locres --stats");
            Console.Error.WriteLine("  parse_locres en/Game.locres --namespace \"601_HeroUIAsset_1011_ST\" --key \"HeroUIAssetBPTable_10110010_HeroInfo_TName\"");
            Console.Error.WriteLine("  parse_locres en/Game.locres --search \"BRUCE BANNER\"");
            Console.Error.WriteLine("  parse_locres en/Game.locres --output locres_dump.json");
            return 1;
        }

        string inputPath = args[1];
        string? outputPath = null;
        string? filterNamespace = null;
        string? filterKey = null;
        string? searchTerm = null;
        bool statsOnly = false;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length) outputPath = args[++i];
            else if (args[i] == "--namespace" && i + 1 < args.Length) filterNamespace = args[++i];
            else if (args[i] == "--key" && i + 1 < args.Length) filterKey = args[++i];
            else if (args[i] == "--search" && i + 1 < args.Length) searchTerm = args[++i];
            else if (args[i] == "--stats") statsOnly = true;
        }

        var locresFiles = new List<string>();
        if (Directory.Exists(inputPath))
        {
            locresFiles.AddRange(Directory.GetFiles(inputPath, "*.locres", SearchOption.AllDirectories));
            if (locresFiles.Count == 0)
            {
                Console.Error.WriteLine($"No .locres files found in: {inputPath}");
                return 1;
            }
            Console.Error.WriteLine($"Found {locresFiles.Count} .locres file(s)");
        }
        else if (File.Exists(inputPath))
        {
            locresFiles.Add(inputPath);
        }
        else
        {
            Console.Error.WriteLine($"Path not found: {inputPath}");
            return 1;
        }

        foreach (var locresPath in locresFiles)
        {
            try
            {
                Console.Error.WriteLine($"Parsing: {locresPath}");
                var locres = new FTextLocalizationResource(locresPath);
                Console.Error.WriteLine($"  Version: {locres.Version}, Namespaces: {locres.Entries.Count}, Total entries: {locres.TotalEntryCount}");

                if (statsOnly)
                {
                    // Show per-namespace counts
                    var stats = locres.Entries
                        .OrderByDescending(ns => ns.Value.Count)
                        .Select(ns => new { Namespace = ns.Key, Count = ns.Value.Count });
                    string json = JsonSerializer.Serialize(new
                    {
                        file = locresPath,
                        version = locres.Version.ToString(),
                        namespace_count = locres.Entries.Count,
                        total_entries = locres.TotalEntryCount,
                        namespaces = stats
                    }, new JsonSerializerOptions { WriteIndented = true });
                    WriteOutput(json, outputPath);
                    continue;
                }

                // Specific key lookup
                if (!string.IsNullOrEmpty(filterNamespace) && !string.IsNullOrEmpty(filterKey))
                {
                    if (locres.TryGetString(filterNamespace, filterKey, out string? value))
                    {
                        var result = new { Namespace = filterNamespace, Key = filterKey, LocalizedString = value };
                        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                        WriteOutput(json, outputPath);
                    }
                    else
                    {
                        Console.Error.WriteLine($"  Key not found: [{filterNamespace}] {filterKey}");
                    }
                    continue;
                }

                // Filter by namespace
                if (!string.IsNullOrEmpty(filterNamespace))
                {
                    if (locres.Entries.TryGetValue(filterNamespace, out var nsEntries))
                    {
                        var dict = new Dictionary<string, string>();
                        foreach (var kv in nsEntries)
                            dict[kv.Key] = kv.Value.LocalizedString;
                        string json = JsonSerializer.Serialize(new
                        {
                            Namespace = filterNamespace,
                            EntryCount = nsEntries.Count,
                            Entries = dict
                        }, new JsonSerializerOptions { WriteIndented = true });
                        WriteOutput(json, outputPath);
                    }
                    else
                    {
                        Console.Error.WriteLine($"  Namespace not found: {filterNamespace}");
                    }
                    continue;
                }

                // Search
                if (!string.IsNullOrEmpty(searchTerm))
                {
                    var matches = new List<object>();
                    foreach (var (ns, key, localizedString) in locres.GetAllEntries())
                    {
                        if (key.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                            localizedString.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(new { Namespace = ns, Key = key, LocalizedString = localizedString });
                        }
                    }
                    Console.Error.WriteLine($"  Found {matches.Count} matches for \"{searchTerm}\"");
                    string json = JsonSerializer.Serialize(new { SearchTerm = searchTerm, MatchCount = matches.Count, Matches = matches },
                        new JsonSerializerOptions { WriteIndented = true });
                    WriteOutput(json, outputPath);
                    continue;
                }

                // Full dump
                var fullDump = new Dictionary<string, Dictionary<string, string>>();
                foreach (var nsPair in locres.Entries)
                {
                    var inner = new Dictionary<string, string>();
                    foreach (var kv in nsPair.Value)
                        inner[kv.Key] = kv.Value.LocalizedString;
                    fullDump[nsPair.Key] = inner;
                }
                string fullJson = JsonSerializer.Serialize(fullDump, new JsonSerializerOptions { WriteIndented = true });
                WriteOutput(fullJson, outputPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error parsing {locresPath}: {ex.Message}");
            }
        }

        return 0;
    }

    private static void WriteOutput(string content, string? outputPath)
    {
        if (!string.IsNullOrEmpty(outputPath))
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, content, System.Text.Encoding.UTF8);
            Console.Error.WriteLine($"  Written to: {outputPath}");
        }
        else
        {
            Console.WriteLine(content);
        }
    }

    /// <summary>
    /// JSON API: Parse .locres file(s).
    /// </summary>
    private static UAssetResponse ParseLocresJson(string? filePath, List<string>? filePaths)
    {
        var paths = new List<string>();
        if (!string.IsNullOrEmpty(filePath))
            paths.Add(filePath);
        if (filePaths != null)
            paths.AddRange(filePaths);

        if (paths.Count == 0)
            return new UAssetResponse { Success = false, Message = "file_path or file_paths required" };

        try
        {
            var allEntries = new Dictionary<string, Dictionary<string, string>>();
            int totalEntries = 0;

            foreach (var path in paths)
            {
                if (!File.Exists(path))
                    return new UAssetResponse { Success = false, Message = $"File not found: {path}" };

                var locres = new FTextLocalizationResource(path);
                totalEntries += locres.TotalEntryCount;

                foreach (var nsPair in locres.Entries)
                {
                    if (!allEntries.TryGetValue(nsPair.Key, out var inner))
                    {
                        inner = new Dictionary<string, string>();
                        allEntries[nsPair.Key] = inner;
                    }
                    foreach (var kv in nsPair.Value)
                        inner[kv.Key] = kv.Value.LocalizedString;
                }
            }

            return new UAssetResponse
            {
                Success = true,
                Message = $"Parsed {paths.Count} .locres file(s): {allEntries.Count} namespaces, {totalEntries} entries",
                Data = allEntries
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error parsing .locres: {ex.Message}" };
        }
    }

    #endregion
}

#endregion
