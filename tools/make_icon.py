"""Generates src/Builder/builder.ico (an NES-pad style icon) with only the standard library."""
import struct, zlib, sys

def rrect(x, y, x0, y0, x1, y1, r):
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r and x0 <= x <= x1 and y0 <= y <= y1

def shade(u, v):
    """Colour at normalised coords (0..1), or None for transparent."""
    if not rrect(u, v, 0.02, 0.22, 0.98, 0.78, 0.08):
        return None
    col = (205, 205, 210)
    if rrect(u, v, 0.06, 0.27, 0.94, 0.73, 0.04):
        col = (35, 35, 40)
    # d-pad
    if (0.13 <= u <= 0.37 and 0.445 <= v <= 0.555) or (0.195 <= u <= 0.305 and 0.38 <= v <= 0.62):
        col = (225, 225, 230) if col == (35, 35, 40) else col
    # select/start
    if rrect(u, v, 0.42, 0.52, 0.49, 0.56, 0.02) or rrect(u, v, 0.51, 0.52, 0.58, 0.56, 0.02):
        col = (150, 150, 155)
    # A/B
    for cx in (0.70, 0.85):
        if (u - cx) ** 2 + (v - 0.52) ** 2 <= 0.065 ** 2:
            col = (210, 40, 40)
    # red stripe label
    if 0.42 <= u <= 0.58 and 0.33 <= v <= 0.37:
        col = (210, 40, 40)
    return col

def render(size, ss=4):
    px = []
    for y in range(size):
        row = []
        for x in range(size):
            r = g = b = a = 0
            for sy in range(ss):
                for sx in range(ss):
                    c = shade((x + (sx + .5) / ss) / size, (y + (sy + .5) / ss) / size)
                    if c:
                        r += c[0]; g += c[1]; b += c[2]; a += 1
            if a:
                row.append((r // a, g // a, b // a, 255 * a // (ss * ss)))
            else:
                row.append((0, 0, 0, 0))
        px.append(row)
    return px

def png(px):
    n = len(px)
    raw = b''.join(b'\0' + bytes(c for p in row for c in p) for row in px)
    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', n, n, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')

def dib(px):
    n = len(px)
    stride = ((n + 31) // 32) * 4
    out = struct.pack('<IiiHHIIiiII', 40, n, n * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    for row in reversed(px):
        out += b''.join(bytes((p[2], p[1], p[0], p[3])) for p in row)
    for row in reversed(px):
        m = bytearray(stride)
        for x, p in enumerate(row):
            if p[3] == 0:
                m[x >> 3] |= 0x80 >> (x & 7)
        out += bytes(m)
    return out

sizes = [256, 48, 32, 16]
images = [png(render(s, 2)) if s == 256 else dib(render(s)) for s in sizes]
hdr = struct.pack('<HHH', 0, 1, len(sizes))
off = 6 + 16 * len(sizes)
dirs = b''
for s, im in zip(sizes, images):
    dirs += struct.pack('<BBBBHHII', s % 256, s % 256, 0, 0, 1, 32, len(im), off)
    off += len(im)
open(sys.argv[1], 'wb').write(hdr + dirs + b''.join(images))
