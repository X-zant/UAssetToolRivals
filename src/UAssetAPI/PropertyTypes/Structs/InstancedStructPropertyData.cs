using Newtonsoft.Json;
using System;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyTypes.Structs;

/// <summary>
/// A struct whose type is chosen per instance (FInstancedStruct): the struct's index, the size of the value, then the value.
/// A value that cannot be read, for example because the mappings lack its struct, stays in <see cref="UnparsedValue"/> so it survives a save.
/// </summary>
public class InstancedStructPropertyData : PropertyData<StructPropertyData>
{
    [JsonProperty]
    public FPackageIndex StructIndex = new FPackageIndex();

    [JsonProperty]
    public byte[] UnparsedValue;

    public InstancedStructPropertyData(FName name) : base(name) { }

    public InstancedStructPropertyData() { }

    private static readonly FString CurrentPropertyType = new FString("InstancedStruct");
    public override bool HasCustomStructSerialization => true;
    public override FString PropertyType => CurrentPropertyType;

    public override void Read(AssetBinaryReader reader, bool includeHeader, long leng1, long leng2 = 0, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (includeHeader)
        {
            this.ReadEndPropertyTag(reader);
        }

        StructIndex = new FPackageIndex(reader);
        int size = reader.ReadInt32();
        long start = reader.BaseStream.Position;

        try
        {
            FString structName = StructIndex.IsImport() ? StructIndex.ToImport(reader.Asset).ObjectName.Value : StructIndex.IsExport() ? StructIndex.ToExport(reader.Asset).ObjectName.Value : null;
            if (structName != null)
            {
                var data = new StructPropertyData(Name, FName.DefineDummy(reader.Asset, structName));
                data.Ancestry.Initialize(Ancestry, Name);
                data.Read(reader, false, size);
                if (reader.BaseStream.Position == start + size)
                {
                    Value = data;
                    return;
                }
            }
        }
        catch (Exception)
        {
            // kept as raw bytes below
        }

        reader.BaseStream.Position = start;
        UnparsedValue = reader.ReadBytes(size);
    }

    public override int Write(AssetBinaryWriter writer, bool includeHeader, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (includeHeader)
        {
            this.WriteEndPropertyTag(writer);
        }

        StructIndex.Write(writer);
        long sizePosition = writer.BaseStream.Position;
        writer.Write(0);

        int size = 0;
        if (Value != null)
        {
            size = Value.Write(writer, false);
        }
        else if (UnparsedValue != null)
        {
            writer.Write(UnparsedValue);
            size = UnparsedValue.Length;
        }

        long end = writer.BaseStream.Position;
        writer.BaseStream.Position = sizePosition;
        writer.Write(size);
        writer.BaseStream.Position = end;
        return sizeof(int) * 2 + size;
    }

    public override void ResolveAncestries(UAsset asset, AncestryInfo ancestrySoFar)
    {
        Value?.ResolveAncestries(asset, ancestrySoFar);
        base.ResolveAncestries(asset, ancestrySoFar);
    }

    protected override void HandleCloned(PropertyData res)
    {
        var cloningProperty = (InstancedStructPropertyData)res;
        cloningProperty.StructIndex = FPackageIndex.FromRawIndex(StructIndex.Index);
        cloningProperty.UnparsedValue = (byte[])UnparsedValue?.Clone();
    }
}
