# Renders Issun's WPF ProgressRing (src/Issun/Views/ProgressRing.cs) at set
# points in its 2 s loop, by setting its private Progress property, since
# WPF's animation clock doesn't run in an offscreen render. 16 x 16 at 1.25x,
# on the dark card colour, in the accent colour the WinUI render measured.
#   pwsh -File wpf-ring-frames.ps1 <Issun.dll> <outdir> <accent hex> <card hex>
param([string]$Dll, [string]$Out, [string]$Accent = "#60CDFF", [string]$Card = "#2B2B2B")
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$asm = [Reflection.Assembly]::LoadFrom($Dll)
$type = $asm.GetType("Issun.Views.ProgressRing")
$dpField = $type.GetField("ProgressProperty", [Reflection.BindingFlags]"NonPublic,Static")
$dp = $dpField.GetValue($null)
New-Item -ItemType Directory -Force $Out | Out-Null
foreach ($i in 0..7) {
    $p = ($i + 0.5) / 8
    $ring = [Activator]::CreateInstance($type)
    $ring.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString($Accent)
    $host_ = [Windows.Controls.Border]::new()
    $host_.Background = [Windows.Media.BrushConverter]::new().ConvertFromString($Card)
    $host_.Width = 32; $host_.Height = 32
    $ring.Width = 16; $ring.Height = 16
    $ring.HorizontalAlignment = "Center"; $ring.VerticalAlignment = "Center"
    $host_.Child = $ring
    $ring.SetValue($dp, [double]$p)
    $host_.Measure([Windows.Size]::new(32, 32)); $host_.Arrange([Windows.Rect]::new(0, 0, 32, 32)); $host_.UpdateLayout()
    $bmp = [Windows.Media.Imaging.RenderTargetBitmap]::new(40, 40, 120, 120, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($host_)
    $enc = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $f = [IO.File]::Create((Join-Path $Out ("wpf-ring-{0}.png" -f $i))); $enc.Save($f); $f.Close()
}
"wrote 8 frames to $Out"
