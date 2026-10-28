#!/usr/bin/env python3
"""Распаковщик .tmod (формат tModLoader 2024+: magic TMOD, фиксированные хэш/подпись)."""
import sys, os, zlib, struct, io

def read_7bit_int(f):
    result = shift = 0
    while True:
        b = f.read(1)[0]
        result |= (b & 0x7F) << shift
        if not (b & 0x80):
            return result
        shift += 7

def read_bs_string(f):
    n = read_7bit_int(f)
    return f.read(n).decode('utf-8')

def main(path, outdir):
    data = open(path, 'rb').read()
    f = io.BytesIO(data)
    magic = f.read(4)
    assert magic == b'TMOD', f"bad magic {magic!r}"
    tml_version = read_bs_string(f)
    fhash = f.read(20)          # SHA1, фиксированная длина
    sig = f.read(256)           # RSA-подпись (у локальной сборки нули)
    section_len = struct.unpack('<i', f.read(4))[0]  # длина секции данных (таблица записей + блобы)
    mod_name = read_bs_string(f)
    mod_version = read_bs_string(f)
    count = struct.unpack('<i', f.read(4))[0]
    print(f"tML version : {tml_version}")
    print(f"Mod         : {mod_name} v{mod_version}")
    print(f"hash        : {fhash.hex()}")
    print(f"signature   : {'<zeros, локальная сборка>' if not any(sig) else sig[:16].hex()+'...'}")
    print(f"data section: {section_len} bytes, files: {count}")
    entries = []
    for _ in range(count):
        name = read_bs_string(f)
        size, csize = struct.unpack('<ii', f.read(8))
        entries.append((name, size, csize))
    print(f"entry table ends at 0x{f.tell():x}")
    for name, size, csize in entries:
        blob = f.read(csize)
        raw = blob if csize == size else zlib.decompress(blob, -15)
        assert len(raw) == size, f"{name}: {len(raw)} != {size}"
        outp = os.path.join(outdir, name.replace('/', os.sep))
        os.makedirs(os.path.dirname(outp) or '.', exist_ok=True)
        open(outp, 'wb').write(raw)
        print(f"  {name:55s} {size:>8} B (packed {csize:>8})")
    print(f"end of stream at 0x{f.tell():x} (file 0x{len(data):x})")
    return 0

if __name__ == '__main__':
    sys.exit(main(sys.argv[1], sys.argv[2]))
