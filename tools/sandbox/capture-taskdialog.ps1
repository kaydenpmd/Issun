# Runs INSIDE Windows Sandbox. Compiles taskdialog\CommandLinks.cs with the
# .NET Framework compiler in Windows, shows its task dialog (command links with
# notes), hovers the first link, and writes screenshots and every element's
# position to C:\IssunOut\command-links.
$out = "C:\IssunOut\command-links"; New-Item -ItemType Directory -Force $out | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
New-Item -ItemType Directory -Force C:\cl | Out-Null
& $csc /nologo /target:winexe /out:C:\cl\CommandLinks.exe /win32manifest:C:\IssunIn\taskdialog\CommandLinks.manifest C:\IssunIn\taskdialog\CommandLinks.cs *> "$out\csc.txt"
if (-not (Test-Path C:\cl\CommandLinks.exe)) { exit 1 }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System.Runtime.InteropServices;
public static class Mouse { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); }
"@
$AE = [System.Windows.Automation.AutomationElement]
Start-Process C:\cl\CommandLinks.exe
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Command Links")
$w = $null
for ($i = 0; $i -lt 40 -and -not $w; $i++) { Start-Sleep -Milliseconds 500; $w = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond) }
if (-not $w) { "no dialog" | Set-Content "$out\taskdialog-error.txt"; exit 1 }
[Mouse]::SetCursorPos(5, 5) | Out-Null
Start-Sleep 2
function Shot($name) {
    $r = $w.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save("$out\$name.png", [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
}
Shot "taskdialog-rest"
$r = $w.Current.BoundingRectangle
$lines = @("window at $r", "os $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion) $((Get-CimInstance Win32_OperatingSystem).Version)")
$first = $null
$w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
    $c = $_.Current; $b = $c.BoundingRectangle
    if (-not $first -and $c.Name -like "First choice*") { $first = $b }
    $lines += "{0,-10} {1,-12} x={2} y={3} w={4} h={5} '{6}'" -f ($c.ControlType.ProgrammaticName -replace "ControlType\.", ""), $c.ClassName, ([int]($b.X - $r.X)), ([int]($b.Y - $r.Y)), ([int]$b.Width), ([int]$b.Height), ($c.Name -replace "`r?`n", " / ")
}
$lines | Set-Content "$out\taskdialog.txt" -Encoding UTF8
if ($first) { [Mouse]::SetCursorPos([int]($first.X + $first.Width / 2), [int]($first.Y + $first.Height / 2)) | Out-Null; Start-Sleep 1; Shot "taskdialog-hover" }
$ws = New-Object -ComObject WScript.Shell; [void]$ws.AppActivate("Command Links"); Start-Sleep -Milliseconds 300; $ws.SendKeys("{ESC}")
"done" | Set-Content "$out\taskdialog-done.txt"
