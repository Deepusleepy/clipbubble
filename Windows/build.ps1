# Builds ClipBubble.exe, installs it to %LOCALAPPDATA%\Programs\ClipBubble and relaunches it.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build ClipBubble.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# Quit the running app, but not `ClipBubble.exe --mcp` servers that agents are using.
Get-CimInstance Win32_Process -Filter "Name = 'ClipBubble.exe'" | Where-Object { $_.CommandLine -notmatch '--mcp' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue; Wait-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
$dest = Join-Path $env:LOCALAPPDATA 'Programs\ClipBubble'
$exe = Join-Path $dest 'ClipBubble.exe'
New-Item -ItemType Directory -Force $dest | Out-Null
# Those servers keep the old exe open. Windows can't overwrite it but can rename it, so it moves aside; old copies
# go once no server uses them.
Get-ChildItem $dest -Filter 'ClipBubble.*.old' | Remove-Item -ErrorAction SilentlyContinue
if (Test-Path $exe) { Move-Item $exe (Join-Path $dest "ClipBubble.$(Get-Date -Format yyyyMMddHHmmss).old") }
Copy-Item bin\Release\net48\ClipBubble.exe $dest
Start-Process $exe
