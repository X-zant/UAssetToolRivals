import struct, sys

def read_fstring(d, o):
    n = struct.unpack_from('<i', d, o)[0]; o += 4
    if n == 0: return '', o
    if n < 0:
        s = d[o:o + -n * 2].decode('utf-16le').rstrip('\0'); return s, o - n * 2
    s = d[o:o + n].decode('latin1').rstrip('\0'); return s, o + n

def material_name_table(d):
    """FMaterialResourceMemoryWriter name table: int32 NumNames, then per name FString + 2x uint16 hash."""
    # Locate by trying every int32 count that parses into plausible names ending at a FMaterialResourceLocOnDisk array.
    best = None
    for start in range(0, len(d) - 8):
        n = struct.unpack_from('<i', d, start)[0]
        if not (3 <= n <= 4000): continue
        o = start + 4; names = []
        try:
            for _ in range(n):
                ln = struct.unpack_from('<i', d, o)[0]
                if not (2 <= ln <= 256): raise ValueError
                s, o = read_fstring(d, o)
                if not s or not all(32 <= ord(c) < 127 for c in s): raise ValueError
                o += 4
                names.append(s)
        except Exception:
            continue
        if any(x.startswith('F') and 'ShaderMap' in x for x in names):
            best = (start, names, o); break
    return best

def types(uexp):
    d = open(uexp, 'rb').read()
    start, names, after = material_name_table(d)
    out = []
    i = after
    while True:
        j = -1
        for flags in (5, 0x85):
            k = d.find(struct.pack('<IIII', 8, flags, 8, flags), i)
            if k >= 0 and (j < 0 or k < j): j = k
        if j < 0: break
        fsize = struct.unpack_from('<I', d, j + 16)[0]
        t = j + 20 + fsize
        n = struct.unpack_from('<i', d, t)[0]
        rows = []
        for e in range(n):
            ni, num, sz = struct.unpack_from('<iiI', d, t + 4 + e * 32)
            h = d[t + 16 + e * 32: t + 36 + e * 32].hex()
            rows.append((names[ni] + (f'_{num-1}' if num else ''), sz, h))
        out.append((j, fsize, rows))
        i = t
    return names, start, out

if __name__ == '__main__':
    a = {}
    for label, ue in [('stock', sys.argv[1]), ('game', sys.argv[2])]:
        names, start, maps = types(ue)
        print(f'{label}: name table @ {start} ({len(names)} names), {len(maps)} map(s): ' + ', '.join(f'@{j} frozen={fs}' for j, fs, _ in maps))
        a[label] = {}
        for (_, _, rows) in maps:
            for nm, sz, h in rows: a[label].setdefault(nm, (sz, h))
    print(f"{'type':58} {'stock':>6} {'game':>6}  hash")
    for nm in sorted(set(a['stock']) | set(a['game']), key=lambda x: (x not in a['stock'], x)):
        s = a['stock'].get(nm); g = a['game'].get(nm)
        same = '' if not (s and g) else ('same' if s[1] == g[1] else 'DIFF')
        print(f"{nm:58} {str(s[0]) if s else '-':>6} {str(g[0]) if g else '-':>6}  {same}")
