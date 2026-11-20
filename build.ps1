<#
.SYNOPSIS
  Builds a release of SentinelDesk:
    publish/server  - self-contained web server (API + console), runnable as a Windows service
    publish/agent   - self-contained agent bundle (Agent service, Guard watchdog, Tray app, install scripts)
    publish/SentinelDesk-Agent-<version>.zip - package for staged rollout via the console
#>
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $SkipWeb
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

if (-not $SkipWeb) {
    Write-Host '>> Building web console' -ForegroundColor Cyan
    Push-Location (Join-Path $root 'web/console')
    npm ci
    npm run build
    Pop-Location
}

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
$zip = Join-Path $out "SentinelDesk-Agent-$version.zip"
Compress-Archive -Path (Join-Path $out 'agent/*') -DestinationPath $zip -Force

Write-Host '>> Building one-file office installer with NSIS (SentinelDesk-Setup.exe)' -ForegroundColor Cyan
$makensis = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source
if (-not $makensis) {
    $makensis = @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $makensis) { throw 'NSIS (makensis.exe) not found - install it with: winget install NSIS.NSIS' }
$parts = @(($version -split '[.-]') | Where-Object { $_ -match '^\d+$' } | Select-Object -First 4)
while ($parts.Count -lt 4) { $parts += '0' }
$setup = Join-Path $out 'SentinelDesk-Setup.exe'
& $makensis /V2 /INPUTCHARSET UTF8 "/DVERSION=$version" "/DFILEVERSION=$($parts -join '.')" "/DAGENTDIR=$(Join-Path $out 'agent')" "/DOUTFILE=$setup" (Join-Path $root 'deploy/setup/SentinelDesk-Setup.nsi')
if ($LASTEXITCODE) { throw 'NSIS setup build failed' }
# the server serves configured copies of it from Console -> Agent management -> Office installer
New-Item -ItemType Directory -Force (Join-Path $out 'server/installer') | Out-Null
Copy-Item $setup (Join-Path $out 'server/installer/SentinelDesk-Setup.exe') -Force

Write-Host "`nOK Done" -ForegroundColor Green
Write-Host "  Server : $out\server\SentinelDesk.Server.exe"
Write-Host "  Agent  : $out\agent  (run install-agent.ps1 as administrator)"
Write-Host "  Setup  : $setup  (download a configured copy per branch in Console -> Agent management)"
Write-Host "  Update : $zip  (upload in Console -> Agent management -> Releases)"
