using UAssetTool;

namespace MaterialTagPresetExporter;

/// <summary>
/// Standalone front end for <see cref="MaterialTagPresets"/>: reads the game's cooked
/// SkeletalMeshes straight out of the .utoc/.ucas containers and writes a
/// MaterialTagPresets.ini of per-slot gameplay tags and per-LOD DefaultHiddenMaterials
/// for the MaterialTagPlugin in the UE editor.
///
/// Usage:
///   MaterialTagPresetExporter &lt;paks_directory&gt; &lt;output.ini&gt; --usmap &lt;path&gt; [options]
///
/// Run with no arguments for the full option list.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] is "--version" or "-v" or "version"))
        {
            var asm = typeof(Program).Assembly;
            Console.WriteLine($"MaterialTagPresetExporter {asm.GetName().Version?.ToString(3) ?? "unknown"}");
            return 0;
        }

        MaterialTagPresets.UsageName = "MaterialTagPresetExporter";

        // MaterialTagPresets.RunCli parses argv the way the UAssetTool CLI hands it over,
        // with the command name in args[0]. Prepend it so both front ends share one parser.
        string[] forwarded = new string[args.Length + 1];
        forwarded[0] = "export_material_tag_presets";
        Array.Copy(args, 0, forwarded, 1, args.Length);

        return MaterialTagPresets.RunCli(forwarded);
    }
}
