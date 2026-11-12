# Runs inside Windows Sandbox (Windows PowerShell 5.1), started by msix-run.ps1:
# trusts the throwaway test certificate (here only, never on the host),
# installs the spike's MSIX, and checks from inside the package what Issun
# needs: the StartupTask, the tray, a 127.0.0.1 listener, a named pipe,
# running other programs, and where AppData and HKCU writes land.
param([string]$Step)
$ErrorActionPreference = "Continue"
$out = "C:\IssunOut"; $in = "C:\IssunIn"
$report = Join-Path $out "report.txt"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
function Say($m) { $l = "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $m; Add-Content $report $l; Write-Output $l }
function Check($ok, $m) { if ($ok) { Say " ok   $m" } else { Say " FAIL $m" } }
$msix = Get-ChildItem $in -Filter "IssunSpike_*.msix" | Select-Object -First 1
$dep = Join-Path $in "Microsoft.WindowsAppRuntime.2.msix"
$env:ISSUN_SPIKE_LOG = Join-Path $out "msix-spike.log"
$p = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
New-Item $p -Force | Out-Null
Set-ItemProperty $p AppsUseLightTheme 0 -Type DWord
Set-ItemProperty $p SystemUsesLightTheme 0 -Type DWord
function Pkg { Get-AppxPackage -Name "IssunSpike.WinUI3" }

switch ($Step) {
"trust" {
    Say "== before installing"
    Say "     package $($msix.Name), $($msix.Length) bytes ($([math]::Round($msix.Length/1MB,2)) MB); runtime dependency $((Get-Item $dep).Length) bytes ($([math]::Round((Get-Item $dep).Length/1MB,2)) MB)"
    $war = @(Get-AppxPackage -AllUsers "Microsoft.WindowsAppRuntime*" | ForEach-Object { "$($_.Name) $($_.Version)" })
    Say "     Windows App Runtime packages already here: $($war -join '; ')"
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Say "     running elevated: $admin"
    # A settings file in the real %APPDATA% before install, as the owner's
    # Issun has today: can the packaged app read it?
    New-Item -ItemType Directory -Force (Join-Path $env:APPDATA "IssunPlanted") | Out-Null
    Set-Content (Join-Path $env:APPDATA "IssunPlanted\settings.json") '{"planted": "before the package was installed"}'
    $c = Import-Certificate -FilePath (Join-Path $in "spike-test.cer") -CertStoreLocation Cert:\LocalMachine\TrustedPeople
    Check ($c) "trusted the test certificate in the sandbox's LocalMachine\TrustedPeople ($($c.Thumbprint))"
}
"install" {
    Say "== install"
    try { Add-AppxPackage -Path $msix.FullName -ErrorAction Stop; Say "     without the runtime dependency: installed" }
    catch { Say "     without the runtime dependency: refused: $($_.Exception.Message.Split([char]10)[0])" }
    if (-not (Pkg)) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try { Add-AppxPackage -Path $msix.FullName -DependencyPath $dep -ErrorAction Stop } catch { Say "     with it: $($_.Exception.Message)" }
        Say "     with the runtime dependency: took $($sw.ElapsedMilliseconds) ms"
    }
    $pk = Pkg
    Check ($pk) "the package is installed: $($pk.PackageFullName)"
    if ($pk) {
        Say "     install location $($pk.InstallLocation), signature kind $($pk.SignatureKind), dependencies: $(($pk.Dependencies | ForEach-Object { "$($_.Name) $($_.Version)" }) -join '; ')"
        $f = Get-ChildItem $pk.InstallLocation -Recurse -File; Say "     installed: $($f.Count) files, $([math]::Round(($f | Measure-Object Length -Sum).Sum/1MB,1)) MB"
        $rt = Get-AppxPackage "Microsoft.WindowsAppRuntime.2*" | Where-Object { $_.Name -eq "Microsoft.WindowsAppRuntime.2" }
        Say "     Windows App Runtime framework now: $($rt.Name) $($rt.Version), $($rt.InstallLocation)"
        $alias = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\IssunSpike.exe"
        Check (Test-Path $alias) "the execution alias exists ($alias)"
    }
}
"pkgtest" {
    Say "== inside the package: identity, writes, listener, pipe, other programs, StartupTask"
    New-Item -ItemType Directory -Force "C:\Program Files\Tailscale" | Out-Null
    Copy-Item (Join-Path $in "tailscale.exe") "C:\Program Files\Tailscale\tailscale.exe" -Force
    $env:PATH = "$env:PATH;C:\Program Files\Tailscale"
    $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
    if ($port -eq 8787) { $port = 8790 }
    $pipe = "issun-spike-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
    Say "     port $port, pipe $pipe"
    $job = Start-Job -ArgumentList $pipe -ScriptBlock {
        param($name)
        $s = New-Object IO.Pipes.NamedPipeServerStream($name, [IO.Pipes.PipeDirection]::InOut)
        $s.WaitForConnection()
        $r = New-Object IO.StreamReader($s); $w = New-Object IO.StreamWriter($s); $w.AutoFlush = $true
        $line = $r.ReadLine(); $w.WriteLine("hello back from the pipe server"); Start-Sleep 1; $s.Dispose()
        "server got: $line"
    }
    Start-Sleep 2
    $dir = Join-Path $out "pkg"
    Remove-Item $dir -Recurse -ErrorAction SilentlyContinue
    $proc = Start-Process "IssunSpike.exe" -ArgumentList "--package-test $dir --port $port --pipe $pipe --leave-startup-on" -PassThru
    foreach ($i in 1..300) { if (Test-Path (Join-Path $dir "ready")) { break }; Start-Sleep -Milliseconds 200 }
    Check (Test-Path (Join-Path $dir "ready")) "the packaged spike ran its checks"
    $running = Get-Process IssunSpike -ErrorAction SilentlyContinue | Select-Object -First 1
    Say "     its process: $($running.Path)"
    try { $resp = (Invoke-WebRequest "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 5).Content; Check ($resp -eq "ok") "GET http://127.0.0.1:$port/health from outside the package answered '$resp'" }
    catch { Check $false "GET from outside the package: $($_.Exception.Message)" }
    New-Item -ItemType File (Join-Path $dir "stop") | Out-Null
    $jr = Receive-Job $job -Wait -Timeout 10 -ErrorAction SilentlyContinue; Remove-Job $job -Force
    Say "     pipe server side: $jr"
    $j = Get-Content (Join-Path $dir "package-test.json") -Raw | ConvertFrom-Json
    $j.PSObject.Properties | ForEach-Object { Say ("     {0}: {1}" -f $_.Name, $_.Value) }
    Check ($j.pipeReply -eq "hello back from the pipe server") "named pipe from inside the package to a server outside it"
    Check ($j.listenerSelfGet -eq "ok") "Kestrel listening on 127.0.0.1 from inside the package"
    Check ($j.runTailscaleByPath -like "exit 0:*") "ran C:\Program Files\Tailscale\tailscale.exe and read its output"
    Check ($j.startupAfterRequestEnable -eq "Enabled" -and $j.startupAfterDisable -eq "Disabled") "the app turned its StartupTask on ($($j.startupAfterRequestEnable)) and off ($($j.startupAfterDisable))"

    $pk = Pkg; $lc = Join-Path $env:LOCALAPPDATA ("Packages\" + $pk.PackageFamilyName + "\LocalCache")
    foreach ($w in @(@{n = "%LOCALAPPDATA%"; real = Join-Path $env:LOCALAPPDATA "IssunSpikeProbe\probe.txt"; red = Join-Path $lc "Local\IssunSpikeProbe\probe.txt"},
                     @{n = "%APPDATA%"; real = Join-Path $env:APPDATA "IssunSpikeProbe\probe.txt"; red = Join-Path $lc "Roaming\IssunSpikeProbe\probe.txt"},
                     @{n = "%LOCALAPPDATA% by the child program"; real = Join-Path $env:LOCALAPPDATA "FakeCliProbe\probe.txt"; red = Join-Path $lc "Local\FakeCliProbe\probe.txt"})) {
        Say ("     a write to {0}: real folder {1}, package's LocalCache {2}" -f $w.n, (Test-Path $w.real), (Test-Path $w.red))
    }
    Say "     HKCU\Software\IssunSpikeProbe in the real registry: $(Test-Path HKCU:\Software\IssunSpikeProbe)"
    $st = "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$($pk.PackageFamilyName)\IssunSpikeStartup"
    Say "     startup task's record: $(if (Test-Path $st) { (Get-ItemProperty $st | Select-Object -Property State,UserEnabledStartupOnce | Out-String).Trim() -replace '\s+', ' ' } else { 'none' })"
}
"tray" {
    Say "== tray icon and menu, packaged"
    $proc = Start-Process "IssunSpike.exe" -ArgumentList "--tray-test C:\IssunOut\msix-tray" -PassThru
    foreach ($i in 1..150) { if (Test-Path C:\IssunOut\msix-tray\tray-menu.png) { break }; Start-Sleep -Milliseconds 200 }
    Start-Sleep 2
    Check (Test-Path C:\IssunOut\msix-tray\tray-menu.png) "the packaged spike opened its tray menu and captured it"
    Say "     icon rect: $(Get-Content C:\IssunOut\msix-tray\tray-rect.txt -ErrorAction SilentlyContinue)"
}
"uia" {
    Say "== window and UI Automation, packaged"
    Start-Process "IssunSpike.exe" -ArgumentList "--theme dark"
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $win = $null
    foreach ($i in 1..200) {
        $pr = Get-Process IssunSpike -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($pr) { $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pr.Id))) }
        if ($win) { break }; Start-Sleep -Milliseconds 50
    }
    Check $win "found the packaged spike's window"
    Start-Sleep 2
    $copy = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Copy key")))
    if ($copy) { $copy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1 }
    Check ((Get-Content $env:ISSUN_SPIKE_LOG) -match "Copy key clicked") "found Copy key and invoking it ran its handler"
    $b = $win.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$b.Width), ([int]$b.Height); $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$b.X, [int]$b.Y, 0, 0, $bmp.Size); $bmp.Save("C:\IssunOut\msix-window-dark.png"); $g.Dispose(); $bmp.Dispose()
    Get-Process IssunSpike -ErrorAction SilentlyContinue | Stop-Process -Force
    Say "== --screenshot, packaged"
    $proc = Start-Process "IssunSpike.exe" -ArgumentList "--screenshot C:\IssunOut\msix-screenshot-dark.png --theme dark" -PassThru
    foreach ($i in 1..150) { if (Test-Path C:\IssunOut\msix-screenshot-dark.png) { break }; Start-Sleep -Milliseconds 200 }
    Check (Test-Path C:\IssunOut\msix-screenshot-dark.png) "--screenshot wrote a PNG from inside the package"
}
"logoff" {
    Say "== signing out with the StartupTask left on"
    Get-Process IssunSpike -ErrorAction SilentlyContinue | Stop-Process -Force
    Add-Content $env:ISSUN_SPIKE_LOG "---- signing out $(Get-Date -Format o)"
    shutdown.exe /l /f
}
"explorerstart" {
    # Windows Sandbox doesn't sign back in after a sign-out, so make Explorer
    # run the startup apps again instead, as it does once per sign-in: clear
    # its once-per-session flag and restart it.
    Say "== startup apps run again: Explorer restarted with its once-per-session flag cleared"
    Get-Process IssunSpike -ErrorAction SilentlyContinue | Stop-Process -Force
    Add-Content $env:ISSUN_SPIKE_LOG "---- signing out $(Get-Date -Format o)"
    $si = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo"
    $flags = @(Get-ChildItem $si -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -eq "StartupHasBeenRun" })
    Say "     StartupHasBeenRun keys found: $($flags.Count) ($(($flags | ForEach-Object { $_.Name }) -join ', '))"
    $flags | ForEach-Object { Remove-Item $_.PSPath -Recurse -Force }
    Stop-Process -Name explorer -Force
    Start-Sleep 5
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }
    Start-Sleep 40
}
"afterlogon" {
    # Started by Windows, the app has no ISSUN_SPIKE_LOG, so it logs to its
    # default %LOCALAPPDATA%\IssunSpike\spike.log, which MSIX redirects.
    $pk = Pkg
    $own = Join-Path $env:LOCALAPPDATA ("Packages\" + $pk.PackageFamilyName + "\LocalCache\Local\IssunSpike\spike.log")
    Say "     the app's own log at $own exists: $(Test-Path $own)"
    if (Test-Path $own) { Copy-Item $own (Join-Path $out "startup-spike.log"); Get-Content $own | Select-String "activation|\[package\]" | Select-Object -Last 4 | ForEach-Object { Say "     log: $($_.Line)" } }
    $env:ISSUN_SPIKE_LOG = $own
    $pr = @(Get-Process IssunSpike -ErrorAction SilentlyContinue)
    Say "     IssunSpike processes running: $($pr.Count) $(($pr | ForEach-Object { $_.Path }) -join ', ')"
    $after = if (Test-Path $env:ISSUN_SPIKE_LOG) { Get-Content $env:ISSUN_SPIKE_LOG -Raw } else { "" }
    Check ($pr.Count -gt 0 -and $after -match "kind StartupTask") "Windows started the packaged spike at sign-in through its StartupTask"
    ($after -split "`n" | Select-String "activation|package\]") | ForEach-Object { Say "     log: $($_.Line.Trim())" }
}
"uninstall" {
    Say "== uninstall"
    Get-Process IssunSpike -ErrorAction SilentlyContinue | Stop-Process -Force
    $pk = Pkg
    Remove-AppxPackage $pk.PackageFullName
    Check (-not (Pkg)) "removed"
    Say "     its LocalCache left behind: $(Test-Path (Join-Path $env:LOCALAPPDATA ('Packages\' + $pk.PackageFamilyName)))"
    $st = "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$($pk.PackageFamilyName)"
    Say "     its startup record left behind: $(Test-Path $st)"
}
}
