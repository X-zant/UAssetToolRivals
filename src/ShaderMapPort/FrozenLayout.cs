namespace ShaderMapPort;

/// <summary>How a field is frozen (UE 5.3 MemoryImage.h / MemoryLayout.h).</summary>
public enum FieldKind
{
    /// <summary>Plain bytes with no frozen pointers in them.</summary>
    Raw,
    /// <summary>An inline struct, described by its own <see cref="TypeLayout"/>.</summary>
    Struct,
    /// <summary>TMemoryImageArray: frozen pointer (8), int32 Num, int32 Max.</summary>
    Array,
    /// <summary>TMemoryImagePtr: frozen pointer (8). Polymorphic when <see cref="Field.Type"/> is null.</summary>
    Ptr,
    /// <summary>FMemoryImageHashTable: Hash ptr, NextIndex ptr (uint32 buffers), uint32 HashMask, uint32 IndexSize.</summary>
    HashTable,
}

/// <summary>Element of a TMemoryImageArray.</summary>
public sealed record ArrayElement(FieldKind Kind, int Stride, string? Type = null)
{
    /// <summary>Elements with no pointers: the whole buffer is carried as bytes.</summary>
    public static readonly ArrayElement Bytes = new(FieldKind.Raw, 0);
    /// <summary>TMemoryImagePtr elements (polymorphic when <paramref name="type"/> is null).</summary>
    public static ArrayElement Pointer(string? type = null) => new(FieldKind.Ptr, 8, type);
    public static ArrayElement Of(string type, int stride) => new(FieldKind.Struct, stride, type);
}

public sealed record Field(string Name, int Offset, FieldKind Kind, int Size = 0, string? Type = null, ArrayElement? Element = null)
{
    /// <summary>Value for a field the source layout does not have.</summary>
    public byte[]? Default { get; init; }
    /// <summary>Builds the value for a field the source layout does not have, when bytes are not enough.</summary>
    public Func<FrozenStruct>? DefaultStruct { get; init; }

    public static Field Raw(string name, int offset, int size, byte[]? def = null) => new(name, offset, FieldKind.Raw, size) { Default = def };
    public static Field Inline(string name, int offset, string type) => new(name, offset, FieldKind.Struct, Type: type);
    public static Field Array(string name, int offset, ArrayElement? elem = null) => new(name, offset, FieldKind.Array, 16, Element: elem ?? ArrayElement.Bytes);
    public static Field Ptr(string name, int offset, string? type = null) => new(name, offset, FieldKind.Ptr, 8, type);
    public static Field Hash(string name, int offset) => new(name, offset, FieldKind.HashTable, 24);
}

public sealed class TypeLayout
{
    public string Name { get; }
    public int Size { get; }
    public IReadOnlyList<Field> Fields { get; }
    public TypeLayout(string name, int size, IEnumerable<Field> fields)
    {
        Name = name; Size = size; Fields = fields.ToList();
        foreach (var f in Fields)
            if (f.Offset < 0 || f.Offset + Math.Max(f.Size, 0) > size && f.Kind != FieldKind.Struct)
                throw new ArgumentException($"{name}.{f.Name} lies outside the type ({f.Offset}+{f.Size} > {size})");
    }
    public Field? Find(string name) => Fields.FirstOrDefault(f => f.Name == name);
}

/// <summary>A set of frozen type layouts: one target platform's view of the types in a shader map.</summary>
public sealed class LayoutSet
{
    public string Name { get; }
    /// <summary>FPlatformTypeLayoutParameters::Flags this layout set is frozen with.</summary>
    public uint Flags { get; }
    readonly Dictionary<string, TypeLayout> types = new();
    public LayoutSet(string name, uint flags) { Name = name; Flags = flags; }
    public LayoutSet Add(TypeLayout t) { types[t.Name] = t; return this; }
    public TypeLayout? Find(string name) => types.TryGetValue(name, out var t) ? t : null;
    public TypeLayout Get(string name) => Find(name) ?? throw new KeyNotFoundException($"{Name} layout has no type '{name}'");
    public IEnumerable<TypeLayout> All => types.Values;
}
