#!/usr/bin/env python3
"""Извлечение (файл, строка) для каждого метода + сводный досье-файл для реконструкции."""
import struct, json, sys
import dnfile

def rc(b, i):
    if b[i] & 0x80 == 0: return b[i], i + 1
    if b[i] & 0xC0 == 0x80: return ((b[i] & 0x3F) << 8) | b[i+1], i + 2
    return ((b[i] & 0x1F) << 24) | (b[i+1] << 16) | (b[i+2] << 8) | b[i+3], i + 4

d = open('/tmp/tmod/extracted/EngineOptimizer.pdb', 'rb').read()
blob = d[9912:9912+27012]
t = d[236:236+8588]
mdi_off = 24 + 7*4 + 13*8
n_mdi = 278

def method_line(rid):
    doc, sp = struct.unpack_from('<HH', t, mdi_off + (rid-1)*4)
    if not sp:
        return doc, None
    L, j = rc(blob, sp)
    end = j + L
    i = j
    token, i = rc(blob, i)      # local sig token
    _, i = rc(blob, i)          # флаг/дельта
    vals = []
    ok = True
    for _ in range(4):
        if i >= end:
            ok = False
            break
        v, i = rc(blob, i)
        vals.append(v)
    if not ok:
        return doc, None
    il, X, line, col = vals
    return doc, line

docs = ["EngineOptimizer.cs","OptimizerConfig.cs","Perf/AsyncFileWriter.cs","Perf/KeybindProfileGuard.cs",
        "Perf/ParallelUpdates.cs","Perf/PerfData.cs","Perf/PerfLog.cs","Perf/ProfilerSystem.cs",
        "Perf/SmoothCursorSystem.cs","Perf/Telemetry.cs","Perf/WorldPreloader.cs",
        "obj/.NETCoreApp.AssemblyAttributes.cs","obj/EngineOptimizer.AssemblyInfo.cs"]

result = {}
problems = []
for rid in range(1, n_mdi + 1):
    doc, line = method_line(rid)
    fname = docs[doc-1] if 0 < doc <= len(docs) else None
    result[rid] = {"doc": doc, "file": fname, "line": line}
    if line is not None and not (1 <= line <= 4000):
        problems.append((rid, line))

# проверка монотонности по файлам
print("Проблемные строки:", problems[:10])
prev = {}
viol = []
for rid in range(1, n_mdi + 1):
    r = result[rid]
    if r["line"] is None or r["file"] is None or r["file"].startswith("obj/"):
        continue
    f = r["file"]
    if f in prev and r["line"] < prev[f] - 30:  # допускаем небольшие отклонения (lambdas, nested)
        viol.append((rid, f, prev[f], r["line"]))
    prev[f] = max(prev.get(f, 0), r["line"])
print("Нарушения монотонности (>30 строк назад):", viol[:15])

json.dump(result, open('/tmp/tmod/method_lines.json', 'w'))

# сводка по файлам
from collections import defaultdict
byfile = defaultdict(list)
for rid, r in result.items():
    if r["file"] and not r["file"].startswith("obj/") and r["line"]:
        byfile[r["file"]].append((r["line"], rid))
for f in sorted(byfile):
    lines = sorted(byfile[f])
    print(f"{f}: {len(lines)} методов, строки {lines[0][0]}..{lines[-1][0]}")
