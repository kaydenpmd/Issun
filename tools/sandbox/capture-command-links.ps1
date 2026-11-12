# Runs INSIDE Windows Sandbox. Opens File Explorer's "Replace or Skip Files"
# dialog, Windows 11's own command links, by copying a file onto one that
# already exists, then screenshots it and writes every element's position
# (UI Automation) to C:\IssunOut. Run once per theme: -Theme light or dark.
# Used for the setup window's command links (12 Nov 2026); the results are
# saved in the design library under microsoft/command-links.
param([ValidateSet("light", "dark")][string]$Theme = "dark")
$out = "C:\IssunOut\command-links"; New-Item -ItemType Directory -Force $out | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$AE = [System.Windows.Automation.AutomationElement]

$p = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
New-Item -Force $p -ErrorAction SilentlyContinue | Out-Null
$v = if ($Theme -eq "dark") { 0 } else { 1 }
Set-ItemProperty $p AppsUseLightTheme $v -Type DWord
Set-ItemProperty $p SystemUsesLightTheme $v -Type DWord

New-Item -ItemType Directory -Force C:\cl-src, C:\cl-dst | Out-Null
"new" | Set-Content C:\cl-src\example.txt
"old" | Set-Content C:\cl-dst\example.txt
(Get-Item C:\cl-dst\example.txt).LastWriteTime = (Get-Date).AddDays(-3)

# CopyHere returns at once; the dialog belongs to Explorer's copy engine.
$job = Start-Job { (New-Object -ComObject Shell.Application).Namespace("C:\cl-dst").CopyHere("C:\cl-src\example.txt"); Start-Sleep 60 }
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Replace or Skip Files")
$w = $null
for ($i = 0; $i -lt 40 -and -not $w; $i++) { Start-Sleep -Milliseconds 500; $w = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) }
if (-not $w) { "no dialog" | Set-Content "$out\$Theme-error.txt"; exit 1 }
Start-Sleep 2
$r = $w.Current.BoundingRectangle
$bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
$bmp.Save("$out\replace-or-skip-$Theme.png", [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()

$lines = @("window $($w.Current.Name) at $r, dpi scale $([System.Windows.Forms.Screen]::PrimaryScreen.Bounds)", "os $((Get-CimInstance Win32_OperatingSystem).Version) build $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion)")
$w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
    $c = $_.Current; $b = $c.BoundingRectangle
    $lines += "{0,-12} {1,-14} x={2} y={3} w={4} h={5} '{6}'" -f ($c.ControlType.ProgrammaticName -replace "ControlType\.", ""), $c.ClassName, ([int]($b.X - $r.X)), ([int]($b.Y - $r.Y)), ([int]$b.Width), ([int]$b.Height), ($c.Name -replace "`r?`n", " / ")
}
$lines | Set-Content "$out\replace-or-skip-$Theme.txt" -Encoding UTF8
# Leave the dialog by pressing Escape (Skip-like: nothing is copied).
$ws = New-Object -ComObject WScript.Shell; [void]$ws.AppActivate("Replace or Skip Files"); Start-Sleep -Milliseconds 300; $ws.SendKeys("{ESC}")
Stop-Job $job -ErrorAction SilentlyContinue
"done" | Set-Content "$out\$Theme-done.txt"
