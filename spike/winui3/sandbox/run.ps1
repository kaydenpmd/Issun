# Runs the WinUI 3 spike on a clean Windows in Windows Sandbox, beside the WPF
# Issun, through sandbox\test.ps1. From PowerShell on the host:
#   pwsh spike/winui3/sandbox/run.ps1 -Spike <IssunSpike.exe> -Wpf <Issun.exe> -Baseline <BaselineWpf.exe> [-Keep]
# Results: %LOCALAPPDATA%\IssunSandbox\spike-<time>\out (report.txt, pictures, spike.log).
# The WPF Issun must be a local (build 0) publish run with --demo only, which
# neither installs itself nor touches real data.
param([Parameter(Mandatory)][string]$Spike, [Parameter(Mandatory)][string]$Wpf, [Parameter(Mandatory)][string]$Baseline, [switch]$Keep,
      [string[]]$Steps = @("clean", "start", "uia", "tray", "shot"), [ValidateSet("dark", "light")][string]$Theme = "dark")
$Steps = @($Steps | ForEach-Object { $_ -split "," } | Where-Object { $_ })
$ErrorActionPreference = "Stop"
$work = Join-Path $env:LOCALAPPDATA ("IssunSandbox\spike-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$in = Join-Path $work "in"; $out = Join-Path $work "out"
New-Item -ItemType Directory -Force $in, $out | Out-Null
function Note($m) { Write-Host ("{0:HH:mm:ss} {1}" -f (Get-Date), $m) }
$running = (wsb list) | Where-Object { $_ -match '^[0-9a-f-]{36}$' }
if ($running) { throw "A sandbox is already running ($running). Close it first." }
Copy-Item $Spike (Join-Path $in "IssunSpike.exe")
Copy-Item $Wpf (Join-Path $in "Issun.exe")
Copy-Item $Baseline (Join-Path $in "BaselineWpf.exe")
Copy-Item (Join-Path $PSScriptRoot "test.ps1") $in
$wsb = Join-Path $work "spike.wsb"
@"
<Configuration>
  <MappedFolders>
    <MappedFolder><HostFolder>$in</HostFolder><SandboxFolder>C:\IssunIn</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$out</HostFolder><SandboxFolder>C:\IssunOut</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
</Configuration>
"@ | Set-Content $wsb -Encoding UTF8
Note "starting Windows Sandbox ($work)"
Start-Process $wsb
$id = $null; $deadline = (Get-Date).AddMinutes(10)
while ((Get-Date) -lt $deadline) {
    Start-Sleep 5
    $id = (wsb list) | Where-Object { $_ -match '^[0-9a-f-]{36}$' } | Select-Object -First 1
    if (-not $id) { continue }
    wsb exec --id $id -r ExistingLogin -c "cmd /c echo ready> C:\IssunOut\ready.txt" 2>$null | Out-Null
    if (Test-Path (Join-Path $out "ready.txt")) { break }
}
if (-not (Test-Path (Join-Path $out "ready.txt"))) { throw "the sandbox didn't become ready within 10 minutes" }
Note "sandbox ready ($id)"
wsb exec --id $id -r ExistingLogin -c "cmd /c copy C:\IssunIn\*.exe %USERPROFILE%\Downloads\" | Out-Null
foreach ($s in $Steps) {
    Note "step: $s"
    wsb exec --id $id -r ExistingLogin -c "cmd /c powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IssunIn\test.ps1 -Step $s -Theme $Theme > C:\IssunOut\ps-$s.txt 2>&1" | Out-Null
}
Get-Content (Join-Path $out "report.txt")
if (-not $Keep) { wsb stop --id $id | Out-Null; Note "sandbox closed" }
Note "results: $out"
