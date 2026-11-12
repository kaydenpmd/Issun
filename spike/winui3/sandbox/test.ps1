# Runs inside Windows Sandbox (Windows PowerShell 5.1), started by run.ps1.
# Checks the WinUI 3 spike on a clean Windows: no .NET, no Windows App SDK
# runtime installed; then measures cold start against WPF Issun, drives the
# spike with UI Automation, opens its tray menu and restarts Explorer under it.
# Every check prints "ok" or "FAIL"; results land in C:\IssunOut.
param([string]$Step = "all", [ValidateSet("dark", "light")][string]$Theme = "dark")
$ErrorActionPreference = "Continue"
$out = "C:\IssunOut"; $in = "C:\IssunIn"
$report = Join-Path $out "report.txt"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
function Say($m) { $l = "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $m; Add-Content $report $l; Write-Output $l }
function Check($ok, $m) { if ($ok) { Say " ok   $m" } else { Say " FAIL $m" } }
function Shot($name, [System.Drawing.Rectangle]$r) {
    $bmp = New-Object System.Drawing.Bitmap $r.Width, $r.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.X, $r.Y, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
$root = [System.Windows.Automation.AutomationElement]::RootElement
$TS = [System.Windows.Automation.TreeScope]
$AE = [System.Windows.Automation.AutomationElement]
function Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
$dl = Join-Path $env:USERPROFILE "Downloads"
$spike = Join-Path $dl "IssunSpike.exe"; $wpf = Join-Path $dl "Issun.exe"; $base = Join-Path $dl "BaselineWpf.exe"
$env:ISSUN_SPIKE_LOG = Join-Path $out "spike.log"

# Dark mode by default, for every picture (the owner's rule) and so the tray
# menu has something to follow; -Theme light for the design library's light
# captures.
$p = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
New-Item $p -Force | Out-Null
$light = [int]($Theme -eq "light")
Set-ItemProperty $p AppsUseLightTheme $light -Type DWord
Set-ItemProperty $p SystemUsesLightTheme $light -Type DWord

if ($Step -in "all", "clean") {
    Say "== clean PC"
    $os = Get-CimInstance Win32_OperatingSystem
    Say "     os $($os.Caption) $($os.Version) build $($os.BuildNumber)"
    $war = @(Get-AppxPackage -AllUsers *WindowsAppRuntime* -ErrorAction SilentlyContinue)
    Say ("     Windows App Runtime packages: " + $(if ($war) { ($war | ForEach-Object { "$($_.Name) $($_.Version)" }) -join "; " } else { "none" }))
    # Windows 11 24H2 ships Windows App Runtime packages of its own (the CBS
    # ones). A self-contained app must not use them; the start step checks
    # which copy the spike actually loaded.
    Say "     (inbox, part of Windows itself: $($war.Count) package(s); not installed by anyone)"
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $shared = Test-Path "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"
    Check (-not $dotnet -and -not $shared) "no .NET runtime is installed (dotnet: $([bool]$dotnet), desktop runtime folder: $shared)"
    Check (-not (Test-Path (Join-Path $env:TEMP ".net"))) "nothing unpacked yet under %TEMP%\.net"
    $size = (Get-Item $spike).Length
    Say "     IssunSpike.exe $size bytes ($([math]::Round($size/1MB,2)) MB); Issun.exe (WPF) $((Get-Item $wpf).Length) bytes ($([math]::Round((Get-Item $wpf).Length/1MB,2)) MB)"
}

# Starts an exe, and times how long until UI Automation finds a button in a
# window of that process: the moment the window is up and usable. The same
# yardstick for both builds.
function TimeStart($exe, $arguments, $label) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $proc = if ($arguments) { Start-Process $exe -ArgumentList $arguments -PassThru } else { Start-Process $exe -PassThru }
    $found = $null
    while ($sw.ElapsedMilliseconds -lt 60000) {
        $win = $root.FindFirst($TS::Children, (Cond $AE::ProcessIdProperty $proc.Id))
        if ($win) {
            $btn = $win.FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button)))
            if ($btn) { $found = $sw.ElapsedMilliseconds; break }
        }
        Start-Sleep -Milliseconds 15
    }
    Say ("     {0}: {1}" -f $label, $(if ($found) { "$found ms" } else { "no window in 60 s" }))
    return @{ Proc = $proc; Ms = $found }
}
function Stop($r) { if ($r.Proc -and -not $r.Proc.HasExited) { Stop-Process -Id $r.Proc.Id -Force; $r.Proc.WaitForExit(5000) | Out-Null }; Start-Sleep -Milliseconds 800 }

if ($Step -in "all", "start") {
    Say "== cold start (first run unpacks into %TEMP%\.net), then 5 warm starts each"
    $times = @{}
    foreach ($app in @(@{n = "WinUI spike"; e = $spike; a = "--theme dark"}, @{n = "WPF Issun --demo"; e = $wpf; a = "--demo --theme dark"}, @{n = "WPF baseline (one Fluent page)"; e = $base; a = ""})) {
        $first = TimeStart $app.e $app.a "$($app.n) first run"
        Check ($first.Ms) "$($app.n) opened a window on a clean PC"
        if ($app.n -like "WinUI*") {
            $x = Get-ChildItem (Join-Path $env:TEMP ".net\IssunSpike") -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($x) { $f = Get-ChildItem $x.FullName -Recurse -File; Say "     unpacked: $($f.Count) files, $([math]::Round(($f | Measure-Object Length -Sum).Sum/1MB,1)) MB in $($x.FullName)" }
        } else {
            $x = Get-ChildItem (Join-Path $env:TEMP $(if ($app.e -eq $base) { ".net\BaselineWpf" } else { ".net\Issun" })) -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($x) { $f = Get-ChildItem $x.FullName -Recurse -File; Say "     unpacked: $($f.Count) files, $([math]::Round(($f | Measure-Object Length -Sum).Sum/1MB,1)) MB in $($x.FullName)" }
        }
        Stop $first
        $warm = @()
        foreach ($i in 1..5) { $r = TimeStart $app.e $app.a "$($app.n) warm $i"; $warm += $r.Ms; Stop $r }
        $sorted = $warm | Sort-Object
        Say "     $($app.n): first $($first.Ms) ms, warm median $($sorted[2]) ms (min $($sorted[0]), max $($sorted[4]))"
    }
    $loaded = @(Get-Content $env:ISSUN_SPIKE_LOG | Select-String "\[env\] loaded" | ForEach-Object { $_.Line })
    $loaded | Select-Object -First 8 | ForEach-Object { Say "     $_" }
    Check ($loaded.Count -gt 0 -and -not ($loaded -match "WindowsApps")) "the spike loaded its own Windows App SDK DLLs (from %TEMP%\.net), none of Windows' inbox ones"
    Get-Content $env:ISSUN_SPIKE_LOG -ErrorAction SilentlyContinue | Select-String "first frame" | ForEach-Object { Say "     spike log: $($_.Line)" }
}

if ($Step -in "all", "uia") {
    Say "== UI Automation and a live picture of the spike's window"
    $proc = Start-Process $spike -ArgumentList "--theme $Theme" -PassThru
    $win = $null
    foreach ($i in 1..200) { $win = $root.FindFirst($TS::Children, (Cond $AE::ProcessIdProperty $proc.Id)); if ($win) { break }; Start-Sleep -Milliseconds 50 }
    Check $win "found the spike's window ($($win.Current.Name), class $($win.Current.ClassName))"
    Start-Sleep 2
    $copy = $win.FindFirst($TS::Descendants, (Cond $AE::NameProperty "Copy key"))
    Check $copy "found the Copy key button by name"
    if ($copy) {
        $copy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 1
        Check ((Get-Content $env:ISSUN_SPIKE_LOG) -match "Copy key clicked") "invoking it ran its handler (the spike logged the click)"
    }
    $newKey = $win.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty "NewKey"))
    Check $newKey "found the [text] button by AutomationId NewKey"
    $edit = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((Cond $AE::NameProperty "Endpoint"), (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit)))))
    if ($edit) {
        $v = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        Check ($v -like "https://*") "read the Endpoint box's value: $v"
    } else { Check $false "found the Endpoint box by name" }
    $names = @($win.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) | ForEach-Object { $_.Current.Name })
    Say "     buttons UI Automation sees: $($names -join ' | ')"
    $ring = $win.FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::ProgressBar)))
    Say "     the ProgressRing appears as: $(if ($ring) { "ProgressBar '$($ring.Current.Name)'" } else { 'nothing' })"
    $b = $win.Current.BoundingRectangle
    Shot "live-spike-window-$Theme" (New-Object System.Drawing.Rectangle ([int]$b.X), ([int]$b.Y), ([int]$b.Width), ([int]$b.Height))
    if ($ring) {
        # Scroll the page to its foot so the whole ring is on screen: at this
        # window height the status row sits under the window's edge.
        $sv = $win.FindFirst($TS::Descendants, (Cond $AE::IsScrollPatternAvailableProperty $true))
        if ($sv) { $sv.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100); Start-Sleep 1 }
        $rb = $ring.Current.BoundingRectangle
        Say "     ring on screen at $([int]$rb.X),$([int]$rb.Y) $([int]$rb.Width)x$([int]$rb.Height)"
        foreach ($k in 0..7) { Shot ("live-ring-{0}-{1}" -f $Theme, $k) (New-Object System.Drawing.Rectangle ([int]$rb.X - 6), ([int]$rb.Y - 6), ([int]$rb.Width + 12), ([int]$rb.Height + 12)); Start-Sleep -Milliseconds 250 }
    }
    Stop-Process -Id $proc.Id -Force
}

if ($Step -in "all", "tray") {
    Say "== tray icon and menu"
    Remove-Item (Join-Path $out "tray") -Recurse -ErrorAction SilentlyContinue
    $proc = Start-Process $spike -ArgumentList "--tray-test C:\IssunOut\tray" -PassThru
    $proc.WaitForExit(30000) | Out-Null
    Check ($proc.ExitCode -eq 0) "the spike opened its own tray menu and captured it (exit $($proc.ExitCode))"
    Say "     icon rect: $(Get-Content C:\IssunOut\tray\tray-rect.txt -ErrorAction SilentlyContinue); menu rect: $(Get-Content C:\IssunOut\tray\tray-menu-rect.txt -ErrorAction SilentlyContinue)"

    Say "== the icon survives Explorer restarting"
    $proc = Start-Process $spike -ArgumentList "--theme dark" -PassThru
    Start-Sleep 4
    $tray = $root.FindFirst($TS::Descendants, (Cond $AE::NameProperty "Issun"))
    Say "     UI Automation finds an element named Issun: $(if ($tray) { "$($tray.Current.ControlType.ProgrammaticName) class $($tray.Current.ClassName)" } else { 'no' })"
    Stop-Process -Name explorer -Force
    Start-Sleep 8
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer; Start-Sleep 6 }
    Check ((Get-Content $env:ISSUN_SPIKE_LOG) -match "TaskbarCreated") "the spike saw TaskbarCreated and added its icon again"
    $adds = @((Get-Content $env:ISSUN_SPIKE_LOG) -match "NIM_ADD ok").Count
    Say "     NIM_ADD ok lines in the log so far: $adds"
    Stop-Process -Id $proc.Id -Force
}

if ($Step -in "all", "shot") {
    Say "== offscreen --screenshot on a clean PC"
    $proc = Start-Process $spike -ArgumentList "--screenshot C:\IssunOut\sandbox-screenshot-dark.png --theme dark" -PassThru
    $proc.WaitForExit(60000) | Out-Null
    Check ($proc.ExitCode -eq 0 -and (Test-Path C:\IssunOut\sandbox-screenshot-dark.png)) "--screenshot wrote a PNG (exit $($proc.ExitCode))"
}
Say "== done"
