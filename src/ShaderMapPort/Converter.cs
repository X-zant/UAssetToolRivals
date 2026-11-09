using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace ShaderMapPort;

public sealed record ResourceReport(int Index, string Status, int StockSize, int GameSize, IReadOnlyList<string> Types, IReadOnlyList<string> AddedFields)
{
    /// <summary>The shader map's hash (key into the shader library), hex; null when the resource has no shader map.</summary>
    public string? ShaderMapHash { get; init; }
    public bool HasShaderMap => ShaderMapHash != null;
}

/// <summary>Converts the frozen shader maps of cooked material resources from the stock 5.3 layout to the game's.</summary>
public static class Converter
{
    const string RootType = "FMaterialShaderMapContent";

    public static ResourceReport ConvertResource(MaterialResourceBlock block, int index)
    {
        var res = block.Resources[index];
        if (!res.Valid) return new(index, "no shader map", 0, 0, Array.Empty<string>(), Array.Empty<string>());
        var typeNames = res.TypeDependencies.Select(t => block.NameText(t.NameIndex, t.NameNumber)).ToList();
        if (res.ImageLayoutParams.Flags == Layouts.GameFlags && res.IdLayoutParams.Flags == Layouts.GameFlags)
            return new(index, "already in game layout", res.Frozen.Length, res.Frozen.Length, typeNames, Array.Empty<string>());
        if (res.ImageLayoutParams.Flags != Layouts.StockFlags || res.ImageLayoutParams.MaxFieldAlignment != 8)
            throw new NotSupportedException($"resource {index}: frozen with {res.ImageLayoutParams}, expected a stock 64-bit cook (MaxFieldAlignment=8, Flags=0x5)");
        if (typeNames.Count == 0 || typeNames[0] != RootType)
            throw new NotSupportedException($"resource {index}: root type is '{typeNames.FirstOrDefault()}', not {RootType}");

        // stock image -> object model
        var root = new FrozenReader(res.Frozen, Layouts.Stock, typeNames).ReadRoot(RootType);

        // Self-check: the stock layouts must reproduce an image that reads back the same way.
        var again = new FrozenWriter(Layouts.Stock).Write(root);
        var reread = new FrozenReader(again, Layouts.Stock, typeNames).ReadRoot(RootType);
        if (!Same(root, reread)) throw new InvalidDataException($"resource {index}: stock round trip changed the shader map");

        // The game cooks no shader pipelines (none in 2238 of its master materials; a stock cook always adds them
        // when the RHI supports them, whatever r.ShaderPipelines says). Each pipeline stage is also compiled as a
        // standalone shader in the same map, which is what the game looks up, so the pipelines are dropped.
        int droppedPipelines = DropPipelines(root);

        // object model -> game image
        var writer = new FrozenWriter(Layouts.Game);
        var game = writer.Write(root);
        // and the game image must read back with the game layouts (coverage + pointer checks)
        var check = new FrozenReader(game, Layouts.Game, typeNames).ReadRoot(RootType);
        if (!Same(root, check, ignoreAdded: true)) throw new InvalidDataException($"resource {index}: game image does not read back as the same shader map");

        int stockSize = res.Frozen.Length;
        res.Frozen = game;
        // patch tables follow their objects; entries inside dropped pipelines (only those) go away with them
        foreach (var p in res.ScriptNames.Concat(res.MemoryImageNames))
            p.Offsets = p.Offsets.Select(o => MapOrDrop(writer, o, droppedPipelines > 0)).Where(o => o >= 0).Select(o => (uint)o).ToList();
        foreach (var (_, patches) in res.VTables)
        {
            var kept = patches.Select(x => (x.VTableOffset, Offset: MapOrDrop(writer, x.Offset, droppedPipelines > 0))).Where(x => x.Offset >= 0).ToList();
            patches.Clear();
            patches.AddRange(kept.Select(x => (x.VTableOffset, (uint)x.Offset)));
        }
        res.VTables.RemoveAll(v => v.Patches.Count == 0);
        res.ScriptNames.RemoveAll(p => p.Offsets.Count == 0);
        res.MemoryImageNames.RemoveAll(p => p.Offsets.Count == 0);
        res.IdLayoutParams.Flags = Layouts.GameFlags;
        res.ImageLayoutParams.Flags = Layouts.GameFlags;
        // Type sizes are informational in a shipping build; keep them truthful anyway.
        for (int i = 0; i < res.TypeDependencies.Count; i++)
        {
            var g = Layouts.Game.Find(typeNames[i]) ?? Layouts.MeshShader(Layouts.Game, typeNames[i]);
            if (g != null) res.TypeDependencies[i].LayoutSize = (uint)g.Size;
        }
        string status = droppedPipelines > 0 ? $"converted, dropped {droppedPipelines} shader pipeline(s)" : "converted";
        return new(index, status, stockSize, game.Length, typeNames, writer.Defaults.Distinct().ToList());
    }

    static long MapOrDrop(FrozenWriter writer, long src, bool mayDrop)
    {
        if (!mayDrop) return writer.Map(src);
        try { return writer.Map(src); } catch (InvalidDataException) { return -1; }
    }

    static int DropPipelines(FrozenStruct root)
    {
        int n = 0;
        void Clear(FrozenStruct map)
        {
            if (map.Values.TryGetValue("ShaderPipelines", out var v) && v is FrozenArray { Count: > 0 } a)
            {
                // Only when every stage also exists as a standalone shader of the same type in this map; otherwise
                // the pipeline is the only copy of that stage and stays (stock FShaderPipeline layout).
                var standalone = map.Values.TryGetValue("Shaders", out var s) && s is FrozenArray sa
                    ? sa.Elements.OfType<FrozenPtr>().Select(p => p.TypeIndex).ToHashSet() : new HashSet<int>();
                foreach (var pipe in a.Elements.OfType<FrozenPtr>().Select(p => p.Target).OfType<FrozenStruct>())
                    foreach (var (k, stage) in pipe.Values)
                        if (k.StartsWith("Shaders[") && stage is FrozenPtr { Target: not null } sp && !standalone.Contains(sp.TypeIndex))
                            return;
                n += a.Count;
                map.Values["ShaderPipelines"] = new FrozenArray(FrozenArrayKind.Pointers);
            }
        }
        Clear(root);
        if (root.Values.TryGetValue("OrderedMeshShaderMaps", out var m) && m is FrozenArray maps)
            foreach (var e in maps.Elements)
                if (e is FrozenPtr { Target: { } t }) Clear(t);
        return n;
    }

    /// <summary>Structural equality of two object models (values compared by bytes; pointers by target).</summary>
    static bool Same(FrozenValue a, FrozenValue b, bool ignoreAdded = false, HashSet<(object, object)>? seen = null)
    {
        seen ??= new();
        switch (a, b)
        {
            case (FrozenBytes x, FrozenBytes y):
                // a field widened in the target (UsedSceneTextures u32 -> u64) compares on its source width
                int n = Math.Min(x.Bytes.Length, y.Bytes.Length);
                return x.Bytes.AsSpan(0, n).SequenceEqual(y.Bytes.AsSpan(0, n))
                       && x.Bytes.AsSpan(n).IndexOfAnyExcept((byte)0) < 0 && y.Bytes.AsSpan(n).IndexOfAnyExcept((byte)0) < 0;
            case (FrozenStruct x, FrozenStruct y):
                if (!seen.Add((x, y))) return true;
                if (x.Type != y.Type) return false;
                foreach (var (k, v) in x.Values)
                    if (!y.Values.TryGetValue(k, out var w) || !Same(v, w, ignoreAdded, seen)) return false;
                return ignoreAdded || x.Values.Count == y.Values.Count;
            case (FrozenArray x, FrozenArray y):
                if (x.Count != y.Count || x.Kind != y.Kind) return false;
                if (x.Kind == FrozenArrayKind.Bytes)
                {
                    if (x.Count == 0) return true;
                    int m = Math.Min(x.Bytes!.Length, y.Bytes!.Length);   // trailing alignment padding may differ
                    return x.Bytes.AsSpan(0, m).SequenceEqual(y.Bytes.AsSpan(0, m));
                }
                return x.Elements.Zip(y.Elements).All(p => Same(p.First, p.Second, ignoreAdded, seen));
            case (FrozenPtr x, FrozenPtr y):
                if ((x.Target == null) != (y.Target == null) || x.TypeIndex != y.TypeIndex) return false;
                return x.Target == null || Same(x.Target, y.Target!, ignoreAdded, seen);
            case (FrozenHashTable x, FrozenHashTable y):
                return x.HashMask == y.HashMask && x.IndexSize == y.IndexSize && x.Hash.SequenceEqual(y.Hash) && x.NextIndex.SequenceEqual(y.NextIndex);
            default:
                return false;
        }
    }

    /// <summary>Convert every cooked material resource in a package. Returns null when the package has none.</summary>
    public static List<ResourceReport>? ConvertPackage(string uassetPath, string outPath)
    {
        var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_3);
        var reports = new List<ResourceReport>();
        bool any = false;
        foreach (var export in asset.Exports)
        {
            if (export.Extras is not { Length: > 16 } extras) continue;
            MaterialResourceBlock block; int start, end;
            try { (block, start, end) = MaterialResourceBlock.Locate(extras); }
            catch (InvalidDataException) { continue; }
            any = true;
            for (int i = 0; i < block.Resources.Count; i++)
            {
                var r = block.Resources[i];
                reports.Add(ConvertResource(block, i) with { ShaderMapHash = r.Valid && r.ResourceHash != null ? Convert.ToHexString(r.ResourceHash) : null });
            }
            // the material's cached parameter data needs the game's extra per-parameter arrays too
            int blockLength = end - start;
            var addedArrays = CachedExpressionDataPort.Port(asset, ref extras, ref start);
            end = start + blockLength;   // the block moved with the rewritten struct in front of it
            if (addedArrays.Count > 0)
                reports.Add(new ResourceReport(-1, "cached expression data: added " + string.Join(", ", addedArrays), 0, 0, Array.Empty<string>(), Array.Empty<string>()));
            var rebuilt = block.Write();
            export.Extras = extras.AsSpan(0, start).ToArray().Concat(rebuilt).Concat(extras.AsSpan(end).ToArray()).ToArray();
        }
        if (!any) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        asset.Write(outPath);
        return reports;
    }
}
