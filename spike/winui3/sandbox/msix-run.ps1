# Installs the spike's MSIX in Windows Sandbox and runs sandbox\msix-test.ps1
# there. The test certificate is trusted inside the sandbox only. From
# PowerShell on the host:
#   pwsh spike/winui3/sandbox/msix-run.ps1 -Msix <IssunSpike_..._x64.msix> -Runtime <Dependencies\x64\Microsoft.WindowsAppRuntime.2.msix> -Cer <spike-test.cer> -FakeTailscale <tailscale.exe> [-Keep]
# Results: %LOCALAPPDATA%\IssunSandbox\msix-<time>\out.
param([Parameter(Mandatory)][string]$Msix, [Parameter(Mandatory)][string]$Runtime, [Parameter(Mandatory)][string]$Cer,
      [Parameter(Mandatory)][string]$FakeTailscale, [switch]$Keep)
$ErrorActionPreference = "Stop"
$work = Join-Path $env:LOCALAPPDATA ("IssunSandbox\msix-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$in = Join-Path $work "in"; $out = Join-Path $work "out"
New-Item -ItemType Directory -Force $in, $out | Out-Null
function Note($m) { Write-Host ("{0:HH:mm:ss} {1}" -f (Get-Date), $m) }
$running = (wsb list) | Where-Object { $_ -match '^[0-9a-f-]{36}$' }
if ($running) { throw "A sandbox is already running ($running). Close it first." }
Copy-Item $Msix $in; Copy-Item $Runtime (Join-Path $in "Microsoft.WindowsAppRuntime.2.msix")
Copy-Item $Cer (Join-Path $in "spike-test.cer"); Copy-Item $FakeTailscale (Join-Path $in "tailscale.exe")
Copy-Item (Join-Path $PSScriptRoot "msix-test.ps1") $in
$wsb = Join-Path $work "msix.wsb"
@"
<Configuration>
  <MappedFolders>
    <MappedFolder><HostFolder>$in</HostFolder><SandboxFolder>C:\IssunIn</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$out</HostFolder><SandboxFolder>C:\IssunOut</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
</Configuration>
"@ | Set-Content $wsb -Encoding UTF8
function WaitReady($marker, $minutes) {
    $deadline = (Get-Date).AddMinutes($minutes)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep 5
        $script:id = (wsb list) | Where-Object { $_ -match '^[0-9a-f-]{36}$' } | Select-Object -First 1
        if (-not $script:id) { continue }
        wsb exec --id $script:id -r ExistingLogin -c "cmd /c echo ready> C:\IssunOut\$marker" 2>$null | Out-Null
        if (Test-Path (Join-Path $out $marker)) { return $true }
    }
    return $false
}
function Step($s) {
    Note "step: $s"
    wsb exec --id $script:id -r ExistingLogin -c "cmd /c powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IssunIn\msix-test.ps1 -Step $s > C:\IssunOut\ps-$s.txt 2>&1" | Out-Null
}
Note "starting Windows Sandbox ($work)"
Start-Process $wsb
if (-not (WaitReady "ready.txt" 10)) { throw "the sandbox didn't become ready within 10 minutes" }
Note "sandbox ready ($script:id)"
foreach ($s in "trust", "install", "pkgtest", "tray", "uia") { Step $s }
# With the StartupTask left on, have Explorer run the startup apps again (a
# real sign-out ends the sandbox's session for good: it never signed back in
# on 12 Nov 2026), then see whether Windows started the app.
Step "explorerstart"
Step "afterlogon"
Step "uninstall"
Get-Content (Join-Path $out "report.txt")
if (-not $Keep) { wsb stop --id $script:id | Out-Null; Note "sandbox closed" }
Note "results: $out"
