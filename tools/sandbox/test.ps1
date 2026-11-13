# Runs INSIDE Windows Sandbox (Windows PowerShell 5.1, the signed-in user's
# session), started by run.ps1 through `wsb exec`. Tests what only a clean PC
# shows: the first run, the setup window, self-install, an update over a
# running copy, the tray icon, and uninstall. Drives the WinUI windows through UI Automation and
# Windows message boxes with keystrokes (their buttons don't show up in UI
# Automation from here). Writes report.txt, screenshots and copies of Issun's
# logs to C:\IssunOut, which run.ps1 maps to a folder on the host.
#
# Every check that can fail writes a line starting "FAIL"; run.ps1 counts them.
param([string]$Step = "all")
$ErrorActionPreference = "Continue"
$out = "C:\IssunOut"
New-Item -ItemType Directory -Force $out | Out-Null
$report = Join-Path $out "report.txt"
function Say($m) { $line = "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $m; Add-Content -Path $report -Value $line -Encoding UTF8 }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$AE = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]

function Shot($name, $el) {
    try {
        if ($el) { $r = $el.Current.BoundingRectangle; $x=[int]$r.X; $y=[int]$r.Y; $w=[int]$r.Width; $h=[int]$r.Height }
        else { $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; $x=$b.X; $y=$b.Y; $w=$b.Width; $h=$b.Height }
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
        $bmp.Save((Join-Path $out "$($name).png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Say "screenshot $($name).png (${w}x${h})"
    } catch { Say "screenshot $name failed: $($_.Exception.Message)" }
}

function FindWindow($title, $seconds) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $title)
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $w = $AE::RootElement.FindFirst($Tree::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# A window must fit the work area: the main window once opened 820 tall in a
# 775 work area with its title bar off the top of the screen.
function CheckFits($el, $label) {
    $r = $el.Current.BoundingRectangle
    $a = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $msg = "$label window at $([int]$r.X),$([int]$r.Y) size $([int]$r.Width)x$([int]$r.Height) in work area $($a.Width)x$($a.Height)"
    if ($r.Y -lt $a.Y -or $r.X -lt $a.X -or $r.Bottom -gt $a.Bottom + 1 -or $r.Right -gt $a.Right + 1) { Say "FAIL $msg" } else { Say "ok $msg" }
}

function Dump($el, $label) {
    $all = $el.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    Say "--- ${label}: visible elements"
    foreach ($e in $all) {
        $c = $e.Current
        if ($c.IsOffscreen) { continue }
        $type = $c.ControlType.ProgrammaticName -replace "ControlType\.", ""
        if ($type -in @("Text","Button","CheckBox","Edit","Hyperlink")) {
            $extra = ""
            if ($type -eq "CheckBox") { try { $extra = " [" + $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState + "]" } catch {} }
            if ($type -eq "Edit") { try { $extra = " = '" + $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value + "'" } catch {} }
            if (-not $c.IsEnabled) { $extra += " (disabled)" }
            Say ("  {0}: {1}{2}" -f $type, ($c.Name -replace "`r?`n", " / "), $extra)
        }
    }
}

function Click($el, $name) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $b = $el.FindFirst($Tree::Descendants, $cond)
    if (-not $b) { Say "FAIL no button '$name'"; return $false }
    if (-not $b.Current.IsEnabled) { Say "button '$name' is disabled"; return $false }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Say "clicked '$name'"
    return $true
}

function ClickId($el, $id) {
    $b = $el.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)))
    if (-not $b) { Say "FAIL no element with id '$id'"; return $false }
    # WinUI reports what's scrolled out of view as off screen: bring it in.
    if ($b.Current.IsOffscreen) {
        try { $b.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 500 }
        catch { Say "couldn't scroll '$id' into view: $($_.Exception.Message)" }
    }
    if (-not $b.Current.IsEnabled -or $b.Current.IsOffscreen) { Say "FAIL '$id' isn't available"; return $false }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Say "clicked '$id'"
    return $true
}

function OnPage($el, $title) {
    $t = $el.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $title)))
    return [bool]($t -and -not $t.Current.IsOffscreen)
}

# A Windows message box: bring it forward by title and press a key (y, n, Enter).
# A Windows message box: click its button by sending it BM_CLICK, which needs
# no focus. Keystrokes did, and after the tray step's real mouse clicks
# Windows stopped letting this script bring the box forward: "y" went
# nowhere though AppActivate said yes (12 Nov 2026).
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Box {
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string t);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr p, IntPtr a, string c, string t);
    [DllImport("user32")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static bool Click(string title, string button) {
        var box = FindWindowW("#32770", title); if (box == IntPtr.Zero) return false;
        var b = FindWindowExW(box, IntPtr.Zero, "Button", button); if (b == IntPtr.Zero) return false;
        SendMessageW(b, 0x00F5, IntPtr.Zero, IntPtr.Zero); return true;
    }
}
"@
function AnswerBox($title, $keys) {
    $button = switch ($keys) { "y" { "&Yes" } "n" { "&No" } default { "OK" } }
    if ([Box]::Click($title, $button)) { Say "answered '$title' with $button" } else { Say "FAIL no '$button' button in '$title'" }
}

function State($label) {
    Say "=== state: $label"
    $inst = Join-Path $env:LOCALAPPDATA "Programs\Issun"
    if (Test-Path $inst) { Get-ChildItem $inst | ForEach-Object { Say ("  install dir: {0} {1:N0} bytes {2:HH:mm:ss}" -f $_.Name, $_.Length, $_.LastWriteTime) } } else { Say "  install dir: absent" }
    $run = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue
    Say ("  Run key Issun: " + $(if ($run.Issun) { $run.Issun } else { "absent" }))
    $un = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Issun" -ErrorAction SilentlyContinue
    if ($un) { Say "  Apps entry: $($un.DisplayName) $($un.DisplayVersion), uninstall: $($un.UninstallString)" } else { Say "  Apps entry: absent" }
    $lnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Issun.lnk"
    if (Test-Path $lnk) { $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); Say "  Start menu: $($s.TargetPath) $($s.Arguments)" } else { Say "  Start menu: absent" }
    $procs = @(Get-Process Issun* -ErrorAction SilentlyContinue)
    if ($procs.Count) { $procs | ForEach-Object { Say "  process: $($_.Id) $($_.Path)" } } else { Say "  process: none" }
    foreach ($f in @("$env:APPDATA\Issun\settings.json", "$env:LOCALAPPDATA\Issun\window.json", "$env:LOCALAPPDATA\Issun\issun.log")) {
        Say ("  {0}: {1}" -f $f, $(if (Test-Path $f) { "present" } else { "absent" }))
    }
}

function Expect($what, $ok) { if ($ok) { Say "ok $what" } else { Say "FAIL $what" } }

function SaveLogs($tag) {
    $d = Join-Path $out "logs-$tag"; New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item "$env:LOCALAPPDATA\Issun\*.log" $d -ErrorAction SilentlyContinue
    Copy-Item "$env:LOCALAPPDATA\Issun\window.json" $d -ErrorAction SilentlyContinue
    # settings.json holds the key: record only each field's length.
    $sj = "$env:APPDATA\Issun\settings.json"
    if (Test-Path $sj) {
        try { $o = Get-Content $sj -Raw | ConvertFrom-Json; $o.PSObject.Properties | ForEach-Object { Add-Content (Join-Path $d "settings-shape.txt") "$($_.Name): length $("$($_.Value)".Length)" } }
        catch { Add-Content (Join-Path $d "settings-shape.txt") "unreadable: $($_.Exception.Message)" }
    }
}

# Screenshots shown to the owner are in dark mode (their rule, 12 Nov 2026), and
# Issun follows Windows' setting.
$personalize = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
if ((Get-ItemProperty $personalize -ErrorAction SilentlyContinue).AppsUseLightTheme -ne 0) {
    New-Item -Force $personalize | Out-Null
    Set-ItemProperty $personalize AppsUseLightTheme 0 -Type DWord
    Set-ItemProperty $personalize SystemUsesLightTheme 0 -Type DWord
    Say "switched Windows to dark mode"
}

$installDir = Join-Path $env:LOCALAPPDATA "Programs\Issun"
$installed = Join-Path $installDir "Issun.exe"
$download = Get-ChildItem "$env:USERPROFILE\Downloads\Issun-*.exe" | Select-Object -First 1

if ($Step -in @("all","first")) {
    Say "##### first run: $($download.FullName)"
    State "before"
    Start-Process $download.FullName
    $w = FindWindow "Set up Issun" 90
    if (-not $w) { Say "FAIL setup window didn't open within 90 s"; Shot "no-setup" $null; State "no setup window"; SaveLogs "first"; exit 1 }
    Say "ok setup window open"
    Start-Sleep 3
    State "setup window open"
    Expect "installed copy in place" (Test-Path $installed)
    Expect "Apps entry written" (Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Issun")
    Expect "Start menu entry written" (Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Issun.lnk")
    CheckFits $w "setup"
    Start-Sleep 2
    Dump $w "page 0"; Shot "setup-page-0" $w
    function SkipShown { $r = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "Skip"))); return [bool]($r -and $r.Current.Name -eq "Skip" -and -not $r.Current.IsOffscreen) }
    Expect "Skip on the Discord page" (SkipShown)
    # Next asks Discord about the ID, once per click. No Discord here, so the
    # page stays with the reason under the ID, and Skip moves on.
    function NameShown($name) { $r = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))); return [bool]($r -and -not $r.Current.IsOffscreen) }
    Expect "no Discord status card" (-not (NameShown "Set the Discord application ID in Settings. Presence stays off until there is one."))
    Expect "the cards are numbered, with Make sure Discord is open third" ((NameShown "Make an application") -and (NameShown "Enter your application ID") -and (NameShown "Make sure Discord is open") -and (NameShown "1") -and (NameShown "2") -and (NameShown "3"))
    $openDiscord = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "OpenDiscord")))
    Expect "the third card has Open Discord" ($openDiscord -and $openDiscord.Current.Name -eq "Open Discord")
    Click $w "Next" | Out-Null; Start-Sleep 2
    Expect "Next with no ID stays and says why" ((OnPage $w "Connect to Discord") -and (NameShown "An application ID is 17$([char]0x2013)20 digits long."))
    $idBox = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Discord application ID")))
    $idBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("123456789012345678"); Start-Sleep 1
    Expect "editing the ID clears the error" (-not (NameShown "An application ID is 17$([char]0x2013)20 digits long."))
    Click $w "Next" | Out-Null; Start-Sleep 3
    Dump $w "page 0 after Next without Discord"; Shot "setup-page-0-no-discord" $w
    Expect "Next without Discord stays and says Discord isn't running" ((OnPage $w "Connect to Discord") -and (NameShown "Discord isn't running. Open the Discord desktop app and Issun will connect by itself."))
    ClickId $w "Skip" | Out-Null; Start-Sleep 3
    Expect "Skip leads to the Tailscale page" (OnPage $w "Make this PC reachable with a Tailscale Funnel")
    Dump $w "page 1"; Shot "setup-page-1" $w
    Click $w "Next" | Out-Null; Start-Sleep 4
    Expect "Next stays on the Tailscale page without Tailscale" (OnPage $w "Make this PC reachable with a Tailscale Funnel")
    Dump $w "page 1 after Next"; Shot "setup-page-1-refused" $w
    ClickId $w "Skip" | Out-Null; Start-Sleep 3
    Expect "Skip leads to Connect your source" (OnPage $w "Connect your source")
    Dump $w "page 2"; Shot "setup-page-2" $w
    $show = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Show")),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
    Expect "a QR code with only the key can be shown" ($show -and $show.Current.IsEnabled)
    $next = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Next")),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
    Expect "Next is greyed out while no source is connected" ($next -and -not $next.Current.IsEnabled)
    # The page runs below a 600-tall window, and WinUI (unlike WPF) reports
    # what's scrolled out of view as off screen: scroll to the status card.
    $scroller = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::IsScrollPatternAvailableProperty, $true)))
    if ($scroller) { $scroller.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 100); Start-Sleep 1; Say "scrolled to the foot of the page" }
    $waiting = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Waiting...")))
    Expect "the status says Waiting..." ($waiting -and -not $waiting.Current.IsOffscreen)
    Start-Sleep -Milliseconds 700; Shot "source-waiting-a" $w
    Start-Sleep -Milliseconds 600; Shot "source-waiting-b" $w
    ClickId $w "Skip" | Out-Null; Start-Sleep 2
    Expect "Skip leads to Start with Windows" (OnPage $w "Launch Issun on startup?")
    Click $w "Back" | Out-Null; Start-Sleep 1
    Expect "Back returns to Connect your source" (OnPage $w "Connect your source")
    ClickId $w "Skip" | Out-Null; Start-Sleep 2
    Dump $w "page 3"; Shot "setup-page-3" $w
    function Picked($id) { $r = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))); return [bool]($r -and $r.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) }
    function Pick($id) { $r = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))); $r.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Say "picked $id" }
    Shot "setup-page-3-radios" $w
    Expect "Skip on the last page" (SkipShown)
    Expect "Yes is picked on a first run" (Picked "LaunchYes")
    Pick "LaunchNo"; Start-Sleep 2
    Expect "No removes Issun from startup" (-not (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue).Issun)
    Pick "LaunchYes"; Start-Sleep 2
    Click $w "Finish" | Out-Null
    Start-Sleep 3
    Expect "Finish finishes setup" (-not (FindWindow "Set up Issun" 1))
    Expect "Start with Windows registered (ticked by default)" ((Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue).Issun)
    $main = FindWindow "Issun" 10
    if ($main) { CheckFits $main "main"; Shot "main" $main }
    State "after setup closed"
    SaveLogs "first"
}

if ($Step -in @("all","again")) {
    Say "##### opening the installed copy again: the running one should show its window, and setup shouldn't open"
    $before = @(Get-Process Issun* -ErrorAction SilentlyContinue).Count
    Start-Process $installed
    Start-Sleep 6
    Expect "setup didn't open a second time" (-not (FindWindow "Set up Issun" 1))
    Expect "still one Issun running" (@(Get-Process Issun* -ErrorAction SilentlyContinue).Count -eq $before)
    $main = FindWindow "Issun" 5
    if ($main) { CheckFits $main "main" } else { Say "FAIL main window didn't show" }
    Shot "second-open" $null
    # Start with Windows waits for Apply, like the settings above it (the owner, 12 Nov 2026).
    if ($main) {
        $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
        function RunEntry { [bool](Get-ItemProperty $runKey -ErrorAction SilentlyContinue).Issun }
        $box = $main.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Start with Windows")),
            (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))))
        if (-not $box) { Say "FAIL no Start with Windows checkbox" } else {
            try { $box.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 500 } catch {}
            $toggle = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            $before = RunEntry
            $toggle.Toggle(); Start-Sleep 2
            Expect "ticking Start with Windows changes nothing before Apply" ((RunEntry) -eq $before)
            Click $main "Apply" | Out-Null; Start-Sleep 2
            Expect "Apply changes it" ((RunEntry) -ne $before)
            $toggle.Toggle(); Start-Sleep 1
            Click $main "Apply" | Out-Null; Start-Sleep 2
            Expect "and Apply puts it back" ((RunEntry) -eq $before)
        }
    }
}

# The notification-area icon, rewritten by hand for WinUI 3 (12 Nov 2026):
# it's there with the tooltip, a left click opens the window, a right click
# opens the menu, closing the window leaves Issun running, and the menu's
# Quit ends it. Starts the installed copy again afterwards, for the steps that
# follow.
if ($Step -in @("all","tray")) {
    Say "##### the tray icon"
    Add-Type @"
using System; using System.Runtime.InteropServices;
public static class TrayMouse {
    [DllImport("user32")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    public static void Right(int x, int y) { SetCursorPos(x, y); mouse_event(8, 0, 0, 0, IntPtr.Zero); mouse_event(16, 0, 0, 0, IntPtr.Zero); }
    public static void Left(int x, int y) { SetCursorPos(x, y); mouse_event(2, 0, 0, 0, IntPtr.Zero); mouse_event(4, 0, 0, 0, IntPtr.Zero); }
}
"@
    if (-not (Get-Process Issun* -ErrorAction SilentlyContinue)) { Start-Process $installed -ArgumentList "--background"; Start-Sleep 6 }
    # Windows 11 puts a new icon in the overflow; promote Issun's to the taskbar.
    Get-ChildItem "HKCU:\Control Panel\NotifyIconSettings" -ErrorAction SilentlyContinue | ForEach-Object {
        $k = Get-ItemProperty $_.PSPath
        if ($k.ExecutablePath -like "*Issun*") { Set-ItemProperty $_.PSPath IsPromoted 1 -Type DWord; Say "promoted the icon ($($k.ExecutablePath))" }
    }
    Start-Sleep 3
    function FindTray {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        $taskbar = $AE::RootElement.FindFirst($Tree::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, "Shell_TrayWnd")))
        if (-not $taskbar) { return $null }
        # The icon's name is its tooltip, "Issun" and an en dash; the taskbar's
        # button for the window is "Issun - 1 running window", with a hyphen.
        $prefix = "Issun " + [char]0x2013
        foreach ($b in $taskbar.FindAll($Tree::Descendants, $cond)) { if ($b.Current.Name.StartsWith($prefix)) { return $b } }
        return $null
    }
    $icon = $null; $deadline = (Get-Date).AddSeconds(20)
    while (-not $icon -and (Get-Date) -lt $deadline) { $icon = FindTray; if (-not $icon) { Start-Sleep 1 } }
    if (-not $icon) { Say "FAIL no Issun icon in the notification area"; Shot "tray-missing" $null }
    else {
        Say "ok tray icon: $($icon.Current.Name)"
        $r = $icon.Current.BoundingRectangle; $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
        $main = FindWindow "Issun" 1
        if ($main) { $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 2 }
        [TrayMouse]::Left($cx, $cy); Start-Sleep 3
        $main = FindWindow "Issun" 5
        Expect "a click on the icon opens the window" $main
        if ($main) {
            $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 2
            Expect "closing the window leaves Issun running" (Get-Process Issun* -ErrorAction SilentlyContinue)
            Expect "and hides the window" (-not (FindWindow "Issun" 1))
            Shot "tray-after-close" $null
        }
        [TrayMouse]::Right($cx, $cy); Start-Sleep 2
        $menu = $AE::RootElement.FindFirst($Tree::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, "#32768")))
        if (-not $menu) { $menu = $AE::RootElement.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Menu))) }
        if (-not $menu) { Say "FAIL no menu on a right click"; Shot "tray-no-menu" $null; (New-Object -ComObject WScript.Shell).SendKeys("{ESC}") }
        else {
            Shot "tray-menu" $menu
            $all = @($menu.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
            Say ("menu elements: " + (($all | ForEach-Object { ($_.Current.ControlType.ProgrammaticName -replace "ControlType\.", "") + " '" + $_.Current.Name + "'" }) -join ", "))
            $items = @($all | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem })
            $names = @($items | ForEach-Object { $_.Current.Name })
            if ($names.Count) {
                Expect "the menu has Open Issun, Start with Windows and Quit Issun" (($names -contains "Open Issun") -and ($names -contains "Start with Windows") -and ($names -contains "Quit Issun"))
            } else {
                Say "the menu's items aren't in UI Automation from here; its words are in tray-menu.png"
            }
            $quit = $items | Where-Object { $_.Current.Name -eq "Quit Issun" } | Select-Object -First 1
            if ($quit) { $quit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Say "clicked Quit Issun" }
            else {
                # Up from nothing chosen picks the last item, Quit Issun; Enter takes it.
                $h = [IntPtr]$menu.Current.NativeWindowHandle
                [Box]::PostMessageW($h, 0x100, [IntPtr]0x26, [IntPtr]0) | Out-Null; Start-Sleep -Milliseconds 300
                [Box]::PostMessageW($h, 0x100, [IntPtr]0x0D, [IntPtr]0) | Out-Null
                Say "chose Quit Issun with Up and Enter"
            }
            Start-Sleep 6
            Expect "Quit Issun ends it" (-not (Get-Process Issun* -ErrorAction SilentlyContinue))
            Expect "and takes the icon away" (-not (FindTray))
        }
    }
    SaveLogs "tray"
    Start-Process $installed -ArgumentList "--background"; Start-Sleep 6
}

if ($Step -in @("all","update")) {
    Say "##### update over the running copy: opening the download again"
    $old = @(Get-Process Issun* -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    Start-Process $download.FullName
    Start-Sleep 15
    $now = @(Get-Process Issun* -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    Expect "old process quit" (-not ($now | Where-Object { $old -contains $_ }))
    Expect "a new process runs from the install folder" ($now.Count -eq 1)
    Expect "previous build kept beside it" (Test-Path (Join-Path $installDir "Issun.exe.previous"))
    State "after update"
    SaveLogs "update"
}

if ($Step -in @("all","uninstall")) {
    Say "##### uninstall, as Settings > Apps runs it"
    $un = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Issun" -ErrorAction SilentlyContinue
    if (-not $un) { Say "FAIL no Apps entry to uninstall from" } else {
        Start-Process cmd.exe -ArgumentList "/c", "`"$($un.UninstallString)`"" -WindowStyle Hidden
        # No question of Issun's own since 12 Nov 2026 (Settings asks first,
        # in its own flyout); any box here is the "still running" one.
        Start-Sleep 15
        $box = FindWindow "Uninstall Issun" 1
        Expect "no box of Issun's own" (-not $box)
        if ($box) { Shot "uninstall-box" $null }
    }
    State "after uninstall"
    Expect "install folder removed" (-not (Test-Path $installDir))
    Expect "Run entry removed" (-not (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue).Issun)
    Expect "Apps entry removed" (-not (Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Issun"))
    Expect "Start menu entry removed" (-not (Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Issun.lnk"))
    Expect "Issun quit" (-not (Get-Process Issun* -ErrorAction SilentlyContinue))
    Expect "settings kept" (Test-Path "$env:APPDATA\Issun\settings.json")
    SaveLogs "uninstall"
}
# Not in "all": Windows Sandbox has no winget, so this installs it from
# Microsoft's own PowerShell module (PSGallery's Microsoft.WinGet.Client) first.
if ($Step -eq "winget") {
    Say "##### installing winget in the sandbox"
    try {
        Install-PackageProvider -Name NuGet -Force -Scope CurrentUser | Out-Null
        Install-Module Microsoft.WinGet.Client -Force -Scope CurrentUser -Repository PSGallery
        Repair-WinGetPackageManager -Latest -Force -ErrorAction Stop | Out-Null
    } catch { Say "FAIL couldn't install winget: $($_.Exception.Message)" }
    $w = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\winget.exe"
    Expect "winget present" (Test-Path $w)
    if (Test-Path $w) { Say ("winget " + (& $w --version)) }
}

# A first run that stops on the Tailscale page and presses its install button.
if ($Step -eq "tailscale") {
    Say "##### first run, then the Tailscale page's install button"
    Start-Process $download.FullName
    $w = FindWindow "Set up Issun" 90
    if (-not $w) { Say "FAIL setup window didn't open"; exit 1 }
    Start-Sleep 3
    ClickId $w "Skip" | Out-Null  # past the Discord page: no Discord here, so Next waits
    Start-Sleep 3
    Dump $w "Tailscale page before"
    Shot "tailscale-before" $w
    $ts = Get-ChildItem "$env:LOCALAPPDATA\Issun" -ErrorAction SilentlyContinue | Out-Null
    $get = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "InstallTailscale")))
    if (-not $get) { Say "FAIL no install button"; exit 1 }
    Say "pressing '$($get.Current.Name)'"
    $get.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $bar = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ProgressBar)
    Start-Sleep 2
    $p = $w.FindFirst($Tree::Descendants, $bar)
    Expect "progress shown while installing" ($p -and -not $p.Current.IsOffscreen)
    Shot "tailscale-installing" $w
    $deadline = (Get-Date).AddMinutes(14); $shotUac = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep 5
        $p = $w.FindFirst($Tree::Descendants, $bar)
        if (-not $p -or $p.Current.IsOffscreen) { break }
        if (-not $shotUac -and (Get-Process consent -ErrorAction SilentlyContinue)) { Say "a UAC prompt is up"; Shot "tailscale-uac" $null; $shotUac = $true }
    }
    Say "install finished or gave up"
    Start-Sleep 8
    Dump $w "Tailscale page after"
    Shot "tailscale-after" $w
    Shot "tailscale-after-desktop" $null
    Expect "Tailscale installed" (Test-Path "$env:ProgramFiles\Tailscale\tailscale.exe")
    Get-Process tailscale* -ErrorAction SilentlyContinue | ForEach-Object { Say "  process: $($_.Name) $($_.Path)" }
    Get-Service Tailscale -ErrorAction SilentlyContinue | ForEach-Object { Say "  service: $($_.Name) $($_.Status) $($_.StartType)" }
    SaveLogs "tailscale"
}
# A first run that types an address of its own on the Tailscale page, as
# someone without Tailscale would, then checks the source page uses it.
# A first run that skips Tailscale, types an endpoint of its own on Connect your
# source, reloads the QR code, and makes a new key.
if ($Step -eq "address") {
    Say "##### first run with an endpoint of its own"
    Start-Process $download.FullName
    $w = FindWindow "Set up Issun" 90
    if (-not $w) { Say "FAIL setup window didn't open"; exit 1 }
    Start-Sleep 3
    ClickId $w "Skip" | Out-Null; Start-Sleep 3  # past the Discord page: no Discord here, so Next waits
    ClickId $w "Skip" | Out-Null; Start-Sleep 3
    $box = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Endpoint")),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))))
    if (-not $box) { Say "FAIL no endpoint field"; exit 1 }
    $value = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $reload = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "ReloadQr")
    $r = $w.FindFirst($Tree::Descendants, $reload)
    Expect "no reload button before an edit" (-not $r -or $r.Current.IsOffscreen)
    $value.SetValue("http://not-https.example.com")
    Start-Sleep 1
    $r = $w.FindFirst($Tree::Descendants, $reload)
    Expect "an edit shows the reload button" ($r -and -not $r.Current.IsOffscreen)
    ClickId $w "ReloadQr" | Out-Null; Start-Sleep 2
    Dump $w "after an http endpoint"; Shot "endpoint-rejected" $w
    Expect "an http endpoint isn't saved" (-not ((Get-Content "$env:APPDATA\Issun\settings.json" -Raw | ConvertFrom-Json).publicBase))
    $value.SetValue("issun-test.example.com")
    ClickId $w "ReloadQr" | Out-Null; Start-Sleep 2
    Expect "the field shows the full endpoint" ($value.Current.Value -eq "https://issun-test.example.com/now-playing")
    $r = $w.FindFirst($Tree::Descendants, $reload)
    Expect "the reload button goes once applied" (-not $r -or $r.Current.IsOffscreen)
    $sj = Get-Content "$env:APPDATA\Issun\settings.json" -Raw | ConvertFrom-Json
    # Since 13 Nov 2026 a typed endpoint goes into the QR code only: the
    # Public address override it used to be saved as is gone.
    Expect "not saved to settings" (-not $sj.publicBase)
    Expect "the field keeps it" ($value.Current.Value -eq "https://issun-test.example.com/now-playing")
    $oldKey = $sj.key
    ClickId $w "NewKey" | Out-Null; Start-Sleep 2
    $sj = Get-Content "$env:APPDATA\Issun\settings.json" -Raw | ConvertFrom-Json
    Expect "the new-key button makes a new key" ($sj.key -and $sj.key -ne $oldKey)
    Click $w "Show" | Out-Null; Start-Sleep 1
    Dump $w "endpoint applied"; Shot "endpoint-applied" $w
    SaveLogs "address"
}
# The QR code's regenerate rules (the owner, 12 Nov 2026): an edit shows the
# button and undoing it hides it; a new key mid-edit waits for the button and
# keeps it up even after the edit is undone; a new key with no edit redraws at once.
if ($Step -eq "qrrules") {
    Say "##### the QR code's regenerate rules"
    Start-Process $download.FullName
    $w = FindWindow "Set up Issun" 90
    if (-not $w) { Say "FAIL setup window didn't open"; exit 1 }
    Start-Sleep 3
    ClickId $w "Skip" | Out-Null; Start-Sleep 3  # past the Discord page: no Discord here, so Next waits
    ClickId $w "Skip" | Out-Null; Start-Sleep 3
    $box = $w.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Endpoint")),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))))
    $value = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $reloadCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "ReloadQr")
    function ReloadShown { $r = $w.FindFirst($Tree::Descendants, $reloadCond); return [bool]($r -and -not $r.Current.IsOffscreen) }
    $log = "$env:LOCALAPPDATA\Issun\issun.log"
    # Only lines written since the last Mark count.
    $script:from = 0
    function Mark { $script:from = @(Get-Content $log).Count }
    function LogHas($text) { return [bool](@(Get-Content $log) | Select-Object -Skip $script:from | Where-Object { $_.Contains($text) }) }
    $original = $value.Current.Value
    Expect "starts with no reload button" (-not (ReloadShown))

    $value.SetValue("one.example.com"); Start-Sleep 1
    Expect "an edit shows it" (ReloadShown)
    $value.SetValue($original); Start-Sleep 1
    Expect "undoing the edit hides it" (-not (ReloadShown))

    $value.SetValue("two.example.com"); Start-Sleep 1
    Mark; ClickId $w "NewKey" | Out-Null; Start-Sleep 2
    Expect "a new key mid-edit waits for the button" (LogHas "[setup] new key during an endpoint edit")
    Expect "and doesn't redraw the code" (-not (LogHas "[setup] QR code redrawn with the new key"))
    $value.SetValue($original); Start-Sleep 3
    Expect "undoing the edit leaves the button up" (ReloadShown)
    Expect "and still doesn't redraw the code" (-not (LogHas "[setup] QR code redrawn"))
    Shot "qrrules-stuck" $w
    Mark; ClickId $w "ReloadQr" | Out-Null; Start-Sleep 2
    Expect "pressing it hides it" (-not (ReloadShown))
    Expect "and redraws the code" (LogHas "[setup] QR code redrawn without an endpoint")

    Mark; ClickId $w "NewKey" | Out-Null; Start-Sleep 2
    Expect "a new key with no edit redraws at once" (LogHas "[setup] QR code redrawn with the new key")
    Expect "with no button" (-not (ReloadShown))
    SaveLogs "qrrules"
}
# Not in "all": --screenshot of every screen, dark, from the download, which
# renders and exits without installing. For when this PC's own session can't
# render: a disconnected session draws wrongly, and Issun refuses then.
if ($Step -eq "shots") {
    Say "##### --screenshot of every screen"
    $dir = Join-Path $out "shots"; New-Item -ItemType Directory -Force $dir | Out-Null
    $runs = [ordered]@{
        "1-main" = @(); "2-main-expanded" = @("--expanded");
        "3-setup-discord" = @("--setup-page", "0");
        "4-setup-tailscale" = @("--setup-page", "1");
        "4b-setup-tailscale-not-installed" = @("--setup-page", "1", "--demo-tailscale", "0");
        "4c-setup-tailscale-all-on" = @("--setup-page", "1", "--demo-tailscale", "3");
        "5-setup-connect-hidden" = @("--setup-page", "2");
        "6-setup-connect-shown" = @("--setup-page", "2", "--expanded");
        "7-setup-startup" = @("--setup-page", "3")
    }
    foreach ($name in $runs.Keys) {
        $a = @("--screenshot", (Join-Path $dir "$name.png"), "--theme", "dark") + $runs[$name]
        $err = Join-Path $dir "$name.err.txt"
        $p = Start-Process $download.FullName -ArgumentList $a -PassThru -Wait -RedirectStandardError $err
        $ok = $p.ExitCode -eq 0 -and (Test-Path (Join-Path $dir "$name.png"))
        Expect "$name rendered" $ok
        if (-not $ok) { Get-Content $err -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object { Say "  $_" } }
    }
}
Say "##### done ($Step)"
