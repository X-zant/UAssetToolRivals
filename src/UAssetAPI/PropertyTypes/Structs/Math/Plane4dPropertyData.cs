using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyTypes.Structs;

/// <summary>
/// The name UE5 gives a double precision FPlane in struct properties. Same bytes as an FPlane.
/// </summary>
public class Plane4dPropertyData : PlanePropertyData
{
    public Plane4dPropertyData(FName name) : base(name) { }

    public Plane4dPropertyData() { }

    private static readonly FString CurrentPropertyType = new FString("Plane4d");
    public override FString PropertyType => CurrentPropertyType;
}
