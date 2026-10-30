using System;

namespace UAssetAPI.UnrealTypes;

/// <summary>
/// A transform with floating point precision, serialized as rotation (X, Y, Z, W), translation, then scale.
/// </summary>
public struct FTransform3f : ICloneable
{
    public FVector4f Rotation;
    public FVector3f Translation;
    public FVector3f Scale3D;

    public FTransform3f(FVector4f rotation, FVector3f translation, FVector3f scale3D)
    {
        Rotation = rotation;
        Translation = translation;
        Scale3D = scale3D;
    }

    public FTransform3f(AssetBinaryReader reader)
    {
        Rotation = new FVector4f(reader);
        Translation = new FVector3f(reader);
        Scale3D = new FVector3f(reader);
    }

    public int Write(AssetBinaryWriter writer)
    {
        return Rotation.Write(writer) + Translation.Write(writer) + Scale3D.Write(writer);
    }

    public object Clone() => new FTransform3f(Rotation, Translation, Scale3D);
}
