# Builds main's HEAD exactly as CI does (same publish command, IssunBuild =
# git rev-list --count HEAD) and swaps it in for the owner's installed Issun,
# keeping the old build beside it as Issun.exe.b<old>.bak. This is how builds
# 109 to 127 were installed on 12-13 Nov 2026 without a release (CLAUDE.md,
# "Updating the installed copy"). Updating stops the live receiver for a few
# seconds: get the owner's yes first.
#
#   pwsh -NoProfile -File tools/install-local.ps1
#
# Stops the old process by the ID read before the rename: after the rename,
# Get-Process still reports the old path, so a path filter misses it.
# With ISSUN_CLEAR_PUBLICBASE=1 it also drops a leftover publicBase from
# settings.json while Issun is stopped (used once, 13 Nov 2026).
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..'))
$b = git rev-list --count HEAD; $sha = git rev-parse --short HEAD
$o = Join-Path $env:TEMP "issun-publish-$b"
"build $b ($sha)"
$t = dotnet test Issun.sln -v q --nologo 2>&1 | Select-String -Pattern 'Passed!|Failed!'
"$t"
if ("$t" -notmatch 'Passed!') { throw 'tests did not pass' }
dotnet publish src/Issun/Issun.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:IssunBuild=$b -p:IssunCommit=$sha -o $o -v q --nologo | Out-Null
$new = "$o\Issun.exe"; $want = (Get-FileHash $new).Hash; "sha256 $want"
$dir = "$env:LOCALAPPDATA\Programs\Issun"; $exe = "$dir\Issun.exe"
$oldVer = (Invoke-RestMethod http://127.0.0.1:8787/version -TimeoutSec 3) -replace '.*\((\d+)\).*', '$1'
$bak = "$dir\Issun.exe.b$oldVer.bak"
if (Test-Path $bak) { throw "backup $bak exists" }
$old = @(Get-Process Issun -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
Rename-Item $exe $bak
try { Copy-Item $new $exe; if ((Get-FileHash $exe).Hash -ne $want) { throw 'hash mismatch' } }
catch { if (Test-Path $exe) { Remove-Item $exe }; Rename-Item $bak $exe; throw "copy failed, restored: $_" }
$sw = [Diagnostics.Stopwatch]::StartNew()
foreach ($id in $old) { Stop-Process -Id $id -Force; Wait-Process -Id $id -Timeout 10 -ErrorAction SilentlyContinue }
if ($env:ISSUN_CLEAR_PUBLICBASE -eq '1') {
  $sf = "$env:APPDATA\Issun\settings.json"
  Copy-Item $sf "$sf.before-publicbase.bak" -Force
  python -I -c "import json,sys; p=sys.argv[1]; d=json.load(open(p,encoding='utf-8')); v=d.pop('publicBase',None); json.dump(d,open(p,'w',encoding='utf-8'),indent=2); print('publicBase removed (was', len(v or ''), 'characters)')" $sf
}
Start-Process $exe -ArgumentList '--background'
$v = $null
while ($sw.Elapsed.TotalSeconds -lt 90 -and $v -notmatch "\($b\)") { [Threading.Thread]::Sleep(500); try { $v = Invoke-RestMethod http://127.0.0.1:8787/version -TimeoutSec 2 } catch {} }
"down {0:N1} s -> {1}; build $oldVer kept as {2}" -f $sw.Elapsed.TotalSeconds, $v, (Split-Path $bak -Leaf)
