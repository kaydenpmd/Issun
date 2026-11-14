"""Issun's icon: two waves arriving at a speech bubble. It is the second half of
Ammy's icon, where the same two waves leave an album cover: what's playing,
sent, then said. (In Okami, Issun is the one who does the talking for Ammy.)

Since 14 Nov 2026 it is drawn the way Discord draws its own icons on Windows,
because Issun is the Discord end of things (the owner: "issun is more of a
discord-adjacent tool than it is an apple music-adjacent one"). Measured from
Discord's desktop app, app-1.0.9268, on the owner's PC:
- the app icon (`app.ico`: .exe, taskbar, Start, title bar) is a flat blurple
  disc filling the frame edge to edge, the logomark white on it, no gradient
  and no shadow, the logomark's farthest point 0.68 of the radius from the
  centre (0.64 to 0.68 across its 16 to 256 px frames);
- the tray icon (`systemtray/win32/tray*.png`) is the logomark alone in white,
  no disc, spanning the frame less 1 px each side, with a soft black halo
  around it (one pixel at 16 px, two at 20 to 32, three at 40), so it shows on
  a light taskbar too.
The design library has the captures and numbers
(`design-research/discord/windows-icons/`).

Until 14 Nov 2026 the icon was black and greys on a white squircle with a
gradient and a drop shadow, in the style of the owner's iPhone apps. Discord's
icons are one white on one blurple, so both waves are white now: the darker
nearer wave ("the signal grows as it arrives") had no colour left to be.

    python tools/make_icon.py src/Issun/Assets/issun.ico          the app icon
    python tools/make_icon.py src/Issun/Assets/tray.ico --tray    the tray's
    python tools/make_icon.py issun-1024.png --square             for Discord

The last is a 1024 px PNG for the Discord application's icon in the Developer
Portal: the disc's blurple to the corners (the owner, 14 Nov 2026: "no
transparent corners"), since Discord rounds it itself. Needs Pillow and
numpy."""
import io
import math
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

# Discord's blurple as its Windows app icon draws it, measured from app.ico's
# 256 px frame (every opaque pixel of the disc). Discord's brand page gives
# #5865F2; the icon's own pixels are a shade off that, and this sits beside it.
BLURPLE = (86, 98, 246)
WHITE = (255, 255, 255)

# The logomark's farthest point from the disc's centre, as a fraction of the
# radius: Clyde's in Discord's 256 px frame.
REACH = 0.68

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

# The picture, in a 1024 space: Ammy's two waves, exactly (radii 279 and 403,
# 74 thick, 38 degrees either side), then the bubble one gap (50) beyond them,
# about the cover's weight, its middle on the waves' axis.
R1, R2, WAVE, GAP, SPREAD = 279, 403, 74, 50, 38
BUBBLE_W, BUBBLE_H = 330, 268

def picture(waves=2):
    """The shapes as point lists, unscaled, with the waves' centre at x=0."""
    bx = R2 + WAVE / 2 + GAP
    body = (bx, -BUBBLE_H / 2, bx + BUBBLE_W, BUBBLE_H / 2, 0.34 * BUBBLE_H)
    tail = (0.30 * BUBBLE_W, 0.30 * BUBBLE_H, 0.05 * BUBBLE_W)
    radii = (R1, R2) if waves == 2 else (R2,)
    return [wave(0, 0, r, WAVE, SPREAD) for r in radii] + [bubble(*body, *tail)]

def placed(shapes, px, fit):
    """The shapes scaled and moved into a px frame. "disc": the farthest point
    REACH of the radius from the centre, as Clyde is. "tray": scaled up from
    that as Clyde is in Discord's tray. Centred on the bounding box, then nudged left by 30/637 of the
    picture's width: the solid bubble outweighs the open waves, and centred
    on its box the picture looks pulled right (the old icon's own correction).
    In the tray the margin is Discord's (TRAY_MARGIN)."""
    pts = np.concatenate([np.asarray(s) for s in shapes])
    (x0, y0), (x1, y1) = pts.min(0), pts.max(0)
    w, h = x1 - x0, y1 - y0
    nudge = 30 / 637 * w
    mid = np.array([(x0 + x1) / 2 + nudge, (y0 + y1) / 2])
    s = REACH * px / 2 / np.sqrt(((pts - mid) ** 2).sum(1)).max()
    if fit == "tray":
        # As much bigger than in the disc as Clyde is: 0.625 of the frame wide
        # in the disc (160 of 256), the frame less the margin in the tray. So
        # the picture keeps its size against Clyde's in both places; filling
        # the width as Clyde does would make it taller than Clyde, being
        # nearly square where Clyde is wide.
        s *= (px - 2 * TRAY_MARGIN[px]) / (0.625 * px)
        s = min(s, (px - 2 * TRAY_MARGIN[px]) / max(w, h))
    c = px / 2
    return [[(c + (x - mid[0]) * s, c + (y - mid[1]) * s) for x, y in sh] for sh in shapes]

# Discord's tray halo: black, its opacity in each ring of pixels out from the
# glyph (rings by 8-neighbour steps from the pixels over half white), measured
# from its tray*.png at the nearest size it ships (28 from its 29, 36 between
# its 32 and 40). It doesn't grow with the frame: one soft pixel at 16, two at
# 20 to 32, three at 40. Under the glyph's own edge pixels it is about 0.95.
HALO = {16: (0.38,), 20: (0.61, 0.11), 24: (0.66, 0.13), 28: (0.63, 0.13),
        32: (0.63, 0.11), 36: (0.69, 0.20, 0.03), 40: (0.74, 0.30, 0.06)}
UNDER = 0.95
# The space each side of the tray glyph's widest edge, left for the halo:
# Discord's is 1 px at 16 to 24 and 2 at 29 to 40.
TRAY_MARGIN = {16: 1, 20: 1, 24: 1, 28: 2, 32: 2, 36: 2, 40: 2}

def tray_frame(cover):
    """The white glyph over its halo, from the glyph's coverage at its final size."""
    px = cover.shape[0]
    inside = cover > 0.5
    h = np.where(inside, UNDER, 0.0)
    g, prev = Image.fromarray((inside * 255).astype(np.uint8)), inside
    for a in HALO[px]:
        g = g.filter(ImageFilter.MaxFilter(3))
        cur = np.asarray(g) > 0
        h[cur & ~prev] = a
        prev = cur
    alpha = cover + h * (1 - cover)
    level = np.divide(cover, alpha, out=np.zeros_like(alpha), where=alpha > 0)
    rgba = np.dstack([level * 255] * 3 + [alpha * 255]).round().astype(np.uint8)
    return Image.fromarray(rgba)

def frame(px, kind="disc"):
    """kind: "disc" (the app icon), "square" (the same picture on a full
    blurple square) or "tray" (the white picture alone, with its halo)."""
    SS = 4 if px >= 512 else 16
    W = px * SS
    # Only the nearer wave below 20 px: two waves and a bubble in 10 px of disc
    # is grey mush, and so is the tray's at 16 (which used to drop it too).
    shapes = placed(picture(waves=2 if px >= 20 else 1), px, "tray" if kind == "tray" else "disc")
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if kind == "disc":
        d.ellipse((0, 0, W - 1, W - 1), fill=BLURPLE + (255,))
    elif kind == "square":
        d.rectangle((0, 0, W, W), fill=BLURPLE + (255,))
    mask = Image.new("L", (W, W), 0)
    md = ImageDraw.Draw(mask)
    for pts in shapes:
        md.polygon([(x * SS, y * SS) for x, y in pts], fill=255)
    if kind == "tray":
        return tray_frame(np.asarray(mask.resize((px, px), Image.BOX), dtype=np.float64) / 255)
    glyph = Image.new("RGBA", (W, W), WHITE + (0,))
    glyph.putalpha(mask)
    img = Image.alpha_composite(img, glyph)
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

def ico(frames):
    # PNG for 128 and 256 (a 256 px BMP would be 256 KB), BMP below, which
    # everything that reads .ico files understands.
    blobs = [(px, png(im) if px >= 128 else bmp(im)) for px, im in frames]
    offset = 6 + 16 * len(blobs)
    head = struct.pack("<HHH", 0, 1, len(blobs))
    for px, blob in blobs:
        head += struct.pack("<BBBBHHII", px % 256, px % 256, 0, 0, 1, 32, len(blob), offset)  # 0 means 256
        offset += len(blob)
    return head + b"".join(blob for _, blob in blobs)

out = Path(sys.argv[1])
if "--square" in sys.argv:
    frame(1024, "square").convert("RGB").save(out)
elif "--tray" in sys.argv:
    # The small-icon size at every scaling step from 100% to 250%: the tray
    # asks for SM_CXSMICON at its DPI and loads the nearest frame (TrayIcon.cs).
    out.write_bytes(ico([(px, frame(px, "tray")) for px in (16, 20, 24, 28, 32, 36, 40)]))
else:
    # Every scaling step's size, not only 100%'s: at 150% the taskbar wants 36,
    # and with no 36 frame it drew the 32 one as it was, 4 px smaller each way
    # than Apple Music's beside it (12 Nov 2026). 30, 36 and 42 are 20 at 150%,
    # 24 at 150% and 24 at 175%; 60, 72, 80 and 96 are the larger steps of 48.
    big = frame(1024)
    frames = [(px, frame(px)) for px in (16, 20, 24, 30, 32, 36, 40, 42, 48, 60, 64, 72, 80, 96)]
    frames += [(px, big.resize((px, px), Image.LANCZOS)) for px in (128, 256)]
    out.write_bytes(ico(frames))
print(f"wrote {out}")
