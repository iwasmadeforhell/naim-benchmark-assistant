"""Packs PNGs (16,24,32,48,64,128,256) from a folder into app.ico (PNG-compressed entries).  python make_ico.py <pngdir> <out.ico>"""
import struct, sys, os
pngdir, out = sys.argv[1], sys.argv[2]
sizes = [16, 24, 32, 48, 64, 128, 256]
imgs = [(s, open(os.path.join(pngdir, f'ico{s}.png'), 'rb').read()) for s in sizes]
head = struct.pack('<HHH', 0, 1, len(imgs))
offset = 6 + 16 * len(imgs)
entries, data = b'', b''
for s, png in imgs:
    entries += struct.pack('<BBBBHHII', 0 if s >= 256 else s, 0 if s >= 256 else s, 0, 0, 1, 32, len(png), offset + len(data))
    data += png
open(out, 'wb').write(head + entries + data)
print('wrote', out, len(head + entries + data), 'bytes')
