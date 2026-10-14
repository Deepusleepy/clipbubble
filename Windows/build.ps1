# Builds ClipBubble.exe, installs it to %LOCALAPPDATA%\Programs\ClipBubble and relaunches it.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build ClipBubble.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-Process ClipBubble -ErrorAction SilentlyContinue | Stop-Process -PassThru | Wait-Process
$dest = Join-Path $env:LOCALAPPDATA 'Programs\ClipBubble'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item bin\Release\net48\ClipBubble.exe $dest -Force
Start-Process (Join-Path $dest 'ClipBubble.exe')
