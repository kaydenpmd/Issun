# Tests a release-numbered Issun on a clean Windows in Windows Sandbox, away
# from the live receiver: first run, setup window, self-install, update over a
# running copy, uninstall. Run from PowerShell on the host (not Claude Code's
# sandboxed Bash):
#
#   pwsh tools/sandbox/run.ps1                 # build, test, close the sandbox
#   pwsh tools/sandbox/run.ps1 -Keep           # leave the sandbox open after
#   pwsh tools/sandbox/run.ps1 -Steps first    # only some of: first, again, update, uninstall
#
# Needs Windows Sandbox (Windows 11 Pro; the optional feature
# Containers-DisposableClientVM) and its `wsb` command. A build here gets the
# commit count as its build number, so it installs itself like a release does,
# which is why it must never be opened on this PC outside the sandbox: it
# would replace the installed Issun. The sandbox has its own network, so
# Issun there can't touch port 8787 or Discord here.
#
# Results go to %LOCALAPPDATA%\IssunSandbox\<time>\out: report.txt (every line
# that failed starts with FAIL), screenshots, and Issun's own logs.
param(
    [string[]]$Steps = @("first", "again", "update", "uninstall"),
    [switch]$Keep,
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
# pwsh -File passes "-Steps first,update" as one string.
$Steps = @($Steps | ForEach-Object { $_ -split "," } | Where-Object { $_ })
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$work = Join-Path $env:LOCALAPPDATA ("IssunSandbox\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$in = Join-Path $work "in"; $out = Join-Path $work "out"; $pub = Join-Path $env:LOCALAPPDATA "IssunSandbox\publish"
New-Item -ItemType Directory -Force $in, $out | Out-Null
function Note($m) { Write-Host ("{0:HH:mm:ss} {1}" -f (Get-Date), $m) }

if (-not (Get-Command wsb -ErrorAction SilentlyContinue)) { throw "Windows Sandbox's wsb command isn't here. Turn on the optional feature Containers-DisposableClientVM (dism /online /enable-feature /featurename:Containers-DisposableClientVM /all) and restart." }
$running = (wsb list) | Where-Object { $_ -match '^[0-9a-f-]{36}$' }
if ($running) { throw "A sandbox is already running ($running). Close it first: Windows Sandbox runs one at a time." }

$build = (git -C $repo rev-list --count HEAD).Trim()
$sha = (git -C $repo rev-parse --short HEAD).Trim()
$dirty = if (git -C $repo status --porcelain) { "-dirty" } else { "" }
if (-not $NoBuild) {
    Note "publishing build $build ($sha$dirty)"
    dotnet publish (Join-Path $repo "src\Issun\Issun.csproj") -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:IssunBuild=$build -p:IssunCommit="$sha$dirty" -o $pub | Select-Object -Last 1
    if ($LASTEXITCODE) { throw "dotnet publish failed" }
}
Copy-Item (Join-Path $pub "Issun.exe") (Join-Path $in "Issun-test-b$build-$sha$dirty.exe")
Copy-Item (Join-Path $PSScriptRoot "test.ps1") $in

$wsb = Join-Path $work "issun-test.wsb"
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

# The first start of a new Sandbox version downloads updates for a minute or
# two; wait until a command runs in the signed-in session.
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
wsb exec --id $id -r ExistingLogin -c "cmd /c copy C:\IssunIn\Issun-*.exe %USERPROFILE%\Downloads\" | Out-Null

foreach ($s in $Steps) {
    Note "step: $s"
    wsb exec --id $id -r ExistingLogin -c "cmd /c powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IssunIn\test.ps1 -Step $s > C:\IssunOut\ps-$s.txt 2>&1" | Out-Null
    $err = Get-Content (Join-Path $out "ps-$s.txt") -ErrorAction SilentlyContinue
    if ($err) { Write-Host "  PowerShell said:"; $err | ForEach-Object { Write-Host "  $_" } }
}

$report = Join-Path $out "report.txt"
$fails = @(Select-String -Path $report -Pattern " FAIL " -SimpleMatch -ErrorAction SilentlyContinue)
Get-Content $report | Where-Object { $_ -match " (ok|FAIL) " }
Note ("{0} failed; full report: {1}" -f $fails.Count, $report)
if (-not $Keep) { wsb stop --id $id | Out-Null; Note "sandbox closed" }
