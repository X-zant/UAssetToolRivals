import sys, struct, capstone
from capstone import x86
sys.path.insert(0, sys.argv[1]); sys.argv = ['x', sys.argv[2]] + sys.argv[3:]
import layoutscan as L
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
def wstr(rva):
    o = L.rva2off(rva); s = ''
    for k in range(80):
        c = struct.unpack_from('<H', L.d, o + 2*k)[0]
        if c == 0: return s
        if not 32 <= c < 127: return None
        s += chr(c)
start = int(sys.argv[2], 16); length = int(sys.argv[3], 16)
o = L.rva2off(start)
regs = {}
for ins in md.disasm(bytes(L.d[o:o+length]), start):
    ops = ins.operands
    if ins.id == x86.X86_INS_LEA and ops[1].type == x86.X86_OP_MEM:
        m = ops[1].mem
        if m.base == x86.X86_REG_RIP:
            regs[ins.reg_name(ops[0].reg)] = ('str', wstr(ins.address + ins.size + m.disp))
        else:
            regs[ins.reg_name(ops[0].reg)] = ('off', ins.reg_name(m.base), m.disp)
    elif ins.id == x86.X86_INS_ADD and ops[1].type == x86.X86_OP_IMM:
        regs[ins.reg_name(ops[0].reg)] = ('off', ins.reg_name(ops[0].reg), ops[1].imm)
    elif ins.id == x86.X86_INS_CALL:
        a = {k: regs.get(k) for k in ('rcx','rdx','r8','r9')}
        name = next((v[1] for v in a.values() if v and v[0]=='str' and v[1]), None)
        off = a.get('rcx')
        print(f'{ins.address:#x} call {ins.op_str:14} this+{off[2] if off and off[0]=="off" else "?":<6} name={name}')
        regs = {}
    elif ins.id == x86.X86_INS_RET:
        print('ret'); break
