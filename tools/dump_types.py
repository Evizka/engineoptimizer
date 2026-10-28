#!/usr/bin/env python3
"""Дамп всех типов: базовый класс, поля (типы, константы, атрибуты), события/свойства."""
import struct, json
import dnfile

pe = dnfile.dnPE('/tmp/tmod/extracted/EngineOptimizer.dll')
def s(v):
    try: return v.value if hasattr(v, 'value') else str(v)
    except Exception: return '?'

typedefs = list(pe.net.mdtables.TypeDef)
fielddefs = list(pe.net.mdtables.Field)
typerefs = list(pe.net.mdtables.TypeRef)
memberrefs = list(pe.net.mdtables.MemberRef)

def tfull(t):
    ns, nm = s(t.TypeNamespace), s(t.TypeName)
    return (ns + '.' + nm) if ns else nm
def trfull(r):
    ns, nm = s(r.TypeNamespace), s(r.TypeName)
    return (ns + '.' + nm) if ns else nm

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
        if tag == 2:  # TypeSpec — рекурсивно
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

def field_type(sig):
    try:
        sr = SigReader(bytes(sig.value if hasattr(sig, 'value') else sig))
        sr.byte()  # 0x06
        return read_type(sr)
    except Exception as e:
        return '?'

# константы
consts = {}
for c in pe.net.mdtables.Constant:
    try:
        parent = c.Parent
        key = (parent.table.number, parent.row_index)
        val = bytes(c.Value.value)
        typ = int(c.Type.value) if hasattr(c.Type, 'value') else int(c.Type)
        if typ in (0x08, 0x09): consts[key] = struct.unpack('<i', val[:4])[0]
        elif typ == 0x0a: consts[key] = struct.unpack('<q', val[:8])[0]
        elif typ == 0x0c: consts[key] = round(struct.unpack('<f', val[:4])[0], 3)
        elif typ == 0x0d: consts[key] = round(struct.unpack('<d', val[:8])[0], 6)
        elif typ == 0x02: consts[key] = val[0] != 0
        elif typ == 0x0e: consts[key] = val.decode('utf-16-le')[:80]
        else: consts[key] = val.hex()[:40]
    except Exception:
        pass

# атрибуты: простое извлечение имени + строковых/числовых аргументов
ca_by_owner = {}
for ca in pe.net.mdtables.CustomAttribute:
    try:
        parent = ca.Parent
        key = (parent.table.number, parent.row_index)
        typ = ca.Type
        tname = '?'
        if typ.table.number == 0x0A:
            ctor = memberrefs[typ.row_index - 1]
            tname = trfull(ctor.Class.row) if ctor.Class.row and hasattr(ctor.Class.row, 'TypeName') else '?'
        # вытащим строки и числа из блоба
        blob = bytes(ca.Value.value if ca.Value and ca.Value.value else b'')
        args = []
        if blob[:2] == b'\x01\x00' and len(blob) > 4:
            i = 2
            named = struct.unpack_from('<H', blob, len(blob)-2)[0]
            # пробуем fixed args (string/int/bool/float)
            try:
                j = i
                while j < len(blob) - 2 and len(args) < 4:
                    b = blob[j]
                    if b == 0xff: break
                    ln = blob[j]; j += 1
                    if 1 <= ln <= 128 and j + ln <= len(blob) - 2:
                        txt = blob[j:j+ln]
                        if all(32 <= c < 127 for c in txt):
                            args.append(txt.decode()); j += ln; continue
                        else:
                            j -= 1; break
                    else:
                        j -= 1; break
            except Exception:
                pass
        ca_by_owner.setdefault(key, []).append((tname, args))
    except Exception:
        pass

lines_map = {int(k): v for k, v in json.load(open('/tmp/tmod/method_lines.json')).items()}
owner_of_method = {}
for ti, t in enumerate(typedefs):
    for m in t.MethodList:
        if m.row_index: owner_of_method[m.row_index] = ti

out = open('/tmp/tmod/types.txt', 'w', encoding='utf-8')
for ti, t in enumerate(typedefs):
    f = t.Flags
    mods = []
    if f.tdInterface: mods.append('interface')
    if f.tdAbstract and not f.tdInterface: mods.append('abstract')
    if f.tdSealed: mods.append('sealed')
    if f.tdPublic: mods.append('public')
    ext = ''
    try:
        e = t.Extends
        if e and e.row_index:
            ext = ' : ' + (trfull(typerefs[e.row_index-1]) if e.table.number == 0x01 else tfull(typedefs[e.row_index-1]) if e.table.number == 0x02 else '?')
    except Exception:
        pass
    # файл типа: файл большинства методов
    files = {}
    for m in t.MethodList:
        if m.row_index and m.row_index in lines_map:
            fn = lines_map[m.row_index].get('file')
            if fn: files[fn] = files.get(fn, 0) + 1
    mainfile = max(files, key=files.get) if files else '?'
    cas = ca_by_owner.get((0x02, ti + 1), [])
    castxt = ' '.join(f'[{n}({",".join(json.dumps(a, ensure_ascii=False) for a in args)})]' if args else f'[{n}]' for n, args in cas)
    out.write(f"\n===== TYPE [{ti+1}] {tfull(t)}{ext}  ({' '.join(mods)})  [файл: {mainfile}] {castxt}\n")
    for fld in t.FieldList:
        if not fld.row or not fld.row_index: continue
        row = fld.row
        ff = row.Flags
        vis = 'public' if ff.fdPublic else ('internal' if ff.fdAssembly else 'private')
        st = ' static' if ff.fdStatic else ''
        ro = ' readonly' if ff.fdInitOnly else ''
        lit = ' const' if ff.fdLiteral else ''
        ty = field_type(row.Signature)
        cv = consts.get((0x04, fld.row_index), '')
        fcas = ca_by_owner.get((0x04, fld.row_index), [])
        fcatxt = ' '.join(f'[{n}({",".join(json.dumps(a, ensure_ascii=False) for a in args)})]' if args else f'[{n}]' for n, args in fcas)
        out.write(f"  .field {vis}{st}{ro}{lit} {ty} {s(row.Name)}{' = ' + repr(cv) if cv != '' else ''} {fcatxt}\n")
    # методы одной строкой (имена + строки)
    for m in t.MethodList:
        if not m.row or not m.row_index: continue
        info = lines_map.get(m.row_index, {})
        out.write(f"  .method rid={m.row_index} L{info.get('line')} {s(m.row.Name)}\n")
out.close()
import os
print('types.txt:', os.path.getsize('/tmp/tmod/types.txt'))
