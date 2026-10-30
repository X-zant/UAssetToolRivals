using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyTypes.Structs;

/// <summary>
/// A quaternion with floating point precision. Serialized as X, Y, Z, W, the same bytes as an FVector4f.
/// </summary>
public class Quat4fPropertyData : Vector4fPropertyData
{
    public Quat4fPropertyData(FName name) : base(name) { }

    public Quat4fPropertyData() { }

    private static readonly FString CurrentPropertyType = new FString("Quat4f");
    public override FString PropertyType => CurrentPropertyType;
}
