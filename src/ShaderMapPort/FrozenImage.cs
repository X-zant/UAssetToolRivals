using System.Buffers.Binary;

namespace ShaderMapPort;

// ---------------------------------------------------------------------------------------------
// Object model of a frozen memory image (UE 5.3 FMemoryImageResult::Bytes).
//
// Frozen pointers (FFrozenMemoryImagePtr, MemoryImage.h): bit 0 = frozen, bits 1..23 = type index
// into the pointer table's type dependencies, bits 24..63 = signed byte offset from the pointer's
// own position. Null = 0.
// ---------------------------------------------------------------------------------------------

public abstract class FrozenValue { }

public sealed class FrozenBytes : FrozenValue
{
    public byte[] Bytes;
    /// <summary>Where these bytes were in the source image (-1 when synthesized).</summary>
    public long SourceOffset = -1;
    public FrozenBytes(byte[] bytes, long sourceOffset = -1) { Bytes = bytes; SourceOffset = sourceOffset; }
}

public sealed class FrozenStruct : FrozenValue
{
    public string Type;
    public Dictionary<string, FrozenValue> Values = new();
    public long SourceOffset = -1;
    public FrozenStruct(string type) { Type = type; }
}

public enum FrozenArrayKind { Bytes, Pointers, Structs }

public sealed class FrozenArray : FrozenValue
{
    public FrozenArrayKind Kind;
    public int Count;
    /// <summary>Bytes: the element buffer (with any alignment padding that followed it).</summary>
    public byte[]? Bytes;
    public long SourceOffset = -1;
    public List<FrozenValue> Elements = new();
    public FrozenArray(FrozenArrayKind kind) { Kind = kind; }
}

public sealed class FrozenPtr : FrozenValue
{
    public FrozenStruct? Target;
    /// <summary>Index into the pointer table's type dependencies, or -1 (INDEX_NONE) for an untyped pointer.</summary>
    public int TypeIndex = -1;
}

public sealed class FrozenHashTable : FrozenValue
{
    public uint[] Hash = Array.Empty<uint>(), NextIndex = Array.Empty<uint>();
    public uint HashMask, IndexSize;
    public long HashOffset = -1, NextOffset = -1;
}

/// <summary>Reads a frozen image with a layout set into the object model.</summary>
public sealed class FrozenReader
{
    readonly byte[] img;
    readonly LayoutSet layouts;
    readonly IReadOnlyList<string> typeNames;
    readonly SortedSet<long> starts = new();
    readonly List<(long Start, long End, string What)> typed = new();
    readonly List<FrozenArray> rawArrays = new();
    readonly List<(long At, int Size, string What)> rawFields = new();
    readonly Dictionary<long, FrozenStruct> visited = new();

    public FrozenReader(byte[] image, LayoutSet layouts, IReadOnlyList<string> typeNames)
    {
        img = image; this.layouts = layouts; this.typeNames = typeNames;
    }

    public FrozenStruct ReadRoot(string rootType)
    {
        var rootLayout = ResolveLayout(rootType, "root");
        starts.Add(0);
        typed.Add((0, rootLayout.Size, rootType));
        var root = ReadStruct(0, rootLayout);
        // raw element buffers run to the next block (their elements carry no pointers)
        var all = starts.ToList();
        foreach (var a in rawArrays)
        {
            int i = all.BinarySearch(a.SourceOffset);
            long end = i + 1 < all.Count ? all[i + 1] : img.Length;
            a.Bytes = img.AsSpan((int)a.SourceOffset, (int)(end - a.SourceOffset)).ToArray();
        }
        Verify();
        return root;
    }

    TypeLayout ResolveLayout(string type, string where) =>
        layouts.Find(type) ?? Layouts.MeshShader(layouts, type)
        ?? throw new NotSupportedException($"{where}: frozen type '{type}' has no {layouts.Name} layout in this tool; refusing to guess its fields");

    FrozenStruct ReadStruct(long at, TypeLayout t)
    {
        var s = new FrozenStruct(t.Name) { SourceOffset = at };
        foreach (var f in t.Fields)
        {
            long p = at + f.Offset;
            switch (f.Kind)
            {
                case FieldKind.Raw:
                    s.Values[f.Name] = new FrozenBytes(img.AsSpan((int)p, f.Size).ToArray(), p);
                    rawFields.Add((p, f.Size, $"{t.Name}.{f.Name}"));
                    break;
                case FieldKind.Struct:
                    s.Values[f.Name] = ReadStruct(p, ResolveLayout(f.Type!, $"{t.Name}.{f.Name}"));
                    break;
                case FieldKind.Array:
                    s.Values[f.Name] = ReadArray(p, f, $"{t.Name}.{f.Name}");
                    break;
                case FieldKind.Ptr:
                    s.Values[f.Name] = ReadPtr(p, f.Type, $"{t.Name}.{f.Name}");
                    break;
                case FieldKind.HashTable:
                    s.Values[f.Name] = ReadHashTable(p, $"{t.Name}.{f.Name}");
                    break;
            }
        }
        return s;
    }

    (long Target, int TypeIndex) Pointer(long at)
    {
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan((int)at, 8));
        if (v == 0) return (-1, 0);
        if ((v & 1) == 0) throw new InvalidDataException($"unfrozen pointer at {at}");
        long off = (long)v >> 24;
        long target = at + off;
        if (target < 0 || target >= img.Length) throw new InvalidDataException($"pointer at {at} leaves the image ({target})");
        // FFrozenMemoryImagePtr::GetTypeIndex: stored + 1, so 0 is INDEX_NONE
        return (target, (int)((v >> 1) & 0x7FFFFF) - 1);
    }

    FrozenPtr ReadPtr(long at, string? declaredType, string where)
    {
        var (target, typeIndex) = Pointer(at);
        var ptr = new FrozenPtr { TypeIndex = typeIndex };
        if (target < 0) return ptr;
        string type = declaredType ?? (typeIndex >= 0 && typeIndex < typeNames.Count ? typeNames[typeIndex] : throw new InvalidDataException($"{where}: type index {typeIndex} is not in the type table"));
        if (visited.TryGetValue(target, out var seen)) { ptr.Target = seen; return ptr; }
        var layout = ResolveLayout(type, where);
        starts.Add(target);
        typed.Add((target, target + layout.Size, type));
        ptr.Target = ReadStruct(target, layout);
        visited[target] = ptr.Target;
        return ptr;
    }

    FrozenArray ReadArray(long at, Field f, string where)
    {
        var (data, _) = Pointer(at);
        int num = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan((int)at + 8, 4));
        var e = f.Element!;
        var a = new FrozenArray(e.Kind switch { FieldKind.Raw => FrozenArrayKind.Bytes, FieldKind.Ptr => FrozenArrayKind.Pointers, _ => FrozenArrayKind.Structs }) { Count = num, SourceOffset = data };
        if (num == 0 || data < 0) { a.Count = 0; a.SourceOffset = -1; return a; }
        starts.Add(data);
        switch (e.Kind)
        {
            case FieldKind.Raw:
                rawArrays.Add(a);
                break;
            case FieldKind.Ptr:
                typed.Add((data, data + 8L * num, $"{where}[]"));
                for (int i = 0; i < num; i++) a.Elements.Add(ReadPtr(data + 8L * i, e.Type, $"{where}[{i}]"));
                break;
            case FieldKind.Struct:
                typed.Add((data, data + (long)e.Stride * num, $"{where}[]"));
                var layout = ResolveLayout(e.Type!, where);
                for (int i = 0; i < num; i++) a.Elements.Add(ReadStruct(data + (long)e.Stride * i, layout));
                break;
        }
        return a;
    }

    FrozenHashTable ReadHashTable(long at, string where)
    {
        var h = new FrozenHashTable
        {
            HashMask = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)at + 16, 4)),
            IndexSize = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)at + 20, 4)),
        };
        var (hp, _) = Pointer(at);
        var (np, _) = Pointer(at + 8);
        if (hp >= 0) { h.HashOffset = hp; h.Hash = U32s(hp, (int)h.HashMask + 1); starts.Add(hp); typed.Add((hp, hp + 4L * (h.HashMask + 1), where + ".Hash")); }
        if (np >= 0) { h.NextOffset = np; h.NextIndex = U32s(np, (int)h.IndexSize); starts.Add(np); typed.Add((np, np + 4L * h.IndexSize, where + ".NextIndex")); }
        return h;
    }

    uint[] U32s(long at, int n)
    {
        var r = new uint[n];
        for (int i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)(at + 4L * i), 4));
        return r;
    }

    /// <summary>
    /// The walk must account for the whole image: every byte is inside a typed block, a raw element
    /// buffer, or is zero padding. And no raw bytes may hold something that decodes as a frozen
    /// pointer to a block start (that would be a pointer field this tool's layouts miss).
    /// </summary>
    void Verify()
    {
        var covered = new bool[img.Length];
        void Mark(long s, long e) { for (long i = Math.Max(0, s); i < Math.Min(e, img.Length); i++) covered[i] = true; }
        foreach (var (s, e, _) in typed) Mark(s, e);
        foreach (var a in rawArrays) Mark(a.SourceOffset, a.SourceOffset + a.Bytes!.Length);
        for (int i = 0; i < img.Length; i++)
            if (!covered[i] && img[i] != 0)
                throw new InvalidDataException($"byte {i} of the frozen image (0x{img[i]:X2}) is not accounted for by the {layouts.Name} layouts; a type here is laid out differently than this tool assumes");

        var blockStarts = new HashSet<long>(starts) { 0 };
        foreach (var (at, size, what) in rawFields)
            for (long p = (at + 7) & ~7L; p + 8 <= at + size; p += 8)
                if (LooksLikePointerTo(p, blockStarts))
                    throw new InvalidDataException($"{what} at {at} holds a frozen pointer at {p}; the layout treats it as plain bytes");
    }

    bool LooksLikePointerTo(long p, HashSet<long> blockStarts)
    {
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan((int)p, 8));
        if ((v & 1) == 0 || v == 1) return false;
        long off = (long)v >> 24;
        return off != 0 && blockStarts.Contains(p + off) && ((v >> 1) & 0x7FFFFF) < 4096;
    }
}

/// <summary>Writes the object model as a frozen image with a (possibly different) layout set.</summary>
public sealed class FrozenWriter
{
    readonly LayoutSet layouts;
    readonly List<byte> img = new();
    readonly Queue<Action> pending = new();
    readonly Dictionary<FrozenStruct, long> placed = new(ReferenceEqualityComparer.Instance);
    /// <summary>(source offset, length, destination offset) of every byte run carried over.</summary>
    public readonly List<(long Src, long Len, long Dst)> Moves = new();
    public readonly List<string> Defaults = new();

    public FrozenWriter(LayoutSet layouts) { this.layouts = layouts; }

    public byte[] Write(FrozenStruct root)
    {
        var layout = Resolve(root.Type);
        long at = Alloc(layout.Size);
        placed[root] = at;
        WriteStruct(root, layout, at, root.Type);
        while (pending.Count > 0) pending.Dequeue()();
        return img.ToArray();
    }

    TypeLayout Resolve(string type) =>
        layouts.Find(type) ?? Layouts.MeshShader(layouts, type) ?? throw new NotSupportedException($"no {layouts.Name} layout for '{type}'");

    long Alloc(int size, int align = 8)
    {
        while (img.Count % align != 0) img.Add(0);
        long at = img.Count;
        for (int i = 0; i < size; i++) img.Add(0);
        return at;
    }

    void Put(long at, ReadOnlySpan<byte> b) { for (int i = 0; i < b.Length; i++) img[(int)at + i] = b[i]; }
    void PutU32(long at, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); Put(at, b); }
    void PutPtr(long at, long target, int typeIndex)
    {
        Span<byte> b = stackalloc byte[8];
        ulong v = target < 0 ? 0 : ((ulong)(target - at) << 24) | ((ulong)(typeIndex + 1) << 1) | 1;
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        Put(at, b);
    }

    void WriteStruct(FrozenStruct s, TypeLayout t, long at, string where)
    {
        if (s.SourceOffset >= 0) Moves.Add((s.SourceOffset, 0, at));
        foreach (var f in t.Fields)
        {
            long p = at + f.Offset;
            s.Values.TryGetValue(f.Name, out var v);
            string here = $"{where}.{f.Name}";
            if (v == null)
            {
                if (f.DefaultStruct != null) v = f.DefaultStruct();
                else if (f.Default != null) v = new FrozenBytes(f.Default);
                else if (f.Kind == FieldKind.Struct) v = new FrozenStruct(f.Type!);
                else if (f.Kind == FieldKind.Array) v = new FrozenArray(FrozenArrayKind.Bytes);
                else if (f.Kind == FieldKind.Ptr) v = new FrozenPtr();
                else v = new FrozenBytes(new byte[f.Size]);
                if (s.SourceOffset >= 0) Defaults.Add(here);
            }
            switch (f.Kind)
            {
                case FieldKind.Raw:
                {
                    var b = ((FrozenBytes)v).Bytes;
                    if (b.Length > f.Size && b.AsSpan(f.Size).IndexOfAnyExcept((byte)0) >= 0)
                        throw new InvalidDataException($"{here}: {b.Length} source bytes do not fit the {f.Size}-byte target field");
                    int n = Math.Min(b.Length, f.Size);
                    Put(p, b.AsSpan(0, n));   // a wider target field is zero-extended
                    if (((FrozenBytes)v).SourceOffset >= 0) Moves.Add((((FrozenBytes)v).SourceOffset, n, p));
                    break;
                }
                case FieldKind.Struct:
                    WriteStruct((FrozenStruct)v, Resolve(f.Type!), p, here);
                    break;
                case FieldKind.Ptr:
                {
                    var ptr = (FrozenPtr)v;
                    if (ptr.Target == null) { PutPtr(p, -1, 0); break; }
                    var target = ptr.Target;
                    pending.Enqueue(() => PutPtr(p, Place(target), ptr.TypeIndex));
                    break;
                }
                case FieldKind.Array:
                {
                    var a = (FrozenArray)v;
                    PutU32(p + 8, (uint)a.Count); PutU32(p + 12, (uint)a.Count);
                    if (a.Count == 0) { PutPtr(p, -1, 0); break; }
                    pending.Enqueue(() => WriteArrayData(a, f, p, here));
                    break;
                }
                case FieldKind.HashTable:
                {
                    var h = (FrozenHashTable)v;
                    PutU32(p + 16, h.HashMask); PutU32(p + 20, h.IndexSize);
                    pending.Enqueue(() =>
                    {
                        PutPtr(p, h.Hash.Length > 0 ? U32Block(h.Hash, h.HashOffset) : -1, -1);
                        PutPtr(p + 8, h.NextIndex.Length > 0 ? U32Block(h.NextIndex, h.NextOffset) : -1, -1);
                    });
                    break;
                }
            }
        }
    }

    long U32Block(uint[] values, long src)
    {
        long at = Alloc(values.Length * 4, 4);
        for (int i = 0; i < values.Length; i++) PutU32(at + 4L * i, values[i]);
        if (src >= 0) Moves.Add((src, values.Length * 4L, at));
        return at;
    }

    long Place(FrozenStruct s)
    {
        if (placed.TryGetValue(s, out long at)) return at;
        var layout = Resolve(s.Type);
        at = Alloc(layout.Size);
        placed[s] = at;
        WriteStruct(s, layout, at, s.Type);
        return at;
    }

    void WriteArrayData(FrozenArray a, Field f, long fieldAt, string where)
    {
        long at;
        switch (a.Kind)
        {
            case FrozenArrayKind.Bytes:
                at = Alloc(a.Bytes!.Length);
                Put(at, a.Bytes);
                if (a.SourceOffset >= 0) Moves.Add((a.SourceOffset, a.Bytes.Length, at));
                break;
            case FrozenArrayKind.Pointers:
                at = Alloc(8 * a.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    var ptr = (FrozenPtr)a.Elements[i];
                    long slot = at + 8L * i;
                    if (ptr.Target == null) PutPtr(slot, -1, 0);
                    else { var target = ptr.Target; pending.Enqueue(() => PutPtr(slot, Place(target), ptr.TypeIndex)); }
                }
                break;
            default:
            {
                var elem = f.Element!;
                var layout = Resolve(elem.Type!);
                at = Alloc(elem.Stride * a.Count);
                for (int i = 0; i < a.Count; i++)
                    WriteStruct((FrozenStruct)a.Elements[i], layout, at + (long)elem.Stride * i, $"{where}[{i}]");
                break;
            }
        }
        PutPtr(fieldAt, at, -1);
    }

    /// <summary>Where a source-image offset ended up (for name and vtable patch tables).</summary>
    public long Map(long src)
    {
        foreach (var (s, len, d) in Moves)
            if (len == 0 ? src == s : src >= s && src < s + len) return d + (src - s);
        throw new InvalidDataException($"source offset {src} was not carried into the new image");
    }
}
