namespace ShaderMapPort;

/// <summary>
/// Frozen layouts of the types a cooked material shader map contains, for stock UE 5.3 (what the
/// editor cooks) and for Marvel Rivals (what the game loads).
///
/// Stock: UE 5.3 source, 64-bit, editor-only fields excluded (cooked layout parameters
/// MaxFieldAlignment=8, Flags=0x5).
///
/// Game: recovered from Marvel-Win64-Shipping.exe (S10.0, CL 3870120+):
///  - LAYOUT_FIELD descriptors initialised at static-init time (field name, offset), e.g.
///    FMaterialShader.MaterialRuntimeUniformBuffer @246, FMeshMaterialShader.ShellFurLayerFactor @258
///    and .MeshSectionIndex @264, FMaterialShaderMapContent.OrderedMeshShaderMaps @104,
///    .MaterialCompilationOutput @120, .ShaderContentHash @992.
///  - Bind() functions (this+offset per parameter name), e.g. FGPUSkinVertexFactoryShaderParameters
///    .BoneMap @44 and .BoneRefPosition @48.
///  - FMaterialCompilationOutput: RX-Port's decode of the game's own materials (two expression
///    sets; see RX-Port HANDOVER_2026-08-04.md and tools/matgraph/docs/scene-textures.md), checked
///    here against M_StylizedOutline / M_Common_Translucent.
/// Type layout hashes are not validated by a shipping build (UE_MEMORYIMAGE_TRACK_TYPE_DEPENDENCIES
/// is off), only the layout parameters and the bytes themselves, so sizes and offsets are what matter.
/// </summary>
public static class Layouts
{
    public const uint StockFlags = 0x5;   // Flag_Initialized | Flag_AlignBases
    public const uint GameFlags = 0x85;   // + NetEase bit 7

    static byte[] Ones(int n) => Enumerable.Repeat((byte)0xFF, n).ToArray();

    // ---- shared pieces (identical in both) ----

    static readonly Field[] ShaderMapContentStock =
    {
        Field.Hash("ShaderHash", 0),
        Field.Array("ShaderTypes", 24),
        Field.Array("ShaderPermutations", 40),
        Field.Array("Shaders", 56, ArrayElement.Pointer()),
        Field.Array("ShaderPipelines", 72, ArrayElement.Pointer("FShaderPipeline")),
        Field.Raw("ShaderPlatformName", 88, 8),   // FMemoryImageName, patched from MemoryImageNames
    };

    static IEnumerable<Field> UniformExpressionSet() => new[]
    {
        Field.Array("UniformPreshaders", 0),
        Field.Array("UniformPreshaderFields", 16),
        Field.Array("UniformNumericParameters", 32),
    }.Concat(Enumerable.Range(0, 7).Select(i => Field.Array($"UniformTextureParameters[{i}]", 48 + 16 * i)))
     .Concat(new[]
    {
        Field.Array("UniformExternalTextureParameters", 160),
        Field.Raw("UniformPreshaderBufferSize", 176, 4),
        Field.Inline("UniformPreshaderData", 184, "FMaterialPreshaderData"),
        Field.Array("DefaultValues", 248),
        Field.Array("VTStacks", 264),
        Field.Array("ParameterCollections", 280),
        Field.Inline("UniformBufferLayoutInitializer", 296, "FRHIUniformBufferLayoutInitializer"),
    });

    static readonly TypeLayout PreshaderData = new("FMaterialPreshaderData", 64, new[]
    {
        Field.Array("Names", 0), Field.Array("StructTypes", 16), Field.Array("StructComponentTypes", 32), Field.Array("Data", 48),
    });

    static readonly TypeLayout LayoutInitializer = new("FRHIUniformBufferLayoutInitializer", 128, new[]
    {
        Field.Array("Name", 0), Field.Array("Resources", 16), Field.Array("GraphResources", 32), Field.Array("GraphTextures", 48),
        Field.Array("GraphBuffers", 64), Field.Array("GraphUniformBuffers", 80), Field.Array("UniformBuffers", 96),
        Field.Raw("Hash..bUniformView", 112, 16),
    });

    static readonly TypeLayout LooseParameterBufferInfo = new("FShaderLooseParameterBufferInfo", 24, new[]
    {
        Field.Raw("BaseIndex", 0, 2), Field.Raw("Size", 2, 2), Field.Array("Parameters", 8),
    });

    static readonly TypeLayout ShaderPipeline = new("FShaderPipeline", 72, new[]
    {
        Field.Raw("TypeName", 0, 8),
        Field.Ptr("Shaders[0]", 8), Field.Ptr("Shaders[1]", 16), Field.Ptr("Shaders[2]", 24), Field.Ptr("Shaders[3]", 32), Field.Ptr("Shaders[4]", 40),
        Field.Raw("PermutationIds", 48, 20),
    });

    static IEnumerable<Field> ShaderBase() => new[]
    {
        // FShaderParameterBindings Bindings
        Field.Array("Bindings.Parameters", 0), Field.Array("Bindings.ResourceParameters", 16), Field.Array("Bindings.BindlessResourceParameters", 32),
        Field.Array("Bindings.GraphUniformBuffers", 48), Field.Array("Bindings.ParameterReferences", 64),
        Field.Raw("Bindings.StructureLayoutHash", 80, 4), Field.Raw("Bindings.RootParameterBufferIndex", 84, 2),
        // FShaderParameterMapInfo ParameterMapInfo
        Field.Array("ParameterMapInfo.UniformBuffers", 88), Field.Array("ParameterMapInfo.TextureSamplers", 104), Field.Array("ParameterMapInfo.SRVs", 120),
        Field.Array("ParameterMapInfo.LooseParameterBuffers", 136, ArrayElement.Of("FShaderLooseParameterBufferInfo", 24)),
        Field.Raw("ParameterMapInfo.Hash", 152, 8),
        Field.Array("UniformBufferParameterStructs", 160),
        Field.Array("UniformBufferParameters", 176),
        Field.Raw("Type", 192, 8), Field.Raw("VFType", 200, 8),   // TIndexedPtr: indices into the pointer table, not offsets
        Field.Raw("Target", 208, 4), Field.Raw("ResourceIndex", 212, 4), Field.Raw("NumInstructions", 216, 4), Field.Raw("SortKey", 220, 4),
        // FMaterialShader
        Field.Array("ParameterCollectionUniformBuffers", 224),
        Field.Raw("MaterialUniformBuffer", 240, 2),
        Field.Raw("VTFeedbackBuffer", 242, 4),
    };

    // ---- stock UE 5.3 ----

    public static LayoutSet Stock { get; } = BuildStock();

    static LayoutSet BuildStock()
    {
        var s = new LayoutSet("UE 5.3", StockFlags);
        s.Add(new TypeLayout("FMaterialShaderMapContent", 572, ShaderMapContentStock.Concat(new[]
        {
            Field.Array("OrderedMeshShaderMaps", 96, ArrayElement.Pointer("FMeshMaterialShaderMap")),
            Field.Inline("MaterialCompilationOutput", 112, "FMaterialCompilationOutput"),
            Field.Raw("ShaderContentHash", 552, 20),
        })));
        s.Add(new TypeLayout("FMeshMaterialShaderMap", 104, ShaderMapContentStock.Append(Field.Raw("VertexFactoryTypeName", 96, 8))));
        s.Add(new TypeLayout("FMaterialCompilationOutput", 440, new[]
        {
            Field.Inline("UniformExpressionSet", 0, "FUniformExpressionSet"),
            Field.Raw("UsedSceneTextures", 424, 4),
            Field.Raw("UsedPathTracingBufferTextures", 428, 1),
            Field.Raw("StrataMaterialCompilationOutput", 429, 4),
            Field.Raw("UsedDBufferTextures", 433, 1),
            Field.Raw("RuntimeVirtualTextureOutputAttributeMask", 434, 1),
            Field.Raw("Bitfields", 435, 2),   // bNeedsSceneTextures .. (two bytes of LAYOUT_BITFIELD)
        }));
        AddShared(s);
        s.Add(MeshMaterialShader("FMeshMaterialShader", stock: true, extra: 0));
        s.Add(MaterialShader(stock: true));
        // FVertexFactoryShaderParameters-derived (polymorphic, via FMeshMaterialShader.VertexFactoryParameters)
        s.Add(new TypeLayout("FGPUSkinVertexFactoryShaderParameters", 44, new[] { Field.Raw("Stock", 0, 44) }));
        // cloth: FGPUSkinVertexFactoryShaderParameters (44) + ClothSimulVertsPositionsNormals, Previous.. (2 x 4),
        // ClothToLocal, PreviousClothToLocal, ClothBlendWeight (3 x 6) | WorldScale (6), GPUSkinApexCloth (4),
        // GPUSkinApexClothStartIndexOffset, ClothNumInfluencesPerVertex (2 x 6) = 92
        s.Add(new TypeLayout("TGPUSkinAPEXClothVertexFactoryShaderParameters", 92, new[]
        {
            Field.Raw("Stock", 0, 44), Field.Raw("ClothA", 44, 26), Field.Raw("ClothB", 70, 22),
        }));
        return s;
    }

    // ---- Marvel Rivals ----

    public static LayoutSet Game { get; } = BuildGame();

    static LayoutSet BuildGame()
    {
        var g = new LayoutSet("Marvel Rivals", GameFlags);
        // FShaderMapContent grows by 8 bytes after ShaderPlatformName (zero in every game material
        // read so far; CUE4Parse skips the same 8 for this game).
        var content = ShaderMapContentStock.Append(Field.Raw("NetEase@96", 96, 8, new byte[8])).ToArray();
        g.Add(new TypeLayout("FMaterialShaderMapContent", 1012, content.Concat(new[]
        {
            Field.Array("OrderedMeshShaderMaps", 104, ArrayElement.Pointer("FMeshMaterialShaderMap")),
            Field.Inline("MaterialCompilationOutput", 120, "FMaterialCompilationOutput"),
            Field.Raw("ShaderContentHash", 992, 20),
        })));
        g.Add(new TypeLayout("FMeshMaterialShaderMap", 112, content.Append(Field.Raw("VertexFactoryTypeName", 104, 8))));
        g.Add(new TypeLayout("FMaterialCompilationOutput", 872, new[]
        {
            Field.Inline("UniformExpressionSet", 0, "FUniformExpressionSet"),
            // 64-bit: the game has 49 ESceneTextureId values (stock 0..30 unchanged, then game-only)
            Field.Raw("UsedSceneTextures", 424, 8),
            Field.Raw("UsedPathTracingBufferTextures", 432, 1),
            Field.Raw("NetEase@433", 433, 1, new byte[] { 1 }),   // 1 in every game material read so far
            // the MaterialRuntime expression set (fills the shaders' MaterialRuntime constant buffer)
            new Field("RuntimeExpressionSet", 440, FieldKind.Struct, Type: "FUniformExpressionSet") { DefaultStruct = EmptyRuntimeExpressionSet },
            Field.Raw("StrataMaterialCompilationOutput", 864, 4),
            Field.Raw("UsedDBufferTextures", 868, 1),
            Field.Raw("RuntimeVirtualTextureOutputAttributeMask", 869, 1),
            Field.Raw("Bitfields", 870, 2),
        }));
        AddShared(g, game: true);
        g.Add(MeshMaterialShader("FMeshMaterialShader", stock: false, extra: 0));
        g.Add(MaterialShader(stock: false));
        g.Add(new TypeLayout("FGPUSkinVertexFactoryShaderParameters", 52, new[]
        {
            Field.Raw("Stock", 0, 44),
            // FShaderResourceParameter; unbound = all zero (IsBound() is NumResources > 0)
            Field.Raw("BoneMap", 44, 4, new byte[4]),
            Field.Raw("BoneRefPosition", 48, 4, new byte[4]),
        }));
        // cloth (game Bind at 0x3c9d8c0): base 52, then the stock fields with IsVertexColorRWorkForClothBlend
        // (FShaderParameter, unbound = 0) inserted after ClothBlendWeight: 52..78 | 78 new | 84..106
        g.Add(new TypeLayout("TGPUSkinAPEXClothVertexFactoryShaderParameters", 106, new[]
        {
            Field.Raw("Stock", 0, 44),
            Field.Raw("BoneMap", 44, 4, new byte[4]), Field.Raw("BoneRefPosition", 48, 4, new byte[4]),
            Field.Raw("ClothA", 52, 26),
            Field.Raw("IsVertexColorRWorkForClothBlend", 78, 6, new byte[6]),
            Field.Raw("ClothB", 84, 22),
        }));
        return g;
    }

    /// <summary>
    /// Vertex factory parameter structs whose type layout hash is the same in the game as in stock 5.3
    /// (so the bytes carry over unchanged); name -> size.
    /// </summary>
    static (string Name, int Size)[] IdenticalVertexFactoryParameters => new[]
    {
        ("FLocalVertexFactoryShaderParameters", 18),
        ("FNiagaraSpriteVertexFactoryShaderParametersVS", 36),
        ("FNiagaraSpriteVertexFactoryShaderParametersPS", 4),
        ("FNiagaraRibbonVertexFactoryShaderParametersVS", 4),
        ("FNiagaraRibbonVertexFactoryShaderParametersPS", 4),
        ("FNiagaraMeshVertexFactoryShaderParametersVS", 4),
        ("FNiagaraMeshVertexFactoryShaderParametersPS", 4),
        ("FInstancedStaticMeshVertexFactoryShaderParameters", 36),
        ("FSplineMeshVertexFactoryShaderParameters", 10),
        ("FGeometryCacheVertexFactoryShaderParameters", 34),
        ("FGeometryCollectionVertexFactoryShaderParameters", 4),
        ("FParticleSpriteVertexFactoryShaderParametersVS", 14),
        ("FParticleSpriteVertexFactoryShaderParametersPS", 4),
        ("FMeshParticleVertexFactoryShaderParameters", 8),
        ("FParticleBeamTrailVertexFactoryShaderParameters", 4),
        ("FGPUSpriteVertexFactoryShaderParametersVS", 46),
        ("FGPUSpriteVertexFactoryShaderParametersPS", 4),
    };

    static void AddShared(LayoutSet set, bool game = false)
    {
        foreach (var (name, size) in IdenticalVertexFactoryParameters)
            set.Add(new TypeLayout(name, size, new[] { Field.Raw("Stock", 0, size) }));
        set.Add(new TypeLayout("FUniformExpressionSet", 424, UniformExpressionSet()));
        set.Add(PreshaderData);
        set.Add(LayoutInitializer);
        set.Add(LooseParameterBufferInfo);
        set.Add(ShaderPipeline);
    }

    /// <summary>
    /// FShader + FMaterialShader + FMeshMaterialShader. Game: MaterialRuntimeUniformBuffer (u16) in
    /// FMaterialShader's padding at 246 (unbound = 0xFFFF), and ShellFurLayerFactor (FShaderParameter @258)
    /// plus MeshSectionIndex (FShaderParameter @264) at the end of FMeshMaterialShader (unbound = zeros),
    /// which moves every derived shader's own fields from 264 to 272.
    /// </summary>
    static TypeLayout MeshMaterialShader(string name, bool stock, int extra)
    {
        var f = ShaderBase().ToList();
        if (!stock) f.Add(Field.Raw("MaterialRuntimeUniformBuffer", 246, 2, Ones(2)));
        f.Add(Field.Ptr("VertexFactoryParameters", 248));
        f.Add(Field.Raw("PassUniformBuffer", 256, 2));
        if (!stock)
        {
            f.Add(Field.Raw("ShellFurLayerFactor", 258, 6, new byte[6]));
            f.Add(Field.Raw("MeshSectionIndex", 264, 6, new byte[6]));
        }
        return new TypeLayout(name, stock ? 264 : 272, f);
    }

    /// <summary>FShader + FMaterialShader (base of post process, Slate and decal material shaders): 248 in both builds.</summary>
    static TypeLayout MaterialShader(bool stock)
    {
        var f = ShaderBase().ToList();
        if (!stock) f.Add(Field.Raw("MaterialRuntimeUniformBuffer", 246, 2, Ones(2)));
        return new TypeLayout("FMaterialShader", 248, f);
    }

    /// <summary>
    /// Mesh-material shader types this tool knows how to move, with the size of their own fields
    /// past FMeshMaterialShader. Only types whose own fields are the same in the game are listed;
    /// anything else is refused rather than guessed.
    ///
    /// TBasePass{VS,PS}&lt;FUniformLightMapPolicy&gt;: FUniformLightMapPolicyShaderParametersType (3 x
    /// FShaderUniformBufferParameter) + ReflectionCaptureBuffer = 8 bytes, at 264 stock / 272 game
    /// (game: TBasePassVertexShaderPolicyParamType BASE @272, ReflectionCaptureBuffer @278).
    /// </summary>
    public static readonly Dictionary<string, int> MeshShaderOwnFields = new()
    {
        ["TBasePassVSFNoLightMapPolicy"] = 8,
        ["TBasePassPSFNoLightMapPolicy"] = 8,
        ["TBasePassPSFNoLightMapPolicySkylight"] = 8,
        ["TBasePassVSFSelfShadowedTranslucencyPolicy"] = 8,
        ["TBasePassPSFSelfShadowedTranslucencyPolicy"] = 8,
        ["TBasePassPSFSelfShadowedTranslucencyPolicySkylight"] = 8,
        // FVelocity{VS,PS}: no own fields (stock 264 = FMeshMaterialShader; game 272, see M_Common_Translucent_Lobby)
        ["FVelocityVS"] = 0,
        ["FVelocityPS"] = 0,
        // FHitProxyVS: FShaderResourceParameter VertexFetch_HitProxyIdBuffer (4); FHitProxyPS: FShaderParameter HitProxyId (6)
        // (stock 268/270, game 276/278)
        ["FHitProxyVS"] = 4,
        ["FHitProxyPS"] = 6,
        // FLumenCard{VS,PS<false>}: no own fields (stock 264, game 272, see M_Common_Hair)
        ["FLumenCardVS"] = 0,
        ["FLumenCardPS<false>"] = 0,
        // depth prepass / masked depth: no own fields (game: M_Common_Body)
        ["TDepthOnlyVS<false>"] = 0,
        ["TDepthOnlyVS<true>"] = 0,
        ["FDepthOnlyPS"] = 0,
        // shadow depth: VS = FShadowDepthVS LayerId + bUseGpuSceneInstancing (2 x FShaderParameter, 16 padded);
        // PS: none (stock 280/264, game 288/272, M_Common_Body). The VirtualShadowMap permutations do not
        // exist in the game (no VSM); the project cooks with r.Shadow.Virtual.Enable=0.
        ["TShadowDepthVSVertexShadowDepth_PerspectiveCorrect"] = 16,
        ["TShadowDepthVSVertexShadowDepth_OutputDepth"] = 16,
        ["TShadowDepthVSVertexShadowDepth_OnePassPointLight"] = 16,
        ["TShadowDepthPSPixelShadowDepth_PerspectiveCorrect"] = 0,
        ["TShadowDepthPSPixelShadowDepth_NonPerspectiveCorrect"] = 0,
        ["TShadowDepthPSPixelShadowDepth_OnePassPointLight"] = 0,
        // lit translucency with per-pixel (surface) lighting: no own fields (game: M_Skin_Water)
        ["FLumenFrontLayerTranslucencyGBufferVS"] = 0,
        ["FLumenFrontLayerTranslucencyGBufferPS"] = 0,
        ["FLumenTranslucencyRadianceCacheMarkVS"] = 0,
        ["FLumenTranslucencyRadianceCacheMarkPS"] = 0,
        // decal material on a mesh: no own fields (game: M_Common_SprayPaint)
        ["FMeshDecalsVS"] = 0,
        ["FMeshDecalsPS"] = 0,
        ["FMeshDecalsEmissivePS"] = 0,           // derived from FMeshDecalsPS, no fields of its own
        ["FMeshDecalsAmbientOcclusionPS"] = 0,   // same (game 272)
        // anisotropy pass (game: M_Common_Hair), refraction (game: M_Common_Glass): no own fields
        ["FAnisotropyVS"] = 0,
        ["FAnisotropyPS"] = 0,
        ["FDistortionMeshVS"] = 0,
        ["FDistortionMeshPS"] = 0,
        // runtime virtual texture output: no own fields (game: M_StandardLandscape)
        ["TVirtualTextureVSBaseColor"] = 0,
        ["TVirtualTextureVSBaseColorNormalRoughness"] = 0,
        ["TVirtualTextureVSBaseColorNormalSpecular"] = 0,
        ["TVirtualTextureVSWorldHeight"] = 0,
        ["TVirtualTexturePSBaseColor"] = 0,
        ["TVirtualTexturePSBaseColorNormalRoughness"] = 0,
        ["TVirtualTexturePSBaseColorNormalSpecular"] = 0,
        ["TVirtualTexturePSWorldHeight"] = 0,
        // volume domain voxelization: VS/GS one FShaderParameter (8 padded), PS none (stock 272/264, game 280/272;
        // game: M_Volume_13_301)
        ["TVoxelizeVolumeVS<VMode_Object_Box>"] = 8,
        ["TVoxelizeVolumeVS<VMode_Primitive_Sphere>"] = 8,
        ["TVoxelizeVolumeGS<VMode_Object_Box>"] = 8,
        ["TVoxelizeVolumeGS<VMode_Primitive_Sphere>"] = 8,
        ["TVoxelizeVolumePS<VMode_Object_Box>"] = 0,
        ["TVoxelizeVolumePS<VMode_Primitive_Sphere>"] = 0,
    };

    /// <summary>
    /// Shader types whose game version appends fields after the stock ones: (base is FMeshMaterialShader?, stock own
    /// bytes, game own bytes). The stock fields keep their offsets; the appended ones are written unbound (all zero:
    /// FShaderParameter / FShaderResourceParameter report IsBound() false). Offsets from the game's Bind() functions.
    /// </summary>
    public static readonly Dictionary<string, (bool Mesh, int StockOwn, int GameOwn)> AppendedOwnFields = new()
    {
        // FLightFunctionVS: stock FStencilingGeometryShaderParameters (18 @248); game adds LightMaskOn @266 and
        // InvReceiverInnerMatrix @272 (Bind at 0x1d21080), 278 total (game: M_CausticsLightfunction)
        ["FLightFunctionVS"] = (false, 18, 30),
        // FDeferredDecalPS: stock 7 x FShaderParameter + MobileBasePassUniformBuffer (44 @248); game appends
        // DecalPOMTransform @292, DecalDynamicParameter0..3 @298..316, ProvidePrimitiveId @322, DecalSectionIndex @328,
        // LightingChannel @334 (Bind at 0x1b23380), 340 total (game: M_Common_SprayPaint)
        ["FDeferredDecalPS"] = (false, 44, 92),
        // the decal variants derive from FDeferredDecalPS without fields of their own (stock); the game's sizes for
        // them (344) are the same fields padded to 8
        ["FDeferredDecalEmissivePS"] = (false, 44, 92),
        ["FDeferredDecalAmbientOcclusionPS"] = (false, 44, 92),
        ["FDeferredDecalShadingModelPS"] = (false, 44, 92),
    };

    /// <summary>
    /// Material (non-mesh) shader types, with the size of their own fields past FMaterialShader (248 in both
    /// builds; the game's MaterialRuntimeUniformBuffer sits in FMaterialShader's padding).
    ///
    /// Not listed, on purpose: FDeferredDecalPS (stock 292, game 340: the game adds 48 bytes of fields this
    /// tool has not mapped), so deferred decal materials are refused.
    /// </summary>
    public static readonly Dictionary<string, int> MaterialShaderOwnFields = new()
    {
        // post process material: no own fields (248 in both, game: M_PP_Transparent)
        ["FPostProcessMaterialVS"] = 0,
        ["FPostProcessMaterialPS"] = 0,
        // UMG / Slate material: VS = ViewProjection (6 -> 8), PS = 4 x FShaderParameter + 2 x FShaderResourceParameter
        // (32); same sizes in the game (M_Common_Add_BattllePass)
        ["TSlateMaterialShaderVSfalse"] = 8,
        ["TSlateMaterialShaderVStrue"] = 8,
        ["TSlateMaterialShaderPSDefault"] = 32,
        ["TSlateMaterialShaderPSBorder"] = 32,
        ["TSlateMaterialShaderPSGrayscaleFont"] = 32,
        ["TSlateMaterialShaderPSColorFont"] = 32,
        ["TSlateMaterialShaderPSCustom"] = 32,
        ["TSlateMaterialShaderPSRoundedBox"] = 32,
        // light function domain: PS = SvPositionToLight, LightFunctionParameters, LightFunctionParameters2 (3 x 6) +
        // HairOnlyDepthTexture (4) = 22, identical in the game (270; checked against M_CausticsLightfunction's cooked
        // parameter bindings); the Lumen / translucent-lighting-inject / volumetric-fog light function shaders add none
        ["FLightFunctionPS"] = 22,
        ["FLumenDirectLightingNonRayTracedShadowsWithLightFunctionCS"] = 0,
        ["FTranslucentLightingInjectPS"] = 0,
        ["FVolumetricFogLightFunctionPS"] = 0,
    };

    /// <summary>The layout of a concrete material shader type: FMeshMaterialShader / FMaterialShader + its own fields as bytes.</summary>
    public static TypeLayout? MeshShader(LayoutSet set, string typeName)
    {
        TypeLayout baseLayout;
        if (AppendedOwnFields.TryGetValue(typeName, out var app))
        {
            baseLayout = set.Get(app.Mesh ? "FMeshMaterialShader" : "FMaterialShader");
            bool game = set.Flags == GameFlags;
            var fields = baseLayout.Fields.Append(Field.Raw("OwnFields", baseLayout.Size, app.StockOwn));
            if (game) fields = fields.Append(Field.Raw("GameAppendedFields", baseLayout.Size + app.StockOwn, app.GameOwn - app.StockOwn, new byte[app.GameOwn - app.StockOwn]));
            return new TypeLayout(typeName, baseLayout.Size + (game ? app.GameOwn : app.StockOwn), fields);
        }
        if (MeshShaderOwnFields.TryGetValue(typeName, out int own)) baseLayout = set.Get("FMeshMaterialShader");
        else if (MaterialShaderOwnFields.TryGetValue(typeName, out own)) baseLayout = set.Get("FMaterialShader");
        else return null;
        return new TypeLayout(typeName, baseLayout.Size + own, baseLayout.Fields.Append(Field.Raw("OwnFields", baseLayout.Size, own)));
    }

    /// <summary>
    /// An empty MaterialRuntime expression set, as the game cooks it for a material with no runtime
    /// parameters (M_StylizedOutline): every array empty, buffer size 0, uniform buffer layout named
    /// "MaterialRuntime" with no members.
    /// </summary>
    public static FrozenStruct EmptyRuntimeExpressionSet()
    {
        var ues = new FrozenStruct("FUniformExpressionSet");
        var layout = new FrozenStruct("FRHIUniformBufferLayoutInitializer");
        var name = System.Text.Encoding.Unicode.GetBytes("MaterialRuntime\0");
        layout.Values["Name"] = new FrozenArray(FrozenArrayKind.Bytes) { Count = name.Length / 2, Bytes = name };
        layout.Values["Hash..bUniformView"] = new FrozenBytes(Convert.FromHexString("0001000000000000FFFFFF0100000000"));
        ues.Values["UniformBufferLayoutInitializer"] = layout;
        return ues;
    }
}
