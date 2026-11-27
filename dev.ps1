<#
.SYNOPSIS
  Runs SentinelDesk locally: server + web console (http://localhost:5080), optionally the agent and tray on this PC.

.DESCRIPTION
  Everything is built into .run\ and started from there - NOT from the projects' bin\ folders - so you can keep
  building/debugging in VS Code or Visual Studio while the local server runs, without "file is locked" errors.
  Local data (database, server keys) lives in .run\data and survives restarts.

.EXAMPLE
  .\dev.ps1              # server + console (demo fleet)
  .\dev.ps1 -Agent       # also run the agent + tray on this PC (auto-enrolls into the local server)
  .\dev.ps1 -Stop        # stop everything started by dev.ps1
#>
param([switch] $Agent, [switch] $Stop)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$run = Join-Path $root '.run'
$serverSrc = Join-Path $root 'src\SentinelDesk.Server'
$dataDir = Join-Path $run 'data'

# Stops SentinelDesk processes started from THIS repo (dev builds or .run copies) so their files are not locked.
function Stop-DevInstances([string[]] $Names = @('SentinelDesk.Server', 'SentinelDesk.Agent', 'SentinelDesk.Tray', 'SentinelDesk.Guard')) {
    $procs = Get-Process $Names -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) }
    foreach ($p in $procs) { Write-Host "   stopping $($p.ProcessName) (pid $($p.Id))" -ForegroundColor DarkGray; Stop-Process -Id $p.Id -Force }
    if ($procs) { Start-Sleep -Seconds 2 }
}

# Health probe without proxy auto-detection (Invoke-WebRequest's WPAD lookup can exceed short timeouts on corporate networks)
function Test-Server {
    try {
        $req = [Net.HttpWebRequest]::Create('http://localhost:5080/healthz')
        $req.Proxy = $null; $req.Timeout = 3000
        $res = $req.GetResponse(); $ok = [int]$res.StatusCode -eq 200; $res.Close(); return $ok
    } catch { return $false }
}

function Invoke-ProjectBuild($project, $outDir) {
    Write-Host "   building $project" -ForegroundColor DarkGray
    $log = dotnet build (Join-Path $root "src\$project") -c Debug -o $outDir --nologo -v q 2>&1
    if ($LASTEXITCODE) { $log | Select-String 'error' | Select-Object -First 10 | ForEach-Object { Write-Host $_ -ForegroundColor Red }; throw "$project build failed" }
}

Write-Host '>> Stopping previous local instances...' -ForegroundColor Cyan
if ($Stop -or $Agent) { Stop-DevInstances } else { Stop-DevInstances @('SentinelDesk.Server') }  # leave a manually started agent alone
if ($Stop) { Write-Host 'Stopped.' -ForegroundColor Green; return }

# Starts an exe in its own console window; env vars are inherited from this session.
function Start-InWindow($exe, $workDir, [hashtable] $envVars) {
    $saved = @{}
    foreach ($k in $envVars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $envVars[$k]) }
    try { return Start-Process -FilePath $exe -WorkingDirectory $workDir -WindowStyle Normal -PassThru }
    finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK not found - install .NET 10 SDK' }

# 1. Local data: keep the database & keys from earlier runs of `dotnet run` (bin\Debug\...\data) the first time
if (-not (Test-Path (Join-Path $dataDir 'sentineldesk.db'))) {
    $legacy = Join-Path $serverSrc 'bin\Debug\net10.0\data'
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    if (Test-Path (Join-Path $legacy 'sentineldesk.db')) {
        Write-Host ">> Moving existing local data into .run\data" -ForegroundColor Cyan
        Copy-Item -Path (Join-Path $legacy '*') -Destination $dataDir -Recurse -Force
    }
}

# 2. Server
$running = Test-Server
if ($running) {
    Write-Host '>> Something else is already serving http://localhost:5080 (e.g. an installed/published server) - using it' -ForegroundColor Yellow
} else {
    Write-Host '>> Building server...' -ForegroundColor Cyan
    Invoke-ProjectBuild 'SentinelDesk.Server' (Join-Path $run 'server')
    Write-Host '>> Starting server (new window)...' -ForegroundColor Cyan
    $exe = Join-Path $run 'server\SentinelDesk.Server.exe'
    # working dir = source folder, so wwwroot and appsettings*.json are read from the repo
    $proc = Start-InWindow $exe $serverSrc @{ ASPNETCORE_ENVIRONMENT = 'Development'; Storage__DataDir = $dataDir }
    Write-Host '   waiting for http://localhost:5080 ' -NoNewline
    $ok = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep 2
        if (Test-Server) { $ok = $true; break } else { Write-Host '.' -NoNewline }
        if ($proc.HasExited) { Write-Host ''; throw "Server exited with code $($proc.ExitCode). Run '$exe' from '$serverSrc' to see the error." }
    }
    Write-Host ''
    if (-not $ok) { throw 'Server did not start - check the server console window for errors (port 5080/5443 in use?)' }
}

# 3. Optional: agent + tray on this PC
if ($Agent) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { Write-Host '   note: not elevated - per-app bandwidth, DNS/websites and USB control need an Administrator terminal' -ForegroundColor Yellow }
    Write-Host '>> Building agent + tray...' -ForegroundColor Cyan
    Invoke-ProjectBuild 'SentinelDesk.Agent' (Join-Path $run 'agent')
    Invoke-ProjectBuild 'SentinelDesk.Tray' (Join-Path $run 'tray')
    Write-Host '>> Starting agent + tray (new windows)...' -ForegroundColor Cyan
    Start-InWindow (Join-Path $run 'agent\SentinelDesk.Agent.exe') (Join-Path $run 'agent') @{
        DOTNET_ENVIRONMENT = 'Development'; SENTINELDESK_SERVER = 'https://localhost:5443'; SENTINELDESK_TOKEN = 'SD-DEV-LOCAL-ENROLL' }
    Start-Process (Join-Path $run 'tray\SentinelDesk.Tray.exe')
}

Write-Host "`nSentinelDesk console: http://localhost:5080" -ForegroundColor Green
Write-Host "You can now build in your IDE freely; run .\dev.ps1 again to pick up code changes, or .\dev.ps1 -Stop." -ForegroundColor DarkGray
Start-Process 'http://localhost:5080'
