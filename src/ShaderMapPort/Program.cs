namespace ShaderMapPort;

/// <summary>
/// ShaderMapPort &lt;cooked dir | .uasset | @listfile&gt;... (-o &lt;out dir&gt; | --in-place) [--strict] [--assetinfo &lt;json&gt;]...
///
/// Rewrites the frozen shader maps in cooked materials from the stock UE 5.3 layout to Marvel
/// Rivals' layout. Packages without cooked material resources are skipped.
///   -o &lt;dir&gt;           write converted packages under &lt;dir&gt;, mirroring paths below the input dir
///   --in-place         overwrite the input packages
///   --strict           fail on materials that have no shaders (they failed to compile)
///   --assetinfo &lt;json&gt; fail on materials whose shader map the cooker's ShaderAssetInfo json lacks
/// Exit code 2 when any package failed.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var inputs = new List<string>();
        string? outDir = null;
        bool inPlace = false, strict = false;
        var assetInfos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "-o" or "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (args[i] == "--in-place") inPlace = true;
            else if (args[i] == "--strict") strict = true;
            else if (args[i] == "--assetinfo" && i + 1 < args.Length) assetInfos.Add(args[++i]);
            else if (args[i] is "-h" or "--help") { Usage(); return 0; }
            else if (args[i].StartsWith('@'))   // response file: one package path per line (extension optional)
                inputs.AddRange(File.ReadAllLines(args[i][1..]).Select(l => l.Trim().Trim('"')).Where(l => l.Length > 0)
                    .Select(l => l.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? l : l + ".uasset"));
            else inputs.Add(args[i]);
        }
        if (inputs.Count == 0 || (outDir == null) == !inPlace) { Usage(); return 1; }

        // shader maps present in the library the packages ship with (--assetinfo: the cooker's ShaderAssetInfo json)
        HashSet<string>? libraryMaps = null;
        if (assetInfos.Count > 0)
        {
            libraryMaps = new(StringComparer.OrdinalIgnoreCase);
            foreach (var path in assetInfos)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var e in doc.RootElement.GetProperty("ShaderCodeToAssets").EnumerateArray())
                    libraryMaps.Add(e.GetProperty("ShaderMapHash").GetString()!);
            }
        }

        int converted = 0, failed = 0;
        foreach (var input in inputs)
        {
            if (!Directory.Exists(input) && !File.Exists(input)) continue;   // a manifest entry that is not a package
            string baseDir = Directory.Exists(input) ? Path.GetFullPath(input) : Path.GetDirectoryName(Path.GetFullPath(input))!;
            var files = Directory.Exists(input)
                ? Directory.GetFiles(input, "*.uasset", SearchOption.AllDirectories)
                : new[] { input };
            foreach (var file in files)
            {
                if (!MayHoldMaterialResources(file)) continue;
                string rel = Path.GetRelativePath(baseDir, Path.GetFullPath(file));
                string dest = inPlace ? file : Path.Combine(outDir!, rel);
                try
                {
                    var reports = Converter.ConvertPackage(file, dest);
                    if (reports == null) continue;
                    converted++;
                    Console.WriteLine(rel);
                    foreach (var r in reports)
                    {
                        Console.WriteLine($"  resource {r.Index}: {r.Status}" + (r.Status == "converted" ? $" ({r.StockSize} -> {r.GameSize} frozen bytes)" : ""));
                        if (r.AddedFields.Count > 0)
                            Console.WriteLine("    filled game-only fields: " + string.Join(", ", r.AddedFields.Select(f => f[(f.IndexOf('.') + 1)..]).Distinct()));
                    }
                    var resources = reports.Where(r => r.Index >= 0).ToList();
                    if (strict && resources.Count > 0 && resources.All(r => !r.HasShaderMap))
                        throw new InvalidDataException("the material has no shaders (it failed to compile in the editor); the game would draw it with DefaultMaterial");
                    if (libraryMaps != null)
                        foreach (var r in resources.Where(r => r.HasShaderMap && !libraryMaps.Contains(r.ShaderMapHash!)))
                            throw new InvalidDataException($"its shaders (map {r.ShaderMapHash}) are not in the shader library this package ships with; the game would draw it with DefaultMaterial");
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
                {
                    failed++;
                    Console.Error.WriteLine($"{rel}: {ex.Message}");
                }
            }
        }
        Console.WriteLine($"{converted} package(s) converted, {failed} failed");
        return failed > 0 ? 2 : 0;
    }

    /// <summary>Cheap pre-filter: a cooked package with material resources names FMaterialShaderMapContent in its exports.</summary>
    static bool MayHoldMaterialResources(string uasset)
    {
        string uexp = Path.ChangeExtension(uasset, ".uexp");
        var path = File.Exists(uexp) ? uexp : uasset;
        var bytes = File.ReadAllBytes(path);
        return bytes.AsSpan().IndexOf("FMaterialShaderMapContent"u8) >= 0;
    }

    static void Usage()
    {
        Console.WriteLine("ShaderMapPort <cooked dir | .uasset | @listfile>... (-o <out dir> | --in-place) [--strict] [--assetinfo <json>]...");
        Console.WriteLine("  Converts the frozen shader maps of cooked materials from the stock UE 5.3 layout");
        Console.WriteLine("  to Marvel Rivals' layout, so the game accepts a custom material's shaders.");
        Console.WriteLine("  @listfile          one package path per line (extension optional)");
        Console.WriteLine("  --strict           fail on materials without shaders (failed to compile)");
        Console.WriteLine("  --assetinfo <json> the cooker's ShaderAssetInfo json; fail on materials whose shader map it lacks");
    }
}
