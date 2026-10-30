using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyTypes.Structs;

/// <summary>
/// The name UE5 gives a double precision FVector in struct properties. Same bytes as an FVector.
/// </summary>
public class Vector3dPropertyData : VectorPropertyData
{
    public Vector3dPropertyData(FName name) : base(name) { }

    public Vector3dPropertyData() { }

    private static readonly FString CurrentPropertyType = new FString("Vector3d");
    public override FString PropertyType => CurrentPropertyType;
}
