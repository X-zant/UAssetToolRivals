using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyTypes.Structs;

/// <summary>
/// A transform with floating point precision.
/// </summary>
public class Transform3fPropertyData : PropertyData<FTransform3f>
{
    public Transform3fPropertyData(FName name) : base(name) { }

    public Transform3fPropertyData() { }

    private static readonly FString CurrentPropertyType = new FString("Transform3f");
    public override bool HasCustomStructSerialization => true;
    public override FString PropertyType => CurrentPropertyType;

    public override void Read(AssetBinaryReader reader, bool includeHeader, long leng1, long leng2 = 0, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (includeHeader)
        {
            this.ReadEndPropertyTag(reader);
        }

        Value = new FTransform3f(reader);
    }

    public override int Write(AssetBinaryWriter writer, bool includeHeader, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (includeHeader)
        {
            this.WriteEndPropertyTag(writer);
        }

        return Value.Write(writer);
    }
}
