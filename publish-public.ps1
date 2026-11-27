<#
.SYNOPSIS
  Rebuilds SentinelDesk and updates the public release repository (Git/ -> github.com/tenzaicbt/SentinelDesk-EDM).

.DESCRIPTION
  1. Runs build.ps1 (unless -SkipBuild)
  2. Replaces Git/releases/* with the new build, and refreshes LICENSE and the README images
     (the web console is part of the server since 1.2.2, so the old Git/web-console copy is removed)
  3. Updates the version numbers in Git/README.md
  4. Safety checks: no dev settings, databases, keys, symbols or source maps; every file under GitHub's 100 MB limit
  5. Commits in Git/ and pushes (unless -NoPush). Records the private commit it was built from in Git/.git (local only).

  Release notes, CHANGELOG.md and README feature text are written by the /publish-public skill before this runs.

.EXAMPLE
  ./publish-public.ps1 -Message "SentinelDesk 1.2.1: faster live view"
#>
param(
    [Parameter(Mandatory)] [string] $Message,
    [switch] $SkipBuild,
    [switch] $NoPush
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pub  = Join-Path $root 'publish'
$g    = Join-Path $root 'Git'

function Invoke-Git([string] $dir, [string[]] $gitArgs) {
    $ErrorActionPreference = 'Continue'
    $out = & git -C $dir @gitArgs 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "git $($gitArgs -join ' ') failed in ${dir}: $($out -join "`n")" }
    return $out
}

if (-not (Test-Path (Join-Path $g '.git'))) { throw "Public repo not found at $g" }

# The private repo should be committed first, so the public build matches a real commit.
$dirty = Invoke-Git $root @('status', '--porcelain')
if ($dirty) { Write-Warning "Private repo has uncommitted changes - the build includes them:`n$($dirty -join "`n")" }
$sourceCommit = (Invoke-Git $root @('rev-parse', 'HEAD')) | Select-Object -First 1

if (-not $SkipBuild) {
    Write-Host '>> Building release (build.ps1)' -ForegroundColor Cyan
    & (Join-Path $root 'build.ps1')
    if (-not $?) { throw 'build.ps1 failed' }
}
foreach ($p in 'server', 'agent', 'SentinelDesk-Setup.exe') {
    if (-not (Test-Path (Join-Path $pub $p))) { throw "Missing build output publish/$p - run without -SkipBuild" }
}

$version = (Get-Item (Join-Path $pub 'agent/SentinelDesk.Agent.dll')).VersionInfo.ProductVersion -replace '\+.*$', ''
$agentZip = Join-Path $pub "SentinelDesk-Agent-$version.zip"
if (-not (Test-Path $agentZip)) { throw "Missing $agentZip" }
Write-Host ">> Packaging version $version" -ForegroundColor Cyan

# releases/
$rel = Join-Path $g 'releases'
Remove-Item -Recurse -Force $rel -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $rel | Out-Null
$stage = Join-Path ([IO.Path]::GetTempPath()) "sd-server-stage-$PID"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
Copy-Item (Join-Path $pub 'server') $stage -Recurse
Remove-Item (Join-Path $stage 'appsettings.Development.json') -ErrorAction SilentlyContinue   # holds the dev enroll token
# the setup file is published next to the server zip (releases/SentinelDesk-Setup.exe); a second copy inside the zip
# would push it over GitHub's 100 MB file limit. The README tells admins to copy it into the server's installer folder.
Remove-Item -Recurse -Force (Join-Path $stage 'installer') -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $rel "SentinelDesk-Server-$version-win-x64.zip") -Force
Remove-Item -Recurse -Force $stage
Copy-Item (Join-Path $pub 'SentinelDesk-Setup.exe'), $agentZip $rel

# The web console runs inside the server (Blazor Server) and cannot be hosted on its own: drop the old static copy
Remove-Item -Recurse -Force (Join-Path $g 'web-console') -ErrorAction SilentlyContinue

# LICENSE and README images
Copy-Item (Join-Path $root 'LICENSE') $g -Force
$img = Join-Path $g 'docs/images'
New-Item -ItemType Directory -Force $img | Out-Null
Copy-Item (Join-Path $root 'doc/logo/SentinelDesk-logo.png'), (Join-Path $root 'doc/logo/SentinelDesk-logo-dark.png') $img -Force
Copy-Item (Join-Path $root 'doc/screenshots/*.png') $img -Force

# README version numbers
$readme = Join-Path $g 'README.md'
$text = [IO.File]::ReadAllText($readme)
$text = $text -replace 'SentinelDesk-Server-[\d.]+-win-x64\.zip', "SentinelDesk-Server-$version-win-x64.zip"
$text = $text -replace 'SentinelDesk-Agent-(\d[\d.]*)\.zip', "SentinelDesk-Agent-$version.zip"
$text = $text -replace '\*\*Version [\d.]+\*\*', "**Version $version**"
[IO.File]::WriteAllText($readme, $text, (New-Object Text.UTF8Encoding $false))

# Safety checks - never publish developer files
$bad = Get-ChildItem $g -Recurse -File -Force | Where-Object {
    $_.FullName -notlike "$g\.git\*" -and (
        $_.Name -like 'appsettings.Development*.json' -or $_.Extension -in '.db', '.pdb', '.map', '.pfx', '.pem', '.key', '.cs', '.razor', '.tsx', '.ts', '.csproj', '.sln' -or
        $_.Name -in '.env', 'build.ps1', 'dev.ps1') }
if ($bad) { throw "Refusing to publish developer files:`n$($bad.FullName -join "`n")" }
foreach ($zip in Get-ChildItem $rel -Filter *.zip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
    try {
        $hits = $z.Entries | Where-Object { $_.Name -like 'appsettings.Development*' -or $_.FullName -match '(^|/)data/' -or $_.Name -match '\.(db|pdb|map)$' }
        if ($hits) { throw "Refusing to publish: $($zip.Name) contains $($hits.FullName -join ', ')" }
    } finally { $z.Dispose() }
}
$big = Get-ChildItem $g -Recurse -File | Where-Object { $_.FullName -notlike "$g\.git\*" -and $_.Length -ge 100MB }
if ($big) { throw "Over GitHub's 100 MB file limit: $($big.Name -join ', ')" }

# Commit and push
Invoke-Git $g @('add', '-A') | Out-Null
$staged = Invoke-Git $g @('status', '--porcelain')
if (-not $staged) { Write-Host 'Public repo already up to date - nothing to commit.' -ForegroundColor Yellow; return }
$msgFile = Join-Path ([IO.Path]::GetTempPath()) "sd-public-msg-$PID.txt"
[IO.File]::WriteAllText($msgFile, "$Message`n", (New-Object Text.UTF8Encoding $false))
try { Invoke-Git $g @('commit', '-q', '-F', $msgFile) | Out-Null } finally { Remove-Item $msgFile -ErrorAction SilentlyContinue }
Set-Content -Path (Join-Path $g '.git/sentineldesk-source-commit') -Value $sourceCommit -Encoding ascii
Invoke-Git $g @('log', '--stat', '--format=%h %s', '-1') | Write-Host

if (-not $NoPush) {
    Write-Host '>> Pushing to the public repo' -ForegroundColor Cyan
    Invoke-Git $g @('-c', 'http.postBuffer=524288000', 'push', 'origin', 'main') | Out-Null
    Write-Host "OK Published SentinelDesk $version to https://github.com/tenzaicbt/SentinelDesk-EDM" -ForegroundColor Green
} else {
    Write-Host "OK Committed locally (not pushed). Push with: git -C Git push origin main" -ForegroundColor Green
}
