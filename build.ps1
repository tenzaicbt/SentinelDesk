<#
.SYNOPSIS
  Builds a release of SentinelDesk:
    publish/server  - self-contained web server (API + Blazor console), runnable as a Windows service
    publish/agent   - self-contained agent bundle (Agent service, Guard watchdog, Tray app, install scripts)
    publish/SentinelDesk-Agent-<version>.zip - package for staged rollout via the console
#>
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish'

# Stops SentinelDesk processes started from THIS repo (dev builds) so their DLLs are not locked.
function Stop-DevInstances {
    $procs = Get-Process SentinelDesk.Server, SentinelDesk.Agent, SentinelDesk.Tray, SentinelDesk.Guard -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) }
    foreach ($p in $procs) { Write-Host "   stopping $($p.ProcessName) (pid $($p.Id))" -ForegroundColor DarkGray; Stop-Process -Id $p.Id -Force }
    if ($procs) { Start-Sleep -Seconds 2 }
}
Stop-DevInstances

Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
$common = @('-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '-p:DebugType=none', '-p:GenerateDocumentationFile=false')

Write-Host '>> Publishing server' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/SentinelDesk.Server') @common -o (Join-Path $out 'server')
if ($LASTEXITCODE) { throw 'server publish failed' }

Write-Host '>> Publishing agent bundle (Agent + Guard + Tray)' -ForegroundColor Cyan
foreach ($p in 'SentinelDesk.Agent', 'SentinelDesk.Guard', 'SentinelDesk.Tray') {
    dotnet publish (Join-Path $root "src/$p") @common -o (Join-Path $out 'agent')
    if ($LASTEXITCODE) { throw "$p publish failed" }
}
Copy-Item (Join-Path $root 'deploy/agent/*.ps1') (Join-Path $out 'agent')
Remove-Item (Join-Path $out 'agent/appsettings*.json') -ErrorAction SilentlyContinue

$version = (Get-Item (Join-Path $out 'agent/SentinelDesk.Agent.dll')).VersionInfo.ProductVersion -replace '\+.*$', ''

# FluentSetup (src/SentinelDesk.Setup, .NET Framework 4.8 - built into Windows 10/11, no runtime needed).
# 1) without a package: the uninstaller that install-agent.ps1 copies into the install folder (Apps & features -> Uninstall)
$setupProj = Join-Path $root 'src/SentinelDesk.Setup'
$setupTmp = Join-Path $out 'setup-build'
Write-Host '>> Building FluentSetup uninstaller (agent\Uninstall.exe)' -ForegroundColor Cyan
dotnet build $setupProj -c $Configuration -o (Join-Path $setupTmp 'plain') --no-incremental '-p:AgentZip=' -p:DebugType=none --nologo -v q
if ($LASTEXITCODE) { throw 'FluentSetup (uninstaller) build failed' }
Copy-Item (Join-Path $setupTmp 'plain/SentinelDesk-Setup.exe') (Join-Path $out 'agent/Uninstall.exe')

$zip = Join-Path $out "SentinelDesk-Agent-$version.zip"
Compress-Archive -Path (Join-Path $out 'agent/*') -DestinationPath $zip -Force

# 2) with the agent package embedded: the one-file installer / upgrader
Write-Host '>> Building FluentSetup installer (SentinelDesk-Setup.exe)' -ForegroundColor Cyan
dotnet build $setupProj -c $Configuration -o (Join-Path $setupTmp 'full') --no-incremental "-p:AgentZip=$zip" -p:DebugType=none --nologo -v q
if ($LASTEXITCODE) { throw 'FluentSetup (installer) build failed' }
$setup = Join-Path $out 'SentinelDesk-Setup.exe'
Copy-Item (Join-Path $setupTmp 'full/SentinelDesk-Setup.exe') $setup -Force
Remove-Item -Recurse -Force $setupTmp
# the server serves configured copies of it from Console -> Agent management -> Office installer, and signs the
# package inside each copy (read from the file itself) so office installers can also upgrade agents
New-Item -ItemType Directory -Force (Join-Path $out 'server/installer') | Out-Null
Copy-Item $setup (Join-Path $out 'server/installer') -Force

Write-Host "`nOK Done" -ForegroundColor Green
Write-Host "  Server : $out\server\SentinelDesk.Server.exe"
Write-Host "  Agent  : $out\agent  (run install-agent.ps1 as administrator)"
Write-Host "  Setup  : $setup  (download a configured copy per branch in Console -> Agent management)"
Write-Host "  Update : $zip  (upload in Console -> Agent management -> Releases)"
