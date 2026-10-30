using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyTypes.Structs;

/// <summary>
/// A plane with floating point precision. Serialized as X, Y, Z, W, the same bytes as an FVector4f.
/// </summary>
public class Plane4fPropertyData : Vector4fPropertyData
{
    public Plane4fPropertyData(FName name) : base(name) { }

    public Plane4fPropertyData() { }

    private static readonly FString CurrentPropertyType = new FString("Plane4f");
    public override FString PropertyType => CurrentPropertyType;
}
