#!/usr/bin/env python3
"""Сводное досье для реконструкции: по файлам, методы по строкам, IL + сигнатуры + атрибуты."""
import struct, json, sys
import dnfile
from dncil.cil.body import CilMethodBody
from dncil.cil.body.reader import CilMethodBodyReaderBase
from dncil.clr.token import Token

DLL = '/tmp/tmod/extracted/EngineOptimizer.dll'
pe = dnfile.dnPE(DLL)

def s(v):
    try:
        return v.value if hasattr(v, 'value') else str(v)
    except Exception:
        return '?'

typedefs = list(pe.net.mdtables.TypeDef)
methoddefs = list(pe.net.mdtables.MethodDef)
fielddefs = list(pe.net.mdtables.Field)
typerefs = list(pe.net.mdtables.TypeRef)
memberrefs = list(pe.net.mdtables.MemberRef)
methodspecs = list(pe.net.mdtables.MethodSpec)

owner_of_method, owner_of_field = {}, {}
for ti, t in enumerate(typedefs):
    for m in t.MethodList:
        if m.row_index: owner_of_method[m.row_index] = ti
    for f in t.FieldList:
        if f.row_index: owner_of_field[f.row_index] = ti

def tfull(t):
    ns, nm = s(t.TypeNamespace), s(t.TypeName)
    return (ns + '.' + nm) if ns else nm

def trfull(r):
    ns, nm = s(r.TypeNamespace), s(r.TypeName)
    return (ns + '.' + nm) if ns else nm

def mdef(rid):
    r = methoddefs[rid - 1]
    ti = owner_of_method.get(rid)
    return f"{tfull(typedefs[ti]) if ti is not None else '?'}::{s(r.Name)}"

def mref(rid):
    r = memberrefs[rid - 1]
    try:
        c = r.Class
        owner = trfull(c.row) if c.row and hasattr(c.row, 'TypeName') else '?'
    except Exception:
        owner = '?'
    return f"{owner}::{s(r.Name)}"

def fdef(rid):
    r = fielddefs[rid - 1]
    ti = owner_of_field.get(rid)
    return f"{tfull(typedefs[ti]) if ti is not None else '?'}::{s(r.Name)}"

def mspec(rid):
    r = methodspecs[rid - 1]
    try:
        m = r.Method
        base = mdef(m.row_index) if m.table.number == 0x06 else mref(m.row_index)
    except Exception:
        base = '?'
    return base + '<T>'

def resolve(tok):
    t, rid = tok.table, tok.rid
    if t == 0x70:
        try:
            us = pe.net.user_strings.get(rid)
            return '"%s"' % us.value.replace('\n', '\\n').replace('\r', '').replace('\t', '\\t')
        except Exception:
            return f'us(0x{rid:x})'
    if t == 0x06: return mdef(rid)
    if t == 0x0A: return mref(rid)
    if t == 0x04: return fdef(rid)
    if t == 0x01: return trfull(typerefs[rid - 1])
    if t == 0x02: return tfull(typedefs[rid - 1])
    if t == 0x2B: return mspec(rid)
    if t == 0x11: return f'local_sig(0x{rid:x})'
    return f'tok(0x{tok.value:08x})'

class Reader(CilMethodBodyReaderBase):
    def __init__(self, pe, rva):
        self.pe = pe
        self.offset = pe.get_offset_from_rva(rva)
    def read(self, n):
        d = self.pe.get_data(self.pe.get_rva_from_offset(self.offset), n)
        self.offset += n
        return d
    def tell(self): return self.offset
    def seek(self, o):
        self.offset = o
        return o

# ---- сигнатуры ----
class SigReader:
    def __init__(self, blob): self.b = blob; self.i = 0
    def byte(self):
        v = self.b[self.i]; self.i += 1; return v
    def compressed(self):
        f = self.byte()
        if f & 0x80 == 0: return f
        if f & 0xC0 == 0x80: return ((f & 0x3F) << 8) | self.byte()
        return ((f & 0x1F) << 24) | (self.byte() << 16) | (self.byte() << 8) | self.byte()

def read_type(sr, depth=0):
    et = sr.byte()
    simple = {0x01:'void',0x02:'bool',0x03:'char',0x04:'sbyte',0x05:'byte',0x06:'short',0x07:'ushort',
              0x08:'int',0x09:'uint',0x0a:'long',0x0b:'ulong',0x0c:'float',0x0d:'double',0x0e:'string',
              0x16:'TypedReference',0x18:'IntPtr',0x19:'UIntPtr',0x1c:'object'}
    if et in simple: return simple[et]
    if et == 0x0f: return read_type(sr, depth+1) + '*'
    if et == 0x10: return 'ref ' + read_type(sr, depth+1)
    if et == 0x45: sr.compressed(); return read_type(sr, depth+1)
    if et in (0x11, 0x12):
        tok = sr.compressed(); tag, rid = tok & 3, tok >> 2
        if tag == 0 and 0 < rid <= len(typedefs): return tfull(typedefs[rid-1])
        if tag == 1 and 0 < rid <= len(typerefs): return trfull(typerefs[rid-1])
        if tag == 2:
            try:
                row = pe.net.mdtables.TypeSpec[rid - 1]
                sub = SigReader(bytes(row.Signature.value if hasattr(row.Signature, 'value') else row.Signature))
                return read_type(sub, depth+1)
            except Exception:
                return 'typespec#%d' % rid
        return 'type#%d' % rid
    if et == 0x1d: return read_type(sr, depth+1) + '[]'
    if et == 0x14:
        inner = read_type(sr, depth+1)
        sr.compressed(); sr.compressed(); sr.compressed()
        return inner + '[,]'
    if et == 0x13: return 'T' + str(sr.compressed())
    if et == 0x1e: return '!!' + str(sr.compressed())
    if et == 0x15:
        kind = read_type(sr, depth+1)
        argc = sr.compressed()
        args = [read_type(sr, depth+1) for _ in range(argc)]
        return f'{kind}<{",".join(args)}>'
    if et in (0x41, 0x42):
        sr.compressed(); return read_type(sr, depth+1)
    return f'et(0x{et:02x})'

def method_sig(blob, params):
    try:
        sr = SigReader(bytes(blob.value if hasattr(blob, 'value') else blob))
        cc = sr.byte()
        if cc & 0x20: sr.compressed()
        n = sr.compressed()
        ret = read_type(sr)
        names = [None] * n
        for p in params:
            seq = int(p.Sequence) if p.Sequence else 0
            if 1 <= seq <= n: names[seq - 1] = s(p.Name)
        parts = []
        for i in range(n):
            t = read_type(sr)
            parts.append(f'{t} {names[i] or ("arg" + str(i))}')
        return ret, parts
    except Exception as e:
        return '?', [f'<{e}>']

# ---- константы ----
consts = {}
for c in pe.net.mdtables.Constant:
    try:
        parent = c.Parent
        key = (parent.table.number, parent.row_index)
        val = bytes(c.Value.value)
        typ = int(c.Type.value) if hasattr(c.Type, 'value') else int(c.Type)
        if typ in (0x08, 0x09):  # i4/u4
            consts[key] = struct.unpack('<i', val[:4])[0]
        elif typ == 0x0a: consts[key] = struct.unpack('<q', val[:8])[0]
        elif typ == 0x0c: consts[key] = struct.unpack('<f', val[:4])[0]
        elif typ == 0x0d: consts[key] = struct.unpack('<d', val[:8])[0]
        elif typ == 0x02: consts[key] = val[0] != 0
        elif typ == 0x0e: consts[key] = val.decode('utf-16-le')[:64]
        else: consts[key] = val.hex()[:32]
    except Exception:
        pass

# ---- атрибуты ----
def decode_ca_blob(blob, ctor_ref):
    vals = []
    try:
        b = bytes(blob.value if hasattr(blob, 'value') else blob)
        if b[:2] != b'\x01\x00': return vals
        i = 2
        # пропускаем fixed args по сигнатуре ctor (грубо: до named-количества)
        # попробуем прочитать все: строка (comp len + utf8), число...
        # эвристика: named count = последние 2 байта
        named = struct.unpack_from('<H', b, len(b)-2)[0] if len(b) >= 4 else 0
        # fixed args: читаем по типам ctor-сигнатуры
        if ctor_ref is not None:
            try:
                sr = SigReader(bytes(ctor_ref.Signature.value))
                sr.byte()
                if b'generic' in b'': pass
                n = sr.compressed()
                read_type(sr)  # ret
                for _ in range(n):
                    t = read_type(sr)
                    if t in ('bool',):
                        vals.append(('bool', b[i] != 0)); i += 1
                    elif t in ('int', 'uint'):
                        vals.append(('int', struct.unpack_from('<i', b, i)[0])); i += 4
                    elif t in ('byte',):
                        vals.append(('byte', b[i])); i += 1
                    elif t in ('float',):
                        vals.append(('float', struct.unpack_from('<f', b, i)[0])); i += 4
                    elif t in ('double',):
                        vals.append(('double', struct.unpack_from('<d', b, i)[0])); i += 8
                    elif t == 'string':
                        ln = b[i]; i += 1
                        vals.append(('string', b[i:i+ln].decode('utf-8', 'replace'))); i += ln
                    else:
                        vals.append(('?', t)); break
            except Exception as e:
                vals.append(('sig-err', str(e)))
        # named args
        j = i
        for _ in range(min(named, 8)):
            if j >= len(b) - 2: break
            kind = b[j]; j += 1  # 0x53 field / 0x54 property
            et = b[j]; j += 1
            nlen = b[j]; j += 1
            name = b[j:j+nlen].decode('utf-8', 'replace'); j += nlen
            if et == 0x0e:
                ln = b[j]; j += 1
                v = b[j:j+ln].decode('utf-8', 'replace'); j += ln
            elif et == 0x08:
                v = struct.unpack_from('<i', b, j)[0]; j += 4
            elif et == 0x02:
                v = b[j] != 0; j += 1
            elif et == 0x0c:
                v = struct.unpack_from('<f', b, j)[0]; j += 4
            else:
                v = '?'; break
            vals.append((name, v))
    except Exception:
        pass
    return vals

ca_by_owner = {}
for ca in pe.net.mdtables.CustomAttribute:
    try:
        parent = ca.Parent
        key = (parent.table.number, parent.row_index)
        typ = ca.Type
        ctor = None
        tname = '?'
        if typ.table.number == 0x0A:
            ctor = memberrefs[typ.row_index - 1]
            tname = trfull(ctor.Class.row) if ctor.Class.row and hasattr(ctor.Class.row, 'TypeName') else '?'
        ca_by_owner.setdefault(key, []).append((tname, decode_ca_blob(ca.Value, ctor)))
    except Exception:
        pass

# ---- строки PDB ----
lines_map = {int(k): v for k, v in json.load(open('/tmp/tmod/method_lines.json')).items()}

# ---- локальные переменные ----
sys.path.insert(0, '/tmp/tmod')
from ppdb import PPDB
ppdb = PPDB('/tmp/tmod/extracted/EngineOptimizer.pdb')

# ---- генерация ----
out = open('/tmp/tmod/dossier.txt', 'w', encoding='utf-8')

def w(x=''):
    out.write(x + '\n')

FILES = {}
for rid in range(1, len(methoddefs) + 1):
    info = lines_map.get(rid)
    if not info: continue
    f = info.get('file')
    if not f or f.startswith('obj/'): continue
    FILES.setdefault(f, []).append(rid)

w("# ДОСЬЕ РЕКОНСТРУКЦИИ EngineOptimizer v0.8.2")
w(f"# Методов с строками: {sum(len(v) for v in FILES.values())}")

# типы по файлам: тип относится к файлу большинства его методов
for f in sorted(FILES):
    w('\n' + '=' * 110)
    w(f"### FILE: {f}")
    w('=' * 110)
    for rid in sorted(FILES[f], key=lambda r: (lines_map[r]['line'] or 9999)):
        m = methoddefs[rid - 1]
        ti = owner_of_method.get(rid)
        tname = tfull(typedefs[ti]) if ti is not None else '?'
        info = lines_map[rid]
        ret, plist = method_sig(m.Signature, [p.row for p in m.ParamList if p.row])
        mf = m.Flags
        mods = []
        if mf.mdPublic: mods.append('public')
        elif mf.mdAssem: mods.append('internal')
        elif mf.mdFamily: mods.append('protected')
        elif mf.mdFamORAssem: mods.append('protected internal')
        else: mods.append('private')
        if mf.mdStatic: mods.append('static')
        if mf.mdVirtual and not mf.mdAbstract: mods.append('virtual')
        if mf.mdAbstract: mods.append('abstract')
        cas = ca_by_owner.get((0x06, rid), [])
        castxt = ''
        if cas:
            parts = []
            for (tn, vals) in cas:
                args = ', '.join(f'{k}={v!r}' for k, v in vals if k != 'sig-err')
                parts.append(f'[{tn}({args})]' if args else f'[{tn}]')
            castxt = ' '.join(parts)
        w(f"\n--- L{info['line']} {tname}::{' '.join(mods)} {ret} {s(m.Name)}({', '.join(plist)}) {castxt}")
        # локальные переменные
        _, _, _, _, lv = ppdb.method_info(rid)
        if lv:
            w(f"    locals: {', '.join(lv[:20])}")
        rva = int(m.Rva) if m.Rva else 0
        if rva == 0:
            w('    <no body>')
            continue
        try:
            body = CilMethodBody(Reader(pe, rva))
        except Exception as e:
            w(f'    <body err {e}>')
            continue
        for ins in body.instructions:
            operand = ins.operand
            txt = ''
            if isinstance(operand, Token):
                txt = resolve(operand)
            elif operand is not None:
                txt = str(operand)
            w(f'    {ins.opcode.name:14s} {txt}')

out.close()
import os
print('dossier:', os.path.getsize('/tmp/tmod/dossier.txt'), 'bytes')
for f in sorted(FILES):
    print(f"  {f}: {len(FILES[f])} методов")
