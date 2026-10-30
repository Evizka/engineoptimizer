#!/usr/bin/env python3
"""Полный дамп IL сборки EngineOptimizer.dll с резолвом токенов и сигнатур."""
import sys, struct
import dnfile
from dncil.cil.body import CilMethodBody
from dncil.cil.body.reader import CilMethodBodyReaderBase
from dncil.clr.token import Token, StringToken

pe = dnfile.dnPE(sys.argv[1] if len(sys.argv) > 1 else 'extracted/EngineOptimizer.dll')

def s(v):
    try:
        return v.value if hasattr(v, 'value') else str(v)
    except Exception:
        return '?'

# ---------- вспомогательные таблицы ----------
typedefs = list(pe.net.mdtables.TypeDef)
methoddefs = list(pe.net.mdtables.MethodDef)
fielddefs = list(pe.net.mdtables.Field)
typerefs = list(pe.net.mdtables.TypeRef)
memberrefs = list(pe.net.mdtables.MemberRef)
methodspecs = list(pe.net.mdtables.MethodSpec)

# карта: methoddef rid -> (владелец TypeDef, индекс)
owner_of_method = {}
for ti, t in enumerate(typedefs):
    for m in t.MethodList:
        if m.row_index:
            owner_of_method[m.row_index] = ti
owner_of_field = {}
for ti, t in enumerate(typedefs):
    for f in t.FieldList:
        if f.row_index:
            owner_of_field[f.row_index] = ti

def typedef_fullname(t):
    ns, nm = s(t.TypeNamespace), s(t.TypeName)
    return (ns + '.' + nm) if ns else nm

def typeref_fullname(r):
    ns, nm = s(r.TypeNamespace), s(r.TypeName)
    return (ns + '.' + nm) if ns else nm

def decode_type_rid(table, rid):
    if table == 0x01 and 0 < rid <= len(typerefs):
        return typeref_fullname(typerefs[rid - 1])
    if table == 0x02 and 0 < rid <= len(typedefs):
        return typedef_fullname(typedefs[rid - 1])
    if table == 0x1B:
        return "typespec#%d" % rid
    return "?t%d.%d" % (table, rid)

def memberref_str(rid):
    r = memberrefs[rid - 1]
    try:
        cls = r.Class
        clsrow = cls.row
        if cls.table and cls.row_index:
            owner = decode_type_rid(cls.table.number if hasattr(cls.table, 'number') else cls.table_index, cls.row_index)
        else:
            owner = '?'
    except Exception:
        owner = '?'
    return f"{owner}::{s(r.Name)}"

def methoddef_str(rid):
    r = methoddefs[rid - 1]
    ti = owner_of_method.get(rid)
    own = typedef_fullname(typedefs[ti]) if ti is not None else '?'
    return f"{own}::{s(r.Name)}"

def fielddef_str(rid):
    r = fielddefs[rid - 1]
    ti = owner_of_field.get(rid)
    own = typedef_fullname(typedefs[ti]) if ti is not None else '?'
    return f"{own}::{s(r.Name)}"

def methodspec_str(rid):
    r = methodspecs[rid - 1]
    try:
        m = r.Method
        tbl = m.table.number if hasattr(m.table, 'number') else 0
        base = methoddef_str(m.row_index) if tbl == 0x06 else memberref_str(m.row_index)
    except Exception:
        base = '?'
    return f"{base}<..>"

def resolve(tok):
    t, rid = tok.table, tok.rid
    if isinstance(tok, StringToken):
        try:
            us = pe.net.user_strings.get_us(rid)
            return '"%s"' % us.value.replace('\n', '\\n').replace('\r', '\\r').replace('\t', '\\t')
        except Exception:
            return "us(0x%x)" % rid
    if t == 0x06: return methoddef_str(rid)
    if t == 0x0A: return memberref_str(rid)
    if t == 0x04: return fielddef_str(rid)
    if t == 0x01: return typeref_fullname(typerefs[rid - 1])
    if t == 0x02: return typedef_fullname(typedefs[rid - 1])
    if t == 0x2B: return methodspec_str(rid)
    if t == 0x11: return "methoddef?"
    return "tok(0x%08x)" % tok.value

class Reader(CilMethodBodyReaderBase):
    def __init__(self, pe, rva):
        self.pe = pe
        self.offset = pe.get_offset_from_rva(rva)
    def read(self, n):
        d = self.pe.get_data(self.pe.get_rva_from_offset(self.offset), n)
        self.offset += n
        return d
    def tell(self):
        return self.offset
    def seek(self, o):
        self.offset = o
        return o

# ---------- сигнатуры (упрощённый декодер ECMA-335) ----------
class SigReader:
    def __init__(self, blob):
        self.b = blob; self.i = 0
    def byte(self):
        v = self.b[self.i]; self.i += 1; return v
    def peek(self):
        return self.b[self.i]
    def compressed(self):
        first = self.byte()
        if first & 0x80 == 0: return first
        if first & 0xC0 == 0x80: return ((first & 0x3F) << 8) | self.byte()
        return ((first & 0x1F) << 24) | (self.byte() << 16) | (self.byte() << 8) | self.byte()

def read_type(sr, depth=0):
    et = sr.byte()
    simple = {0x01:'void',0x02:'bool',0x03:'char',0x04:'sbyte',0x05:'byte',0x06:'short',0x07:'ushort',
              0x08:'int',0x09:'uint',0x0a:'long',0x0b:'ulong',0x0c:'float',0x0d:'double',0x0e:'string',
              0x16:'TypedReference',0x18:'IntPtr',0x19:'UIntPtr',0x1c:'object'}
    if et in simple: return simple[et]
    if et == 0x0f: return read_type(sr, depth+1) + '*'
    if et == 0x10: return 'ref ' + read_type(sr, depth+1)
    if et == 0x45: sr.compressed(); return read_type(sr, depth+1)  # pinned
    if et in (0x11, 0x12):
        tok = sr.compressed()
        table, rid = tok >> 24, tok & 0xFFFFFF
        if table == 0x01 and 0 < rid <= len(typerefs): return typeref_fullname(typerefs[rid-1])
        if table == 0x02 and 0 < rid <= len(typedefs): return typedef_fullname(typedefs[rid-1])
        return 'type(0x%02x#%d)' % (table, rid)
    if et == 0x1d: return read_type(sr, depth+1) + '[]'
    if et == 0x14:
        inner = read_type(sr, depth+1)
        rank = sr.compressed(); sizes = sr.compressed(); lobounds = sr.compressed()
        return f'{inner}[{",".join("0" for _ in range(sizes))}]'
    if et == 0x13: return 'T' + str(sr.compressed())
    if et == 0x1e: return '!!' + str(sr.compressed())
    if et == 0x15:
        kind = read_type(sr, depth+1)
        argc = sr.compressed()
        args = [read_type(sr, depth+1) for _ in range(argc)]
        return f'{kind}<{",".join(args)}>'
    if et in (0x41, 0x42):  # modreq/modopt
        tok = sr.compressed(); return read_type(sr, depth+1)
    if et == 0x1F: return 'enum ' + read_type(sr, depth+1)
    if et == 0x1B: return 'fnptr'
    if et == 0x04 + 0x40: pass
    return f'et(0x{et:02x})'

def method_sig(blob, params):
    try:
        sr = SigReader(bytes(blob))
        cc = sr.byte()
        if cc & 0x20:
            gen = sr.compressed()
        n = sr.compressed()
        ret = read_type(sr)
        names = [None] * n
        for p in params:
            seq = int(p.Sequence) if p.Sequence else 0
            if 1 <= seq <= n: names[seq - 1] = s(p.Name)
        parts = []
        for i in range(n):
            t = read_type(sr)
            nm = names[i] or f'arg{i}'
            parts.append(f'{t} {nm}')
        return ret, parts
    except Exception as e:
        return '?', [f'<sig err {e}>']

# ---------- обход ----------
out = open('/tmp/tmod/il_dump.txt', 'w')

for ti, t in enumerate(typedefs):
    flags = t.Flags
    mods = []
    if flags.tdInterface: mods.append('interface')
    if flags.tdAbstract and not flags.tdInterface: mods.append('abstract')
    if flags.tdSealed: mods.append('sealed')
    if flags.tdPublic: mods.append('public')
    if not flags.tdPublic and not flags.tdInterface: mods.append('internal')
    out.write(f"\n{'='*100}\nTYPE [{ti+1}] {typedef_fullname(t)} ({' '.join(mods)})\n{'='*100}\n")
    # базовый тип
    try:
        ext = t.Extends
        if ext and ext.row_index:
            out.write(f"  extends: {decode_type_rid(ext.table.number, ext.row_index)}\n")
    except Exception:
        pass
    for f in t.FieldList:
        if not f.row or not f.row_index: continue
        row = f.row
        ff = row.Flags
        vis = 'public' if ff.fdPublic else ('internal' if ff.fdAssembly else 'private')
        st = ' static' if ff.fdStatic else ''
        ro = ' readonly' if ff.fdInitOnly else ''
        lit = ' const' if ff.fdLiteral else ''
        ty = '?'
        try:
            srsig = SigReader(bytes(row.Signature))
            srsig.byte()  # 0x06 FIELD
            ty = read_type(srsig)
        except Exception:
            pass
        out.write(f"  .field {vis}{st}{ro}{lit} {ty} {s(row.Name)}\n")
    for m in t.MethodList:
        if not m.row or not m.row_index: continue
        row = m.row
        mf = row.Flags
        vis = 'public' if mf.mdPublic else ('private protected' if mf.mdFamANDAssem else 'internal' if mf.mdAssem else 'protected' if mf.mdFamily else 'private')
        st = ' static' if mf.mdStatic else ''
        virt = ' virtual' if mf.mdVirtual else ''
        absr = ' abstract' if mf.mdAbstract else ''
        ret, plist = method_sig(row.Signature, [p.row for p in row.ParamList if p.row])
        sigtxt = f"{ret} {s(row.Name)}({', '.join(plist)})"
        out.write(f"\n  .method {vis}{st}{virt}{absr} {sigtxt}\n")
        rva = int(row.Rva) if row.Rva else 0
        if rva == 0:
            out.write("    <no body>\n")
            continue
        try:
            body = CilMethodBody(Reader(pe, rva))
        except Exception as e:
            out.write(f"    <body error: {e}>\n")
            continue
        for ins in body.instructions:
            op = ins.opcode.name
            operand = ins.operand
            txt = ''
            if isinstance(operand, Token):
                txt = resolve(operand)
            elif operand is not None:
                txt = str(operand)
            out.write(f"    IL_{ins.offset - body.code_size + 0 if False else ins.offset:04X}: {op:12s} {txt}\n")
        for eh in body.exception_handlers:
            try:
                out.write(f"    TRY IL_{eh.try_start:04X}-IL_{eh.try_end:04X} HANDLER({eh.type}) IL_{eh.handler_start:04X}-IL_{eh.handler_end:04X}\n")
            except Exception:
                out.write(f"    EXH {eh}\n")

out.close()
print("dumped to /tmp/tmod/il_dump.txt")
import os
print("size:", os.path.getsize('/tmp/tmod/il_dump.txt'))
