# tools

## make_icon.py

Draws `src/Issun/Assets/issun.ico`, Issun's icon, at every size from 16 to
256 px:

```
python tools/make_icon.py src/Issun/Assets/issun.ico
```

Needs Python with Pillow and numpy, and is deterministic: running it twice
writes the same bytes. The window, the tray and the .exe all take their icon
from that one file.

What it draws, so a replacement can keep the idea or knowingly drop it: two
waves arriving at a speech bubble, on a white tile. Ammy's iPhone icon is the
first half of the same sentence, the two waves leaving an album cover, and both
are drawn in the style of the owner's Albums icon: its squircle, its black and
greys, its gradient and its shadow. The wave nearer the bubble is the darker
one, so the signal grows as it arrives. In Okami, Issun is the one who does the
talking for Amaterasu ("Ammy"), which is what this app does for the phone.

From 32 px down each frame is laid out by hand on the pixel grid, with fewer
and heavier shapes (at 16 px only one wave fits), because a picture shrunk that
far turns to grey mush. The frames from 40 px up are the full picture, scaled.

The ink-circle placeholder it replaced on 28 Oct 2026 was drawn by
`make-icon.ps1`, which is still in git history.
