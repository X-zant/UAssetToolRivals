"""Recover game frozen type layouts (type -> [(field, offset, numarray, fieldtype)]) from the shipping exe.

Static-init code for LAYOUT_FIELD, per field descriptor FD (game struct = stock 5.3 FFieldLayoutDesc):
  FD+0x00 Name  FD+0x08 Type  FD+0x10 Next  FD+0x18 WriteFunc  FD+0x20 Offset(u32)  FD+0x24 NumArray(u32)  FD+0x28 Flags(u16)
  TD.Fields (TD+0x10) <- lea FD     (prepend; fields are visited in reverse declaration order)
  FD.Name <- reg holding a TCHAR* (stored late, possibly after the next block starts)
  FD.Type <- rax from a call returning &StaticGetTypeLayoutDesc<T>() (function -> type desc mapping recovered separately)
FTypeLayoutDesc: +0x08 Name, +0x10 Fields.
"""
import struct, sys, re, json, collections
import capstone
from capstone import x86

sys.argv, exe, out = [sys.argv[0], sys.argv[1]], sys.argv[1], sys.argv[2]
import layoutscan as L  # noqa: E402  (pe helpers)
d = L.d
t = d[L.traw:L.traw + L.trs]
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True

def wstr_at(rva, maxlen=160):
    o = L.rva2off(rva)
    if o is None: return None
    s = []
    for k in range(maxlen):
        c = struct.unpack_from('<H', d, o + 2 * k)[0]
        if c == 0: break
        if not (32 <= c < 127): return None
        s.append(chr(c))
    else: return None
    return ''.join(s) if len(s) >= 1 else None

rd = [s for s in L.secs if s[0] == '.rdata'][0]
rd_lo, rd_hi = rd[1], rd[1] + rd[2]

# 1. windows: around every RIP-relative LEA of a wide string in .rdata
starts = set()
for m in re.finditer(rb'[\x48\x4C]\x8D[\x05\x0D\x15\x1D\x25\x2D\x35\x3D]', t):
    i = m.start()
    disp = struct.unpack_from('<i', t, i + 3)[0]
    target = L.tva + i + 7 + disp
    if rd_lo <= target < rd_hi and wstr_at(target):
        starts.add((L.tva + i) & ~0x3FF)
print('windows', len(starts), file=sys.stderr)

name_store, imm32, imm16, call_store = {}, {}, {}, {}
ptr_store = []

# canonical 64-bit register for any sub-register, so 'xor esi,esi' / 'mov [x], esi' resolve
CANON = {}
for full in ['rax','rbx','rcx','rdx','rsi','rdi','rbp','rsp','r8','r9','r10','r11','r12','r13','r14','r15']:
    names = {'rax':['eax','ax','al'],'rbx':['ebx','bx','bl'],'rcx':['ecx','cx','cl'],'rdx':['edx','dx','dl'],
             'rsi':['esi','si','sil'],'rdi':['edi','di','dil'],'rbp':['ebp','bp','bpl'],'rsp':['esp','sp','spl']}.get(full, [full+'d', full+'w', full+'b'])
    for n in [full] + names:
        CANON[n] = full
def canon(ins, reg):
    return CANON.get(ins.reg_name(reg), ins.reg_name(reg))

windows = sorted(starts)
print('windows', len(windows), file=sys.stderr)
for w in windows:
    lo, hi = w - 0x800, w + 0xC00
    o = L.rva2off(lo)
    code = bytes(d[o:o + (hi - lo)])
    regs = {}
    for ins in md.disasm(code, lo):
        ops = ins.operands
        if ins.id == x86.X86_INS_LEA and ops[1].type == x86.X86_OP_MEM and ops[1].mem.base == x86.X86_REG_RIP:
            regs[canon(ins, ops[0].reg)] = ('addr', ins.address + ins.size + ops[1].mem.disp)
        elif ins.id == x86.X86_INS_CALL:
            for r in ('rax', 'rcx', 'rdx', 'r8', 'r9', 'r10', 'r11'):
                regs.pop(r, None)
            if ops[0].type == x86.X86_OP_IMM:
                regs['rax'] = ('call', ops[0].imm)
        elif ins.id == x86.X86_INS_XOR and len(ops) == 2 and ops[0].type == ops[1].type == x86.X86_OP_REG and ops[0].reg == ops[1].reg:
            regs[canon(ins, ops[0].reg)] = ('imm', 0)
        elif ins.id == x86.X86_INS_MOV and len(ops) == 2 and ops[0].type == x86.X86_OP_REG and ops[1].type == x86.X86_OP_IMM:
            regs[canon(ins, ops[0].reg)] = ('imm', ops[1].imm)
        elif ins.id == x86.X86_INS_MOV and len(ops) == 2 and ops[0].type == x86.X86_OP_MEM and ops[0].mem.base == x86.X86_REG_RIP:
            dst = ins.address + ins.size + ops[0].mem.disp
            if ops[1].type == x86.X86_OP_REG:
                v = regs.get(canon(ins, ops[1].reg))
                if v is None: continue
                if ops[0].size == 8 and v[0] == 'addr':
                    s = wstr_at(v[1]) if rd_lo <= v[1] < rd_hi else None
                    if s: name_store[dst] = s
                    else: ptr_store.append((dst, v[1]))
                elif ops[0].size == 8 and v[0] == 'call':
                    call_store[dst] = v[1]
                elif v[0] == 'imm' and ops[0].size == 4:
                    imm32[dst] = v[1] & 0xFFFFFFFF
                elif v[0] == 'imm' and ops[0].size == 2:
                    imm16[dst] = v[1] & 0xFFFF
            elif ops[1].type == x86.X86_OP_IMM:
                if ops[0].size == 4: imm32[dst] = ops[1].imm & 0xFFFFFFFF
                elif ops[0].size == 2: imm16[dst] = ops[1].imm & 0xFFFF
        elif len(ops) >= 1 and ops[0].type == x86.X86_OP_REG and ins.id not in (x86.X86_INS_CMP, x86.X86_INS_TEST, x86.X86_INS_PUSH):
            regs.pop(canon(ins, ops[0].reg), None)

# 2. field descriptors: an Offset store at FD+0x20 and a name store at FD
fds = {}
for a, off in imm32.items():
    fd = a - 0x20
    if fd in name_store and (a + 4) in imm32:
        fds[fd] = dict(name=name_store[fd], offset=off, num=imm32[a + 4], typefn=call_store.get(fd + 8))
# 3. type descriptors: TD.Fields (TD+0x10) <- FD
types = collections.defaultdict(list)
for dst, val in set(ptr_store):
    if val in fds:
        types[dst - 0x10].append(val)
# type names: TD+0x08 <- string
result = {}
for td, fl in types.items():
    tname = name_store.get(td + 8, f'td_{td:#x}')
    fields = sorted({f: fds[f] for f in fl}.values(), key=lambda x: x['offset'])
    result.setdefault(tname, [])
    result[tname] = [dict(name=f['name'], offset=f['offset'], num=f['num'], typefn=f['typefn']) for f in fields]
# map type-getter functions to type names: a getter returns &TD; find 'lea rax,[TD]; ret' in the getter
fn_types = {}
for fn in {f['typefn'] for fl in result.values() for f in fl if f['typefn']}:
    o = L.rva2off(fn)
    if o is None: continue
    for ins in md.disasm(bytes(d[o:o + 0x80]), fn):
        if ins.id == x86.X86_INS_LEA and ins.operands[1].type == x86.X86_OP_MEM and ins.operands[1].mem.base == x86.X86_REG_RIP and ins.operands[0].reg == x86.X86_REG_RAX:
            td = ins.address + ins.size + ins.operands[1].mem.disp
            fn_types[fn] = name_store.get(td + 8, f'td_{td:#x}')
        if ins.id == x86.X86_INS_RET: break
for fl in result.values():
    for f in fl:
        f['type'] = fn_types.get(f.pop('typefn'), '?')
json.dump(result, open(out, 'w'), indent=1, sort_keys=True)
print(len(fds), 'fields in', len(result), 'types', file=sys.stderr)



