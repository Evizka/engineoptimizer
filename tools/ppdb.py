#!/usr/bin/env python3
"""Финальный парсер минимального Portable PDB: файлы, строки методов, локальные переменные."""
import struct, sys, json

def rc(b, i):
    if b[i] & 0x80 == 0: return b[i], i + 1
    if b[i] & 0xC0 == 0x80: return ((b[i] & 0x3F) << 8) | b[i+1], i + 2
    return ((b[i] & 0x1F) << 24) | (b[i+1] << 16) | (b[i+2] << 8) | b[i+3], i + 4

def rcs(b, i):
    v, j = rc(b, i)
    bits = (j - i) * 7
    if v & (1 << (bits - 1)):
        v -= (1 << bits)
    return v, j

class PPDB:
    def __init__(self, path):
        d = open(path, 'rb').read()
        assert d[:4] == b'BSJB'
        ver_len = struct.unpack_from('<I', d, 12)[0]
        p = 16 + ((ver_len + 3) & ~3)
        n = struct.unpack_from('<H', d, p + 2)[0]
        p += 4
        streams = {}
        for _ in range(n):
            off, size = struct.unpack_from('<II', d, p)
            p += 8
            nm = b''
            while d[p] != 0:
                nm += d[p:p+1]; p += 1
            p += 1
            p = (p + 3) & ~3
            streams[nm.decode()] = (off, size)
        self.blob = d[streams['#Blob'][0]:streams['#Blob'][0] + streams['#Blob'][1]]
        self.strings = d[streams['#Strings'][0]:streams['#Strings'][0] + streams['#Strings'][1]]
        t_off, t_size = streams['#~']
        t = d[t_off:t_off + t_size]
        heap = t[6]
        S = 4 if heap & 1 else 2
        valid = struct.unpack_from('<Q', t, 8)[0]
        p = 24
        counts = {}
        for bit in range(64):
            if valid & (1 << bit):
                counts[bit] = struct.unpack_from('<I', t, p)[0]
                p += 4
        layout = {48: [2, 2, 2, 2], 49: [2, 2], 50: [2, 2, 2, 2, 4, 4], 51: [2, 2, S], 52: [S, 2], 53: [2, 2], 55: [2, 2, 2]}
        self.rows = {}
        for tbl in sorted(counts):
            sz = sum(layout[tbl])
            self.rows[tbl] = [struct.unpack_from('<' + 'H' * (sz // 2), t, p + i * sz) if sz % 2 == 0 else self._mixed(t, p + i * sz, layout[tbl]) for i in range(counts[tbl])]
            p += counts[tbl] * sz
        # документы
        self.docs = []
        for (name_idx, ha, hsh, lang) in self.rows[48]:
            self.docs.append(self.doc_name(name_idx))
        # локальные скоупы по методам: список (vlist_start) с границами
        self.scopes = self.rows.get(50, [])
        self.localvars = self.rows.get(51, [])
        self.mdi = self.rows.get(49, [])

    @staticmethod
    def _mixed(t, off, lay):
        vals = []
        q = off
        for w in lay:
            vals.append(struct.unpack_from('<H' if w == 2 else '<I', t, q)[0])
            q += w
        return vals

    def read_item(self, idx):
        ln, j = rc(self.blob, idx)
        return self.blob[j:j+ln]

    def str_at(self, idx):
        e = self.strings.index(b'\x00', idx)
        return self.strings[idx:e].decode('utf-8', errors='replace')

    def doc_name(self, idx):
        item = self.read_item(idx)
        sep = '\\' if item and item[0] == 0x5c else '/'
        i = 1 if item and item[0] == 0x5c else 0
        parts = []
        while i < len(item):
            off, i = rc(item, i)
            parts.append(self.read_item(off).decode('utf-8', errors='replace'))
        return sep.join(parts)

    def method_info(self, rid):
        """-> (файл_коротко, start_line, end_line, [(il,line,eline)], имена локальных переменных)"""
        if rid - 1 >= len(self.mdi):
            return None, 0, 0, [], []
        doc_idx, sp_idx = self.mdi[rid - 1]
        fname = self.docs[doc_idx - 1] if 0 < doc_idx <= len(self.docs) else None
        pts = []
        if sp_idx:
            b = self.blob
            i = sp_idx
            L, j = rc(b, i)      # длина блоба
            blob_end = j + L
            i = j
            _, i = rc(b, i)      # local signature token
            # document delta: присутствует если документ сменился относительно предыдущей строки
            # определяем по таблице
            prev_doc = self.mdi[rid - 2][0] if rid >= 2 else 0
            if doc_idx != prev_doc:
                _, i = rc(b, i)
            il = line = col = eline = ecol = 0
            first = True
            while i < blob_end:
                try:
                    d_il, j2 = rc(b, i)
                    d_line, j3 = rcs(b, j2)
                    d_col, j4 = rcs(b, j3)
                    d_eline, j5 = rcs(b, j4)
                    d_ecol, j6 = rcs(b, j5)
                except Exception:
                    break
                if j6 > blob_end:
                    break
                i = j6
                if first:
                    il, line, col, eline, ecol = d_il, d_line, d_col, d_eline, d_ecol
                    first = False
                else:
                    il += d_il; line += d_line; col += d_col; eline += d_eline; ecol += d_ecol
                if line == 0xFEEFEE:
                    continue
                pts.append((il, line, col, eline, ecol))
                if len(pts) > 5000:
                    break
        # локальные переменные: скоупы этого метода
        lvnames = []
        method_scopes = [(k, r) for k, r in enumerate(self.scopes) if r[0] == rid]
        for k, r in method_scopes:
            vstart = r[2]
            vend = self.scopes[k+1][2] if k + 1 < len(self.scopes) else len(self.localvars)
            for vi in range(vstart - 1, min(vend, len(self.localvars))):
                attrs, idx, name_idx = self.localvars[vi]
                nm = self.str_at(name_idx)
                if nm in lvnames or (nm.startswith('<') and '>' in nm):
                    continue
                lvnames.append(nm)
        start_line = pts[0][1] if pts else 0
        end_line = max((p[3] for p in pts), default=0)
        return fname, start_line, end_line, pts, lvnames

if __name__ == '__main__':
    p = PPDB(sys.argv[1])
    print("docs:")
    for i, dn in enumerate(p.docs):
        print(f"  {i+1:2d} {dn}")
    for rid in (int(x) for x in sys.argv[2:]):
        f, s, e, pts, lv = p.method_info(rid)
        short = f.split('\\')[-1] if f else '?'
        print(f"method {rid}: {short}:{s}-{e} pts={len(pts)} vars={lv[:10]}")
