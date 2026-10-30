using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyTypes.Structs;

/// <summary>
/// The name UE5 gives a double precision FVector4 in struct properties. Same bytes as an FVector4.
/// </summary>
public class Vector4dPropertyData : Vector4PropertyData
{
    public Vector4dPropertyData(FName name) : base(name) { }

    public Vector4dPropertyData() { }

    private static readonly FString CurrentPropertyType = new FString("Vector4d");
    public override FString PropertyType => CurrentPropertyType;
}
