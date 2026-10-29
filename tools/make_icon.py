"""Issun's icon: two waves arriving at a speech bubble. It is the second half of
Ammy's icon, where the same two waves leave an album cover: what's playing,
sent, then said. The wave nearer the bubble is the darker one, so the signal
grows as it arrives, the way Ammy's fades as it leaves. Drawn the way Albums'
and Ammy's iPhone icons are: Albums' squircle, black, greys, gradient and
shadow. (In Okami, Issun is the one who does the talking for Ammy.)

Windows masks nothing, so the tile is drawn here, with transparent corners and
its shadow inside the frame. The frames from 32 px down are laid out by hand on
the pixel grid, with fewer and heavier shapes, because a picture shrunk to 16 px
turns to grey mush.

    python tools/make_icon.py src/Issun/Assets/issun.ico

writes the .ico, 16 to 256 px. Needs Pillow and numpy."""
import io
import math
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

TOP, BOTTOM = (255, 255, 255), (238, 238, 242)
BLACK, MID, PALE = (20, 20, 22), (88, 88, 92), (160, 160, 166)

def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))

def bezier(p0, p1, p2, p3, n=48):
    pts = []
    for i in range(n + 1):
        t = i / n
        mt = 1 - t
        pts.append(tuple(mt**3 * p0[k] + 3 * mt**2 * t * p1[k] + 3 * mt * t**2 * p2[k] + t**3 * p3[k] for k in range(2)))
    return pts

def squircle_corner(R, smoothing, budget):
    """Figma's corner smoothing, as figma-squircle implements it (after Figma's
    "Desperately seeking squircles"): one corner, from the top edge to the right
    edge, in coordinates relative to the corner point (x left is negative)."""
    p = (1 + smoothing) * R
    if p > budget:
        smoothing = min(smoothing, budget / R - 1)
        p = min(p, budget)
    arc_measure = 90 * (1 - smoothing)
    arc_len = math.sin(math.radians(arc_measure / 2)) * R * math.sqrt(2)
    alpha = (90 - arc_measure) / 2
    p3p4 = R * math.tan(math.radians(alpha / 2))
    beta = 45 * smoothing
    c = p3p4 * math.cos(math.radians(beta))
    d = c * math.tan(math.radians(beta))
    b = (p - arc_len - c - d) / 3
    a = 2 * b
    pts = bezier((-p, 0), (-p + a, 0), (-p + a + b, 0), (-p + a + b + c, d))
    cx, cy = -R, R
    t0, t1 = -45 - arc_measure / 2, -45 + arc_measure / 2
    for i in range(1, 48):
        t = math.radians(t0 + (t1 - t0) * i / 48)
        pts.append((cx + R * math.cos(t), cy + R * math.sin(t)))
    q0 = (-p + a + b + c + arc_len, d + arc_len)
    pts += bezier(q0, (q0[0] + d, q0[1] + c), (q0[0] + d, q0[1] + b + c), (q0[0] + d, q0[1] + a + b + c))
    return pts

def squircle(x0, y0, x1, y1, R, smoothing=0.6):
    corner = squircle_corner(R, smoothing, min(x1 - x0, y1 - y0) / 2)
    pts = [(x1 + u, y0 + v) for u, v in corner]          # top right
    pts += [(x1 - v, y1 + u) for u, v in corner]         # bottom right
    pts += [(x0 - u, y1 - v) for u, v in corner]         # bottom left
    pts += [(x0 + v, y0 - u) for u, v in corner]         # top left
    return pts

def wave(cx, cy, r, w, phi, n=160):
    """A thick arc about (cx, cy), phi degrees either side of pointing right,
    with round ends, as SF Symbols draws its waves."""
    def at(radius, deg):
        a = math.radians(deg)
        return (cx + radius * math.cos(a), cy + radius * math.sin(a))
    def cap(deg, turn):
        ex, ey = at(r, deg)
        a = math.radians(deg) + turn
        return [(ex + w / 2 * math.cos(a + math.pi * i / 32), ey + w / 2 * math.sin(a + math.pi * i / 32))
                for i in range(1, 32)]
    pts = [at(r + w / 2, -phi + 2 * phi * i / n) for i in range(n + 1)]
    pts += cap(phi, 0)
    pts += [at(r - w / 2, phi - 2 * phi * i / n) for i in range(n + 1)]
    pts += cap(-phi, math.pi)
    return pts

def bubble(x0, y0, x1, y1, R, tw, th, out):
    """A squircle with a tail at its bottom left, toward the waves: the left side
    runs down and bows out to the tip, and a concave curve comes back to meet the
    bottom edge level with it, as Messages' bubbles do. Built with the tail on
    the right, then mirrored."""
    corner = squircle_corner(R, 0.6, min(x1 - x0, y1 - y0) / 2)
    pts = [(x1 + u, y0 + v) for u, v in corner]          # top right
    tip, meet = (x1 + out, y1 + th), (x1 - tw, y1)
    pts += bezier((x1, y1 - 0.5 * th), (x1, y1 + 0.25 * th), (x1 + 0.5 * out, y1 + 0.7 * th), tip, 32)
    pts += bezier(tip, (x1 - 0.25 * tw, y1 + 0.55 * th), (meet[0] + 0.55 * tw, y1), meet)[1:]
    pts += [(x0 - u, y1 - v) for u, v in corner]         # bottom left
    pts += [(x0 + v, y0 - u) for u, v in corner]         # top left
    return [(x0 + x1 - x, y) for x, y in pts]

# The picture, in Albums' 1024 space: Ammy's two waves, exactly (radii 279 and
# 403, 74 thick, 38 degrees either side), then the bubble one gap (50) beyond
# them, about the cover's weight, its middle on the waves' axis. Centred on its
# box, the solid bubble pulls the picture right; it sits 30 px left of that.
R1, R2, WAVE, GAP, SPREAD = 279, 403, 74, 50, 38
BUBBLE_W, BUBBLE_H = 330, 268

def picture():
    left = R1 * math.cos(math.radians(SPREAD)) - WAVE / 2
    right = R2 + WAVE / 2 + GAP + BUBBLE_W
    cx = 512 - (left + right) / 2 - 30
    bx = cx + R2 + WAVE / 2 + GAP
    return dict(waves=[(cx, 512, R1, WAVE, SPREAD, PALE), (cx, 512, R2, WAVE, SPREAD, MID)],
                body=(bx, 512 - BUBBLE_H / 2, bx + BUBBLE_W, 512 + BUBBLE_H / 2, 0.34 * BUBBLE_H),
                tail=(0.30 * BUBBLE_W, 0.30 * BUBBLE_H, 0.05 * BUBBLE_W), shadow=(12, 18, 40))

def on_tile(px, tile=(60, 60, 964, 964), tshadow=(10, 14, 64)):
    """The picture scaled onto a tile with Albums' 214 px margin in proportion,
    for a frame px square."""
    k = px / 1024
    tx0, ty0, tx1, ty1 = (v * k for v in tile)
    f = (tx1 - tx0) / 1024
    ox, oy = tx0, ty0
    P = picture()
    move = lambda x, y: (ox + x * f, oy + y * f)
    waves = [(*move(cx, cy), r * f, w * f, phi, fill) for cx, cy, r, w, phi, fill in P["waves"]]
    bx0, by0 = move(*P["body"][:2])
    bx1, by1 = move(*P["body"][2:4])
    return dict(tile=(tx0, ty0, tx1, ty1, 0.2237 * (tx1 - tx0)), tshadow=tuple(v * k for v in tshadow[:2]) + (tshadow[2],),
                waves=waves, body=(bx0, by0, bx1, by1, P["body"][4] * f),
                tail=tuple(v * f for v in P["tail"]), shadow=(P["shadow"][0] * f, P["shadow"][1] * f, P["shadow"][2]))

# From 32 px down, by hand: (cx, cy, radius, thickness, spread, fill) per wave,
# and the bubble's body (x0, y0, x1, y1, corner) and tail (width, height,
# reach past the body's side).
# At 16 px only the nearer wave fits. Edges sit on whole pixels where they can.
SMALL = {
    16: dict(tile=(0, 0, 16, 16, 3.6), tshadow=None, waves=[(-0.5, 8, 5.2, 2, 40, MID)],
             body=(7, 4, 15, 10, 2.2), tail=(3.2, 3, 0.8), shadow=None),
    20: dict(tile=(0, 0, 20, 20, 4.5), tshadow=None, waves=[(0, 9.5, 3.4, 2, 40, PALE), (0, 9.5, 6.6, 2, 40, MID)],
             body=(9, 5, 19, 12, 2.6), tail=(3.8, 3.4, 0.9), shadow=None),
    24: dict(tile=(0, 0, 24, 24, 5.4), tshadow=None, waves=[(0.5, 11.5, 4.3, 2.4, 40, PALE), (0.5, 11.5, 8, 2.4, 40, MID)],
             body=(11, 6, 23, 15, 3.1), tail=(4.4, 3.8, 1), shadow=None),
    32: dict(tile=(1, 1, 31, 31, 6.7), tshadow=(0.5, 0.6, 60),
             waves=[(1, 15, 5.6, 3, 39, PALE), (1, 15, 10.1, 3, 39, MID)],
             body=(14, 9, 28, 19, 3.7), tail=(5.2, 4.6, 1.2), shadow=None),
}

def frame(px):
    L = SMALL.get(px) or on_tile(px)
    SS = 4 if px >= 512 else 16
    W = px * SS
    sc = lambda pts, dy=0: [(x * SS, (y + dy) * SS) for x, y in pts]
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))

    tx0, ty0, tx1, ty1, tr = L["tile"]
    tile = squircle(tx0, ty0, tx1, ty1, tr)
    if L["tshadow"]:
        drop, blur, a = L["tshadow"]
        sh = Image.new("RGBA", (W, W), (0, 0, 0, 0))
        ImageDraw.Draw(sh).polygon(sc(tile, drop), fill=(0, 0, 0, a))
        img = Image.alpha_composite(img, sh.filter(ImageFilter.GaussianBlur(blur * SS)))
    tmask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(tmask).polygon(sc(tile), fill=255)
    grad = Image.new("RGB", (W, W))
    gd = ImageDraw.Draw(grad)
    for y in range(W):
        gd.line([(0, y), (W, y)], fill=lerp(TOP, BOTTOM, min(max((y / SS - ty0) / (ty1 - ty0), 0), 1)))
    grad = grad.convert("RGBA")
    grad.putalpha(tmask)
    img = Image.alpha_composite(img, grad)

    shapes = [(wave(cx, cy, r, w, phi), fill) for cx, cy, r, w, phi, fill in L["waves"]]
    bx0, by0, bx1, by1, br = L["body"]
    shapes.append((bubble(bx0, by0, bx1, by1, br, *L["tail"]), BLACK))
    if L["shadow"]:
        drop, blur, a = L["shadow"]
        sh = Image.new("RGBA", (W, W), (0, 0, 0, 0))
        sd = ImageDraw.Draw(sh)
        for pts, _ in shapes:
            sd.polygon(sc(pts, drop), fill=(0, 0, 0, a))
        sh = sh.filter(ImageFilter.GaussianBlur(blur * SS))
        # It falls on the tile, so it stops at the tile's edge.
        sh.putalpha(Image.fromarray(np.minimum(np.asarray(sh.getchannel("A")), np.asarray(tmask))))
        img = Image.alpha_composite(img, sh)
    layer = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    for pts, fill in shapes:
        d.polygon(sc(pts), fill=fill + (255,))
    # The waves near the tile's edge must not spill past its rounded corner.
    layer.putalpha(Image.fromarray(np.minimum(np.asarray(layer.getchannel("A")), np.asarray(tmask))))
    img = Image.alpha_composite(img, layer)
    return img.resize((px, px), Image.LANCZOS if px >= 512 else Image.BOX)

def bmp(im):
    """32-bit BMP frame: header, bottom-up BGRA, then the AND mask (set where the
    pixel is fully transparent, for anything that ignores alpha)."""
    w, h = im.size
    a = np.asarray(im, dtype=np.uint8)
    bgra = a[::-1, :, [2, 1, 0, 3]].tobytes()
    stride = ((w + 31) // 32) * 4
    mask = b"".join(np.packbits(row[:, 3] == 0).tobytes().ljust(stride, b"\0") for row in a[::-1])
    return struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(bgra) + len(mask), 0, 0, 0, 0) + bgra + mask

def png(im):
    buf = io.BytesIO()
    im.save(buf, "PNG")
    return buf.getvalue()

out = Path(sys.argv[1])
big = frame(1024)
frames = [(px, frame(px)) for px in (16, 20, 24, 32, 40, 48, 64)]
frames += [(px, big.resize((px, px), Image.LANCZOS)) for px in (128, 256)]
# PNG for 128 and 256 (a 256 px BMP would be 256 KB of mostly tile), BMP below,
# which everything that reads .ico files understands.
blobs = [(px, png(im) if px >= 128 else bmp(im)) for px, im in frames]
offset = 6 + 16 * len(blobs)
head = struct.pack("<HHH", 0, 1, len(blobs))
for px, blob in blobs:
    head += struct.pack("<BBBBHHII", px % 256, px % 256, 0, 0, 1, 32, len(blob), offset)  # 0 means 256
    offset += len(blob)
out.write_bytes(head + b"".join(blob for _, blob in blobs))
print(f"wrote {out} ({len(blobs)} sizes)")
