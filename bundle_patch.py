"""UnityFS patch transport. Game bytes are read from the recipient's own bundle.

Only supports the exact, hash-checked demo; this is not a general Unity editor.
"""
import gzip
import hashlib
import json
import os
from pathlib import Path
import struct
import sys
import lz4.block

def readn(f, n):
    b = f.read(n)
    if len(b) != n:
        raise ValueError('Truncated input')
    return b

def num(f, fmt):
    return struct.unpack(fmt, readn(f, struct.calcsize(fmt)))[0]

def cstr(f):
    b = bytearray()
    while True:
        c = readn(f, 1)
        if c == b'\0': return b.decode('utf-8')
        b.extend(c)
        if len(b) > 4096: raise ValueError('Oversized string')

def decode(b, size, flags):
    mode = flags & 63
    if mode == 0:
        if len(b) != size: raise ValueError('Wrong raw block size')
        return b
    if mode in (2, 3): return lz4.block.decompress(b, uncompressed_size=size)
    raise ValueError('Unsupported compression')

def unpack(path, folder):
    import io
    folder = Path(folder); folder.mkdir(parents=True, exist_ok=True)
    with open(path, 'rb') as f:
        if cstr(f) != 'UnityFS': raise ValueError('Expected UnityFS')
        version = num(f, '>I'); unity = cstr(f); revision = cstr(f)
        size = num(f, '>Q'); compressed = num(f, '>I'); raw = num(f, '>I'); flags = num(f, '>I')
        if size != os.path.getsize(path): raise ValueError('Bundle size mismatch')
        if version >= 7: f.seek((-f.tell()) % 16, 1)
        start = f.tell()
        if flags & 128: f.seek(size - compressed)
        info = io.BytesIO(decode(readn(f, compressed), raw, flags))
        readn(info, 16)
        blocks = [struct.unpack('>IIH', readn(info, 10)) for _ in range(num(info, '>I'))]
        nodes = []
        for _ in range(num(info, '>I')):
            offset, length, nf = struct.unpack('>QQI', readn(info, 20))
            nodes.append(dict(name=cstr(info), offset=offset, length=length, flags=nf))
        if flags & 128: f.seek(start)
        if flags & 512: f.seek((-f.tell()) % 16, 1)
        # A single raw stream avoids holding gigabytes in memory.
        with (folder / 'stream.raw').open('wb') as out:
            for usize, csize, bf in blocks:
                out.write(decode(readn(f, csize), usize, bf))
        with (folder / 'stream.raw').open('rb') as src:
            for i, node in enumerate(nodes):
                src.seek(node['offset'])
                with (folder / (str(i)+'.raw')).open('wb') as out:
                    copy(src, out, node['length'])
        (folder / 'stream.raw').unlink()
    meta = dict(version=version, unity=unity, revision=revision, nodes=nodes, flags=flags, block_flags=blocks[0][2])
    (folder / 'bundle.json').write_text(json.dumps(meta), encoding='utf-8')
    return meta

def copy(src, dest, count):
    while count:
        b = readn(src, min(count, 1024*1024)); dest.write(b); count -= len(b)

def digest(path):
    with open(path, 'rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest().upper()

def apply_delta(source, patch, output, expected):
    with open(source, 'rb') as src, gzip.open(patch, 'rb') as p, open(output, 'wb') as out:
        if readn(p, 8) != b'EVDELTA1': raise ValueError('Bad delta header')
        length = num(p, '<q'); written = 0
        while written < length:
            op = num(p, '<B')
            if op == 0:
                offset = num(p, '<q'); count = num(p, '<i')
                if offset < 0 or count <= 0 or offset+count > os.path.getsize(source): raise ValueError('Invalid copy range')
                src.seek(offset); copy(src, out, count)
            elif op == 1:
                count = num(p, '<i')
                if count <= 0: raise ValueError('Invalid literal length')
                copy(p, out, count)
            else: raise ValueError('Invalid delta opcode')
            written += count
            if written > length: raise ValueError('Delta output overflow')
        if p.read(1): raise ValueError('Trailing delta data')
    if digest(output) != expected: raise ValueError('Reconstructed file hash mismatch')

def pack(folder, meta, output):
    import io
    folder = Path(folder); output = Path(output)
    blocks = []; nodes = []; offset = 0
    blockfile = output.with_suffix('.blocks')
    try:
        with blockfile.open('wb') as body:
            pending = bytearray()
            def emit(b):
                compressed = lz4.block.compress(b, mode='high_compression', compression=9, store_size=False)
                flag = meta.get('block_flags',3)
                if len(compressed) >= len(b): compressed = b; flag &= ~63
                body.write(compressed); blocks.append((len(b), len(compressed), flag))
            for i, node in enumerate(meta['nodes']):
                path = folder / (str(i)+'.raw'); length = path.stat().st_size
                nodes.append((offset, length, node['flags'], node['name'])); offset += length
                with path.open('rb') as f:
                    while True:
                        b = f.read(131072-len(pending))
                        if not b: break
                        pending.extend(b)
                        if len(pending) == 131072: emit(bytes(pending)); pending.clear()
            if pending: emit(bytes(pending))
        info = io.BytesIO(); info.write(bytes(16)); info.write(struct.pack('>I',len(blocks)))
        for block in blocks: info.write(struct.pack('>IIH',*block))
        info.write(struct.pack('>I',len(nodes)))
        for off, length, flags, name in nodes:
            info.write(struct.pack('>QQI',off,length,flags)); info.write(name.encode()+b'\0')
        raw = info.getvalue(); compressed = lz4.block.compress(raw,mode='high_compression',compression=9,store_size=False)
        with output.open('wb') as f:
            f.write(b'UnityFS\0'); f.write(struct.pack('>I',meta['version']))
            f.write(meta['unity'].encode()+b'\0'+meta['revision'].encode()+b'\0')
            flags=meta.get('flags',195)
            sizepos = f.tell(); f.write(struct.pack('>QIII',0,len(compressed),len(raw),flags))
            if meta['version'] >= 7: f.write(bytes((-f.tell())%16))
            if not flags & 128: f.write(compressed)
            if flags & 512: f.write(bytes((-f.tell())%16))
            with blockfile.open('rb') as body: copy(body,f,blockfile.stat().st_size)
            if flags & 128: f.write(compressed)
            end = f.tell(); f.seek(sizepos); f.write(struct.pack('>Q',end))
    finally:
        if blockfile.exists(): blockfile.unlink()

def apply(source, recipe_path, output, work):
    recipe_path = Path(recipe_path); recipe = json.loads(recipe_path.read_text())
    if digest(source) != recipe['source_sha256']: raise ValueError('Source bundle mismatch')
    work = Path(work); meta = unpack(source,work/'input')
    sources = {n['name']:i for i,n in enumerate(meta['nodes'])}
    dest = work/'output'; dest.mkdir(parents=True,exist_ok=True)
    for i, node in enumerate(recipe['bundle']['nodes']):
        origin = work/'input'/(str(sources[node['source']])+'.raw')
        target = dest/(str(i)+'.raw')
        if node.get('patch'):
            apply_delta(origin,recipe_path.parent/node['patch'],target,node['sha256'])
        else:
            if digest(origin) != node['sha256']: raise ValueError('Unchanged file mismatch')
            import shutil
            shutil.copyfile(origin,target)
    print('Recompressing locally reconstructed assets...',flush=True)
    pack(dest,recipe['bundle'],output)
    # Compression bytes may vary by LZ4 version; decoded files were individually verified.
    print('Bundle rebuilt:',digest(output),flush=True)

if __name__ == '__main__':
    if sys.argv[1] == 'unpack': unpack(sys.argv[2],sys.argv[3])
    elif sys.argv[1] == 'apply': apply(*sys.argv[2:])
    else: raise ValueError('Unknown command')
