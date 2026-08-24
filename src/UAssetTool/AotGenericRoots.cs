using System.Runtime.CompilerServices;

namespace UAssetTool;

/// <summary>
/// Forces NativeAOT to emit code for the List&lt;T&gt; instantiations over UAssetAPI types.
///
/// ILC compiles only the generic instantiations it can see statically. Newtonsoft builds
/// these reflectively while reading/writing JSON, which ILC cannot observe, so the code is
/// absent from the image and the AOT library throws at runtime:
///
///     'System.Collections.Generic.List`1[UAssetAPI.&lt;T&gt;]' is missing native code or metadata
///
/// while the untrimmed CLI keeps working. ILLink descriptors do NOT solve this: descriptors
/// root type *definitions*, and the reflection-style [[Type, Assembly]] closed-generic syntax
/// is accepted by the parser but has no effect. Measured directly: adding 50 such entries to
/// ILLink.Descriptors.xml left the AOT image byte-identical and all 72 failures in place.
///
/// Constructing the lists here makes them statically reachable, so ILC emits the code. The
/// module initializer keeps this rooted; the cost is a handful of empty allocations at load.
///
/// Add a line when a new UAssetAPI type is serialized as a List&lt;T&gt; and AOT starts failing.
/// </summary>
internal static class AotGenericRoots
{
    internal static object[]? Roots;

    [ModuleInitializer]
    internal static void Root()
    {
        Roots = new object[]
        {
            new List<UAssetAPI.FEngineVersion>(),
            new List<UAssetAPI.ExportTypes.SerializedInterfaceReference>(),
            new List<UAssetAPI.ExportTypes.FURL>(),
            new List<UAssetAPI.ExportTypes.ObjectMetaDataEntry>(),
            new List<UAssetAPI.ExportTypes.Texture.FOptTexturePlatformData>(),
            new List<UAssetAPI.Kismet.Bytecode.Expressions.FKismetSwitchCase>(),
            new List<UAssetAPI.PropertyTypes.Objects.FTopLevelAssetPath>(),
            new List<UAssetAPI.PropertyTypes.Objects.FSoftObjectPath>(),
            new List<UAssetAPI.PropertyTypes.Structs.FNavAgentSelector>(),
            new List<UAssetAPI.PropertyTypes.Structs.FStringCurveKey>(),
            new List<UAssetAPI.PropertyTypes.Structs.FMovieSceneEvaluationKey>(),
            new List<UAssetAPI.PropertyTypes.Structs.FMovieSceneSubSectionData>(),
            new List<UAssetAPI.PropertyTypes.Structs.FEntityAndMetaDataIndex>(),
            new List<UAssetAPI.PropertyTypes.Structs.FMovieSceneSubSequenceTreeEntry>(),
            new List<UAssetAPI.PropertyTypes.Structs.FMovieSceneSubSectionFieldData>(),
            new List<UAssetAPI.PropertyTypes.Structs.FMovieSceneEvaluationFieldEntityTree>(),
            new List<UAssetAPI.PropertyTypes.Structs.FMovieSceneSubSequenceTree>(),
            new List<UAssetAPI.PropertyTypes.Structs.FSectionEvaluationDataTree>(),
            new List<UAssetAPI.PropertyTypes.Structs.FMovieSceneTrackFieldData>(),
            new List<UAssetAPI.PropertyTypes.Structs.FEntry>(),
            new List<UAssetAPI.PropertyTypes.Structs.FEvaluationTreeEntryHandle>(),
            new List<UAssetAPI.PropertyTypes.Structs.FMovieSceneEvaluationTreeNodeHandle>(),
            new List<UAssetAPI.PropertyTypes.Structs.FMovieSceneEventParameters>(),
            new List<UAssetAPI.PropertyTypes.Structs.FNameCurveKey>(),
            new List<UAssetAPI.UnrealTypes.FGatherableTextData>(),
            new List<UAssetAPI.UnrealTypes.FObjectDataResource>(),
            new List<UAssetAPI.UnrealTypes.FTextSourceData>(),
            new List<UAssetAPI.UnrealTypes.FTextSourceSiteContext>(),
            new List<UAssetAPI.UnrealTypes.FIntVector>(),
            new List<UAssetAPI.UnrealTypes.FIntVector2>(),
            new List<UAssetAPI.UnrealTypes.FLinearColor>(),
            new List<UAssetAPI.UnrealTypes.FMatrix>(),
            new List<UAssetAPI.UnrealTypes.FPlane>(),
            new List<UAssetAPI.UnrealTypes.FQuat>(),
            new List<UAssetAPI.UnrealTypes.FRotator>(),
            new List<UAssetAPI.UnrealTypes.FTransform>(),
            new List<UAssetAPI.UnrealTypes.FTwoVectors>(),
            new List<UAssetAPI.UnrealTypes.FVector>(),
            new List<UAssetAPI.UnrealTypes.FVector2D>(),
            new List<UAssetAPI.UnrealTypes.FVector2f>(),
            new List<UAssetAPI.UnrealTypes.FVector3f>(),
            new List<UAssetAPI.UnrealTypes.FVector4>(),
            new List<UAssetAPI.UnrealTypes.FVector4f>(),
            new List<UAssetAPI.UnrealTypes.FFontCharacter>(),
            new List<UAssetAPI.UnrealTypes.FRichCurveKey>(),
            new List<UAssetAPI.UnrealTypes.FFrameNumber>(),
            new List<UAssetAPI.UnrealTypes.FFrameRate>(),
            new List<UAssetAPI.UnrealTypes.FFrameTime>(),
            new List<UAssetAPI.UnrealTypes.FQualifiedFrameTime>(),
            new List<UAssetAPI.UnrealTypes.FTimecode>(),
        };
    }
}
