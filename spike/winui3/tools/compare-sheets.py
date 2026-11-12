"""Builds the WPF-against-WinUI comparison sheets for one theme:
ring-and-mark-compare-<mode>.png (the Connect your source status row, waiting
and connected, 5x) and ring-frames-compare-<mode>.png (the two rings at eight
points of their 2 s loop, 8x).
    python -I compare-sheets.py <mode> <pages dir> <winui ring frames dir> <wpf ring frames dir> <out dir>
<pages dir> holds wpf-wait-, wpf-connected-, winui-wait- and
winui-connected-<mode>.png (both apps' --screenshot of the page at 1.25x).
"""
import os
import re
import sys

from PIL import Image, ImageDraw, ImageFont

mode, pages, winui_frames, wpf_frames, out = sys.argv[1:6]
dark = mode == "dark"
sheet_bg = (32, 32, 32) if dark else (243, 243, 243)
ink = (230, 230, 230) if dark else (26, 26, 26)
font = ImageFont.truetype("segoeui.ttf", 26)

# The status row's place in each page, from the .text.json beside it: the
# row's text sits at y 701 (WPF) and 708 (WinUI) at 1.25x.
ROW_Y = {"wpf": 701, "winui": 708}


def page(app, state):
    return Image.open(os.path.join(pages, f"{app}-{state}-{mode}.png")).convert("RGB")


def row_crop(app, state):
    y = ROW_Y[app]
    return page(app, state).crop((40, y - 18, 200, y + 38))


card = page("winui", "wait").getpixel((45, 700))


def flat(im):
    im = im.convert("RGBA")
    bg = Image.new("RGBA", im.size, card + (255,))
    bg.alpha_composite(im)
    return bg.convert("RGB")


# Sheet 1: the status row.
z = 5
cw, ch, lab = 160 * z, 56 * z, 170
sheet = Image.new("RGB", (lab + 2 * cw + 30, 2 * ch + 30), sheet_bg)
d = ImageDraw.Draw(sheet)
for r, app in enumerate(["wpf", "winui"]):
    d.text((14, 10 + r * (ch + 10) + ch // 2 - 18), ["WPF", "WinUI 3"][r], fill=ink, font=font)
    for c, state in enumerate(["wait", "connected"]):
        sheet.paste(row_crop(app, state).resize((cw, ch), Image.NEAREST), (lab + c * (cw + 10), 10 + r * (ch + 10)))
sheet.save(os.path.join(out, f"ring-and-mark-compare-{mode}.png"))

# Sheet 2: ring frames. WinUI's frames are whole pages named by ms since the
# ring was switched on; the ring sits at (48..76, 704..732). WPF's are 40 px
# squares with the ring in the middle, one per eighth of the loop.
frames = sorted((int(re.findall(r"\d+", f)[0]), f) for f in os.listdir(winui_frames))
cell, z = 28, 8
cs, lab = cell * z, 130
sheet = Image.new("RGB", (lab + 8 * (cs + 8), 2 * (cs + 8) + 70), sheet_bg)
d = ImageDraw.Draw(sheet)
small = ImageFont.truetype("segoeui.ttf", 24)
for i in range(8):
    t = int((i + 0.5) / 8 * 2000)
    wpf = flat(Image.open(os.path.join(wpf_frames, f"wpf-ring-{i}.png"))).crop((6, 6, 34, 34))
    tt, f = min(frames, key=lambda x: abs(x[0] - (t + 2000)))  # the second loop, past any start-up
    winui = flat(Image.open(os.path.join(winui_frames, f))).crop((48, 704, 76, 732))
    x = lab + i * (cs + 8)
    sheet.paste(wpf.resize((cs, cs), Image.NEAREST), (x, 10))
    sheet.paste(winui.resize((cs, cs), Image.NEAREST), (x, cs + 18))
    d.text((x + 4, 2 * cs + 28), f"{t} ms (WinUI {tt - 2000})", fill=ink, font=small)
d.text((10, 10 + cs // 2 - 14), "WPF", fill=ink, font=small)
d.text((10, cs + 18 + cs // 2 - 14), "WinUI 3", fill=ink, font=small)
sheet.save(os.path.join(out, f"ring-frames-compare-{mode}.png"))
print(f"card {card}; wrote both {mode} sheets to {out}")
