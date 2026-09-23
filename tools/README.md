# tools

## make-icon.ps1

Draws `src/Issun/Assets/issun.ico`, Issun's **placeholder** icon, at every size
from 16 to 256 px:

```
pwsh -File tools/make-icon.ps1
pwsh -File tools/make-icon.ps1 -Preview $env:TEMP\issun-icon-preview.png
```

It is a stand-in, and the owner may replace it at any time: drop any `.ico`
with 16-256 px frames over `src/Issun/Assets/issun.ico` and rebuild. The window,
the tray and the .exe all take their icon from that one file, and nothing else
depends on this script.

What it draws, so a replacement can keep the idea or knowingly drop it: in
Okami, Issun is the tiny glowing Poncle who travels with Amaterasu ("Ammy") and
does the talking she can't, which is what this app does for the phone. Ammy's
icon is sumi-e ink on paper with red accents, so this is warm paper, one black
brush ring (an enso) open at the lower right, a small green glow at its centre,
and a red seal where there is room for one.
