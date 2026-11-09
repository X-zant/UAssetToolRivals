using System.Text;

namespace ShaderMapPort;

/// <summary>
/// The cooked material-resource block at the end of a UMaterial / UMaterialInstance export
/// (UE 5.3 Material.cpp SerializeInlineShaderMaps, MaterialShared.cpp FMaterialResourceMemoryWriter):
///
///   int32 NumResources
///   int32 NumNames, NumNames x FNameEntrySerialized (FString + 2 x uint16 hash)
///   TArray&lt;FMaterialResourceLocOnDisk&gt; Locs   (uint32 Offset, uint8 FeatureLevel, uint8 QualityLevel)
///   int32 NumBytes, NumBytes of resources       (FNames inside are indices into the table above)
///
/// The game reads this block exactly like stock 5.3 (CUE4Parse reads game materials with the
/// stock id, pointer table and patch tables); only the frozen object inside differs.
/// </summary>
public sealed class MaterialResourceBlock
{
    public int NumResources;
    public List<(string Text, ushort Hash0, ushort Hash1)> Names = new();
    public List<(uint Offset, byte FeatureLevel, byte QualityLevel)> Locs = new();
    public List<MaterialResource> Resources = new();

    /// <summary>
    /// Find and parse the block near the end of <paramref name="extras"/>. Returns the block, its
    /// start and its end (a UMaterial export carries 4 more bytes after it, kept as they are).
    /// </summary>
    public static (MaterialResourceBlock Block, int Start, int End) Locate(byte[] extras)
    {
        // A resource count, a name table that parses, a Locs table with one entry per resource and a
        // byte count that lands within a few bytes of the export's end all have to agree, so a false
        // match is not a practical concern.
        for (int start = 0; start + 16 <= extras.Length; start++)
        {
            int n = BitConverter.ToInt32(extras, start);
            if (n < 1 || n > 64) continue;
            try
            {
                var block = Read(extras, start, out int end);
                if (end <= extras.Length && extras.Length - end <= 16) return (block, start, end);
            }
            catch (Exception) { }
        }
        throw new InvalidDataException("No cooked material-resource block found in the material export (was it cooked with shaders?)");
    }

    static MaterialResourceBlock Read(byte[] d, int start, out int end)
    {
        var r = new BinaryReader(new MemoryStream(d, start, d.Length - start));
        var b = new MaterialResourceBlock { NumResources = r.ReadInt32() };
        int numNames = r.ReadInt32();
        if (numNames < 1 || numNames > 100000) throw new InvalidDataException("name count");
        for (int i = 0; i < numNames; i++)
        {
            string s = ReadFString(r);
            b.Names.Add((s, r.ReadUInt16(), r.ReadUInt16()));
        }
        int numLocs = r.ReadInt32();
        if (numLocs != b.NumResources) throw new InvalidDataException("locs");
        for (int i = 0; i < numLocs; i++) b.Locs.Add((r.ReadUInt32(), r.ReadByte(), r.ReadByte()));
        int numBytes = r.ReadInt32();
        if (numBytes < 0 || r.BaseStream.Position + numBytes > r.BaseStream.Length) throw new InvalidDataException("byte count");
        byte[] bytes = r.ReadBytes(numBytes);
        if (b.Locs[0].Offset != 0) throw new InvalidDataException("locs");
        for (int i = 0; i < b.NumResources; i++)
        {
            int from = (int)b.Locs[i].Offset;
            int to = i + 1 < b.NumResources ? (int)b.Locs[i + 1].Offset : bytes.Length;
            b.Resources.Add(MaterialResource.Read(bytes, from, to));
        }
        end = start + (int)r.BaseStream.Position;
        return b;
    }

    public byte[] Write()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(NumResources);
        w.Write(Names.Count);
        foreach (var (text, h0, h1) in Names) { WriteFString(w, text); w.Write(h0); w.Write(h1); }
        var resourceBytes = new List<byte[]>();
        foreach (var res in Resources) resourceBytes.Add(res.Write());
        w.Write(Locs.Count);
        uint offset = 0;
        for (int i = 0; i < Locs.Count; i++)
        {
            w.Write(offset); w.Write(Locs[i].FeatureLevel); w.Write(Locs[i].QualityLevel);
            offset += (uint)resourceBytes[i].Length;
        }
        w.Write((int)offset);
        foreach (var rb in resourceBytes) w.Write(rb);
        return ms.ToArray();
    }

    public string NameText(int index, int number) =>
        index >= 0 && index < Names.Count ? (number == 0 ? Names[index].Text : $"{Names[index].Text}_{number - 1}") : $"#name{index}";

    internal static string ReadFString(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len == 0) return "";
        if (len > 0)
        {
            if (len > 4096) throw new InvalidDataException("string");
            var s = Encoding.Latin1.GetString(r.ReadBytes(len));
            return s.TrimEnd('\0');
        }
        if (-len > 4096) throw new InvalidDataException("string");
        return Encoding.Unicode.GetString(r.ReadBytes(-len * 2)).TrimEnd('\0');
    }

    internal static void WriteFString(BinaryWriter w, string s)
    {
        if (s.Length == 0) { w.Write(0); return; }
        bool ascii = s.All(c => c < 128);
        if (ascii) { w.Write(s.Length + 1); w.Write(Encoding.Latin1.GetBytes(s)); w.Write((byte)0); }
        else { w.Write(-(s.Length + 1)); w.Write(Encoding.Unicode.GetBytes(s)); w.Write((short)0); }
    }
}

/// <summary>Layout parameters as serialized (FPlatformTypeLayoutParameters: uint32 MaxFieldAlignment, uint32 Flags).</summary>
public struct LayoutParams
{
    public uint MaxFieldAlignment, Flags;
    public override string ToString() => $"MaxFieldAlignment={MaxFieldAlignment} Flags=0x{Flags:X}";
}

/// <summary>
/// One FMaterialResource as cooked inline (MaterialShared.cpp FMaterial::SerializeInlineShaderMap,
/// MaterialShader.cpp FMaterialShaderMap::Serialize, ShaderMap.cpp FShaderMapBase::Serialize,
/// MemoryImage.cpp FMemoryImageResult::SaveToArchive). FNames are (index into the block's name
/// table, number).
/// </summary>
public sealed class MaterialResource
{
    public bool Cooked, Valid;
    // FMaterialShaderMapId (cooked): QualityLevel, FeatureLevel, CookedShaderMapIdHash, LayoutParams
    public byte QualityLevel;
    public int FeatureLevel;
    public byte[] CookedShaderMapIdHash = new byte[20];
    public LayoutParams IdLayoutParams;
    // FMemoryImageResult
    public LayoutParams ImageLayoutParams;
    public byte[] Frozen = Array.Empty<byte>();
    public List<TypeDependency> TypeDependencies = new();
    public List<ulong> ShaderTypes = new(), VertexFactoryTypes = new();
    public List<(ulong TypeNameHash, List<(uint VTableOffset, uint Offset)> Patches)> VTables = new();
    public List<NamePatches> ScriptNames = new(), MemoryImageNames = new();
    // after the image
    public bool ShareCode;
    public (int Index, int Number) ShaderPlatformName;
    public byte[]? ResourceHash;
    /// <summary>Anything after the shader-map tail that this tool does not interpret (copied as is).</summary>
    public byte[] Trailer = Array.Empty<byte>();

    public sealed class TypeDependency
    {
        public int NameIndex, NameNumber;
        public uint LayoutSize;
        public byte[] LayoutHash = new byte[20];
    }

    public sealed class NamePatches
    {
        public int NameIndex, NameNumber;
        public List<uint> Offsets = new();
    }

    public static MaterialResource Read(byte[] d, int from, int to)
    {
        var r = new BinaryReader(new MemoryStream(d, from, to - from));
        var m = new MaterialResource();
        m.Cooked = r.ReadUInt32() != 0;
        if (!m.Cooked) throw new InvalidDataException("material resource is not cooked");
        m.Valid = r.ReadUInt32() != 0;
        if (!m.Valid) { m.Trailer = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position)); return m; }
        m.QualityLevel = r.ReadByte();
        m.FeatureLevel = r.ReadInt32();
        m.CookedShaderMapIdHash = r.ReadBytes(20);
        m.IdLayoutParams = new LayoutParams { MaxFieldAlignment = r.ReadUInt32(), Flags = r.ReadUInt32() };
        m.ImageLayoutParams = new LayoutParams { MaxFieldAlignment = r.ReadUInt32(), Flags = r.ReadUInt32() };
        uint frozenSize = r.ReadUInt32();
        m.Frozen = r.ReadBytes((int)frozenSize);
        int numDeps = r.ReadInt32();
        for (int i = 0; i < numDeps; i++)
            m.TypeDependencies.Add(new TypeDependency { NameIndex = r.ReadInt32(), NameNumber = r.ReadInt32(), LayoutSize = r.ReadUInt32(), LayoutHash = r.ReadBytes(20) });
        int numTypes = r.ReadInt32(), numVfTypes = r.ReadInt32();
        for (int i = 0; i < numTypes; i++) m.ShaderTypes.Add(r.ReadUInt64());
        for (int i = 0; i < numVfTypes; i++) m.VertexFactoryTypes.Add(r.ReadUInt64());
        uint numVTables = r.ReadUInt32(), numScriptNames = r.ReadUInt32(), numMemoryImageNames = r.ReadUInt32();
        for (int i = 0; i < numVTables; i++)
        {
            ulong hash = r.ReadUInt64();
            uint n = r.ReadUInt32();
            var patches = new List<(uint, uint)>();
            for (int k = 0; k < n; k++) patches.Add((r.ReadUInt32(), r.ReadUInt32()));
            m.VTables.Add((hash, patches));
        }
        for (int i = 0; i < numScriptNames; i++) m.ScriptNames.Add(ReadNamePatches(r));
        for (int i = 0; i < numMemoryImageNames; i++) m.MemoryImageNames.Add(ReadNamePatches(r));
        m.ShareCode = r.ReadUInt32() != 0;
        m.ShaderPlatformName = (r.ReadInt32(), r.ReadInt32());
        if (!m.ShareCode)
            throw new NotSupportedException("this material carries its shader code inline; cook with the shared shader library (Share Material Shader Code) so the code lives in the .ushaderbytecode");
        m.ResourceHash = r.ReadBytes(20);
        m.Trailer = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position));
        return m;
    }

    static NamePatches ReadNamePatches(BinaryReader r)
    {
        var p = new NamePatches { NameIndex = r.ReadInt32(), NameNumber = r.ReadInt32() };
        uint n = r.ReadUInt32();
        for (int k = 0; k < n; k++) p.Offsets.Add(r.ReadUInt32());
        return p;
    }

    public byte[] Write()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Cooked ? 1u : 0u);
        w.Write(Valid ? 1u : 0u);
        if (!Valid) { w.Write(Trailer); return ms.ToArray(); }
        w.Write(QualityLevel); w.Write(FeatureLevel); w.Write(CookedShaderMapIdHash);
        w.Write(IdLayoutParams.MaxFieldAlignment); w.Write(IdLayoutParams.Flags);
        w.Write(ImageLayoutParams.MaxFieldAlignment); w.Write(ImageLayoutParams.Flags);
        w.Write((uint)Frozen.Length); w.Write(Frozen);
        w.Write(TypeDependencies.Count);
        foreach (var t in TypeDependencies) { w.Write(t.NameIndex); w.Write(t.NameNumber); w.Write(t.LayoutSize); w.Write(t.LayoutHash); }
        w.Write(ShaderTypes.Count); w.Write(VertexFactoryTypes.Count);
        foreach (var t in ShaderTypes) w.Write(t);
        foreach (var t in VertexFactoryTypes) w.Write(t);
        w.Write((uint)VTables.Count); w.Write((uint)ScriptNames.Count); w.Write((uint)MemoryImageNames.Count);
        foreach (var (hash, patches) in VTables)
        {
            w.Write(hash); w.Write((uint)patches.Count);
            foreach (var (vt, off) in patches) { w.Write(vt); w.Write(off); }
        }
        foreach (var p in ScriptNames.Concat(MemoryImageNames))
        {
            w.Write(p.NameIndex); w.Write(p.NameNumber); w.Write((uint)p.Offsets.Count);
            foreach (var o in p.Offsets) w.Write(o);
        }
        w.Write(ShareCode ? 1u : 0u);
        w.Write(ShaderPlatformName.Index); w.Write(ShaderPlatformName.Number);
        w.Write(ResourceHash!);
        w.Write(Trailer);
        return ms.ToArray();
    }
}
