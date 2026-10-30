using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyTypes.Structs;

/// <summary>
/// A vector in 2-D space composed of components (X, Y) with floating point precision.
/// </summary>
public class Vector2fPropertyData : PropertyData<FVector2f>
{
    public Vector2fPropertyData(FName name) : base(name) { }

    public Vector2fPropertyData() { }

    private static readonly FString CurrentPropertyType = new FString("Vector2f");
    public override bool HasCustomStructSerialization => true;
    public override FString PropertyType => CurrentPropertyType;

    public override void Read(AssetBinaryReader reader, bool includeHeader, long leng1, long leng2 = 0, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (includeHeader)
        {
            this.ReadEndPropertyTag(reader);
        }

        Value = new FVector2f(reader);
    }

    public override int Write(AssetBinaryWriter writer, bool includeHeader, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (includeHeader)
        {
            this.WriteEndPropertyTag(writer);
        }

        return Value.Write(writer);
    }

    public override void FromString(string[] d, UAsset asset)
    {
        float.TryParse(d[0], out float X);
        float.TryParse(d[1], out float Y);
        Value = new FVector2f(X, Y);
    }

    public override string ToString()
    {
        return "(" + Value.X + ", " + Value.Y + ")";
    }
}
