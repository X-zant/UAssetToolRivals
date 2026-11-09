"""Recover FFieldLayoutDesc initialisation (name -> desc address, offset) from the shipping exe.

UE 5.3 LAYOUT_FIELD builds a function-local static FFieldLayoutDesc at static-init time:
  FieldDesc.Name = TEXT("X"); FieldDesc.Type = &...; FieldDesc.Offset = offsetof(...); ...
So next to every RIP-relative reference to the field-name string there are stores into the desc.
FFieldLayoutDesc: +0 Name, +8 Type, +0x10 Next, +0x18 WriteFunc, +0x20 Offset(u32), +0x24 NumArray(u32), +0x28 Flags(u8), BitFieldSize, UFieldNameLength
"""
import struct, sys, mmap, re, pickle, os

EXE = sys.argv[1]
f = open(EXE, 'rb'); d = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
pe = struct.unpack_from('<I', d, 0x3C)[0]
nsec = struct.unpack_from('<H', d, pe + 6)[0]
optsz = struct.unpack_from('<H', d, pe + 20)[0]
imagebase = struct.unpack_from('<Q', d, pe + 24 + 24)[0]
secs = []
for i in range(nsec):
    o = pe + 24 + optsz + i * 40
    name = d[o:o+8].rstrip(b'\0').decode('latin1')
    vsize, va, rsize, raw = struct.unpack_from('<IIII', d, o + 8)
    secs.append((name, va, vsize, raw, rsize))
def off2rva(off):
    for n, va, vs, raw, rs in secs:
        if raw <= off < raw + rs: return va + off - raw
def rva2off(rva):
    for n, va, vs, raw, rs in secs:
        if va <= rva < va + rs: return raw + rva - va
print('sections:', [(s[0], hex(s[1]), hex(s[2])) for s in secs])

def find_wstr(s):
    pat = s.encode('utf-16le') + b'\0\0'
    res = []
    for m in re.finditer(re.escape(pat), d):
        # must be the start of the string (previous wchar is NUL or not a letter)
        if m.start() >= 2 and d[m.start()-2:m.start()] != b'\0\0' and chr(d[m.start()-2]).isalnum(): continue
        res.append(off2rva(m.start()))
    return res

text = [s for s in secs if s[0] == '.text'][0]
tva, tvs, traw, trs = text[1], text[2], text[3], text[4]

def xrefs(target_rva):
    """RIP-relative LEA r64,[rip+disp32] (48/4C 8D modrm with mod=00 rm=101)"""
    out = []
    t = d[traw:traw+trs]
    for m in re.finditer(rb'[\x48\x4C]\x8D[\x05\x0D\x15\x1D\x25\x2D\x35\x3D]', t):
        i = m.start()
        disp = struct.unpack_from('<i', t, i + 3)[0]
        if tva + i + 7 + disp == target_rva: out.append(tva + i)
    return out

def stores_after(lea_rva, window=200):
    """Collect RIP-relative stores following the LEA: mov [rip+d],r64 (48/4C 89 modrm) and mov dword [rip+d],imm32 (C7 05)."""
    o = rva2off(lea_rva)
    res = []
    i = 0
    while i < window:
        b = d[o+i:o+i+11]
        if b[0] in (0x48, 0x4C) and b[1] == 0x89 and (b[2] & 0xC7) == 0x05:
            disp = struct.unpack_from('<i', b, 3)[0]; res.append(('mov64', lea_rva + i + 7 + disp, None)); i += 7; continue
        if b[0] == 0xC7 and b[1] == 0x05:
            disp = struct.unpack_from('<i', b, 2)[0]; imm = struct.unpack_from('<I', b, 6)[0]
            res.append(('mov32', lea_rva + i + 10 + disp, imm)); i += 10; continue
        if b[0] == 0x66 and b[1] == 0xC7 and b[2] == 0x05:
            disp = struct.unpack_from('<i', b, 3)[0]; imm = struct.unpack_from('<H', b, 7)[0]
            res.append(('mov16', lea_rva + i + 9 + disp, imm)); i += 9; continue
        if b[0] == 0xC6 and b[1] == 0x05:
            disp = struct.unpack_from('<i', b, 2)[0]; imm = b[6]
            res.append(('mov8', lea_rva + i + 7 + disp, imm)); i += 7; continue
        if b[0] == 0x48 and b[1] == 0xC7 and b[2] == 0x05:
            disp = struct.unpack_from('<i', b, 3)[0]; imm = struct.unpack_from('<i', b, 7)[0]
            res.append(('mov64i', lea_rva + i + 11 + disp, imm)); i += 11; continue
        i += 1
    return res

if __name__ == '__main__':
    for name in sys.argv[2:]:
        for s in find_wstr(name):
            xs = xrefs(s)
            print(f'{name}: string @rva {s:#x}, {len(xs)} lea xref(s)')
            for x in xs[:4]:
                st = stores_after(x)
                desc = next((a for k, a, _ in st if k == 'mov64'), None)
                print(f'   lea @{x:#x} -> name stored to {desc and hex(desc)}')
                for k, a, imm in st[:14]:
                    rel = (a - desc) if desc else None
                    print(f'      {k} [{a:#x}] (desc{rel:+#x})' + (f' = {imm:#x}' if imm is not None else '') if rel is not None and -0x40 < rel < 0x40 else f'      {k} [{a:#x}]' + (f' = {imm:#x}' if imm is not None else ''))
