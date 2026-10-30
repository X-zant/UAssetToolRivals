using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyTypes.Structs;

/// <summary>
/// Marvel Rivals' soft object path, serialized as a single string instead of the engine's asset path and subpath.
/// </summary>
public class MarvelSoftObjectPathPropertyData : PropertyData<FString>
{
    public MarvelSoftObjectPathPropertyData(FName name) : base(name) { }

    public MarvelSoftObjectPathPropertyData() { }

    private static readonly FString CurrentPropertyType = new FString("MarvelSoftObjectPath");
    public override bool HasCustomStructSerialization => true;
    public override FString PropertyType => CurrentPropertyType;

    public override void Read(AssetBinaryReader reader, bool includeHeader, long leng1, long leng2 = 0, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (includeHeader)
        {
            this.ReadEndPropertyTag(reader);
        }

        Value = reader.ReadFString();
    }

    public override int Write(AssetBinaryWriter writer, bool includeHeader, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (includeHeader)
        {
            this.WriteEndPropertyTag(writer);
        }

        return writer.Write(Value);
    }

    public override string ToString()
    {
        return Value?.ToString();
    }
}
