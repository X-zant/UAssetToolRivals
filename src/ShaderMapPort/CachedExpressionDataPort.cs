using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace ShaderMapPort;

/// <summary>
/// Adds the per-parameter arrays Marvel Rivals' FMaterialCachedExpressionData has and stock 5.3 does not.
///
/// A cooked UMaterial carries its cached expression data right after its properties
/// (UMaterialInterface::Serialize: uint32 bSavedCachedExpressionData, then the struct as tagged properties).
/// The game's struct (usmap / SDK dump) adds, per scalar parameter:
///   ScalaAsCustomPrimitiveDataSourceBaseIndex (TArray&lt;bool&gt;)
/// and per vector parameter:
///   VectorAsCustomPrimitiveDataSources (TArray&lt;bool&gt;), VectorCustomPrimitiveDataSourceOffsets (TArray&lt;uint8&gt;),
///   VectorParameterNames (TArray&lt;FName&gt;)
/// plus DynamicSwitchValues and ParameterRuntimeUpdate (empty here). The game indexes these arrays by parameter
/// index without checking (UMaterial parameter queries read ScalaAs...[i] right after ScalarPrimitiveDataIndexValues[i]),
/// so a stock cook's missing arrays mean reads past an empty array: a crash on a parameter query, and parameters
/// randomly treated as "custom primitive data" sources while rendering. Game materials hold false / 0 / None in them
/// (M_StylizedOutline_Trans, M_Common_Translucent), which is what this writes.
/// </summary>
public static class CachedExpressionDataPort
{
    const int StructStart = 4;   // after uint32 bSavedCachedExpressionData

    /// <summary>Rewrites <paramref name="extras"/>[0..<paramref name="blockStart"/>) if the game arrays are missing. Returns the added array names.</summary>
    public static List<string> Port(UAsset asset, ref byte[] extras, ref int blockStart)
    {
        var added = new List<string>();
        if (blockStart < StructStart + 8 || BitConverter.ToUInt32(extras, 0) != 1) return added;   // no saved cached data

        // read the tagged struct
        var props = new List<PropertyData>();
        int structEnd;
        using (var ms = new MemoryStream(extras, 0, blockStart))
        using (var reader = new AssetBinaryReader(ms, asset))
        {
            ms.Position = StructStart;
            var ancestry = new AncestryInfo();
            var parent = FName.DefineDummy(asset, "MaterialCachedExpressionData");
            PropertyData? p;
            while ((p = MainSerializer.Read(reader, ancestry, parent, FName.DefineDummy(asset, "/Script/Engine"), null, true)) != null)
                props.Add(p);
            structEnd = (int)ms.Position;
        }

        // the serializer must reproduce what it read, or we do not touch it
        byte[] original = extras.AsSpan(StructStart, structEnd - StructStart).ToArray();
        if (!Write(asset, props).AsSpan().SequenceEqual(original))
            throw new InvalidDataException("CachedExpressionData does not round-trip through UAssetAPI; not modified");

        int numScalars = CountArray(props, "ScalarValues");
        int numVectors = CountArray(props, "VectorValues");
        void Add(string name, string innerType, Func<int, PropertyData> element, int count)
        {
            if (props.Any(x => x.Name.ToString() == name)) return;
            var arr = new ArrayPropertyData(new FName(asset, name)) { ArrayType = new FName(asset, innerType) };
            arr.Value = Enumerable.Range(0, count).Select(element).ToArray();
            props.Add(arr);
            added.Add($"{name}[{count}]");
        }
        Add("ScalaAsCustomPrimitiveDataSourceBaseIndex", "BoolProperty", i => new BoolPropertyData(new FName(asset, i.ToString())) { Value = false }, numScalars);
        Add("VectorAsCustomPrimitiveDataSources", "BoolProperty", i => new BoolPropertyData(new FName(asset, i.ToString())) { Value = false }, numVectors);
        Add("VectorCustomPrimitiveDataSourceOffsets", "ByteProperty", i => new BytePropertyData(new FName(asset, i.ToString())) { ByteType = BytePropertyType.Byte, Value = 0 }, numVectors);
        Add("VectorParameterNames", "NameProperty", i => new NamePropertyData(new FName(asset, i.ToString())) { Value = new FName(asset, "None") }, numVectors);
        if (added.Count == 0) return added;

        byte[] rewritten = Write(asset, props);
        extras = extras.AsSpan(0, StructStart).ToArray().Concat(rewritten).Concat(extras.AsSpan(structEnd).ToArray()).ToArray();
        blockStart += rewritten.Length - original.Length;
        return added;
    }

    static int CountArray(List<PropertyData> props, string name) =>
        props.FirstOrDefault(x => x.Name.ToString() == name) is ArrayPropertyData a ? a.Value.Length : 0;

    static byte[] Write(UAsset asset, List<PropertyData> props)
    {
        using var ms = new MemoryStream();
        using var writer = new AssetBinaryWriter(ms, asset);
        foreach (var p in props) MainSerializer.Write(p, writer, true);
        writer.Write(new FName(asset, "None"));
        writer.Flush();
        return ms.ToArray();
    }
}
