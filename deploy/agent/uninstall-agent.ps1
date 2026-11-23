<#
.SYNOPSIS
  Removes the SentinelDesk agent. Requires BOTH administrator rights AND a valid Uninstall Key
  (the organisation master key, or a one-time code generated per device in the web console).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File "C:\Program Files\SentinelDesk\uninstall-agent.ps1"
  # prompts for the Uninstall Key

.NOTES
  The services, their registry keys and the data folder are locked to SYSTEM. The key is therefore checked by a one-shot
  SYSTEM task (administrators cannot read the stored hash), which also removes the agent when the key is valid.
  Wrong keys are reported to the console as "tamper.uninstallAttempt"; after 5 wrong keys uninstalling is locked for
  30 minutes ("tamper.uninstallLockout").
#>
[CmdletBinding()]
param(
    [string] $UninstallKey,
    [switch] $RemoteAuthorized,   # used by the agent itself after a signed "uninstall" command from the console
    [switch] $SystemPhase   # internal: run as SYSTEM from the private work folder (key.txt / user.txt in, result.txt out)
)

$ErrorActionPreference = 'Stop'
$InstallDir = Join-Path $env:ProgramFiles 'SentinelDesk'
$DataDir    = Join-Path $env:ProgramData 'SentinelDesk'
$AgentExe   = Join-Path $InstallDir 'SentinelDesk.Agent.exe'
$Marker     = Join-Path $DataDir 'uninstall.authorized'
$TaskName   = 'SentinelDesk-Uninstall'
$identity   = [Security.Principal.WindowsIdentity]::GetCurrent()

# Removes every application and website block the agent applied (policy "Blocking"), so nothing stays blocked
# after uninstall. Runs after the services are stopped (so the agent cannot re-apply) and before the data folder is deleted.
function Remove-SentinelDeskBlocks {
    # applications: Image File Execution Options entries marked as ours, in both registry views
    foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options',
                      'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options') {
        Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if ($null -ne $props -and $null -ne $props.SentinelDeskBlock) {
                Remove-ItemProperty $_.PSPath -Name Debugger, SentinelDeskBlock -ErrorAction SilentlyContinue
                $k = Get-Item $_.PSPath -ErrorAction SilentlyContinue
                if ($k -and $k.ValueCount -eq 0 -and $k.SubKeyCount -eq 0) { Remove-Item $_.PSPath -Force -ErrorAction SilentlyContinue }
            }
        }
    }
    # websites: browser URL block lists - remove only the entries the agent wrote, keep any others and renumber
    $stateFile = Join-Path $DataDir 'block-state.json'
    if (Test-Path $stateFile) {
        try {
            $st = Get-Content -Raw $stateFile | ConvertFrom-Json
            foreach ($p in $st.browser.PSObject.Properties) {
                $path = "HKLM:\$($p.Name)"
                if (-not (Test-Path $path)) { continue }
                $ours = @($p.Value)
                $item = Get-Item $path
                $keep = @($item.GetValueNames() | Sort-Object { [int]($_ -as [int]) } | ForEach-Object { $item.GetValue($_) } | Where-Object { $_ -and $ours -notcontains $_ })
                if ($keep.Count -eq 0) { Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue; continue }
                foreach ($n in $item.GetValueNames()) { Remove-ItemProperty $path -Name $n -ErrorAction SilentlyContinue }
                for ($i = 0; $i -lt $keep.Count; $i++) { New-ItemProperty $path -Name ($i + 1) -Value $keep[$i] -PropertyType String -Force | Out-Null }
            }
            # browsers' secure DNS: restore the values that were there before the agent turned it off
            foreach ($p in $st.dnsPrevious.PSObject.Properties) {
                $path, $name = $p.Name -split '\|', 2
                $reg = "HKLM:\$path"
                if (-not (Test-Path $reg)) { continue }
                if ($null -eq $p.Value) { Remove-ItemProperty $reg -Name $name -ErrorAction SilentlyContinue }
                elseif ($name -in 'Enabled', 'Locked') { Set-ItemProperty $reg -Name $name -Value ([int]$p.Value) -Type DWord }
                else { Set-ItemProperty $reg -Name $name -Value $p.Value -Type String }
                $k = Get-Item $reg -ErrorAction SilentlyContinue
                if ($path -like '*DNSOverHTTPS' -and $k -and $k.ValueCount -eq 0 -and $k.SubKeyCount -eq 0) { Remove-Item $reg -Force -ErrorAction SilentlyContinue }
            }
        } catch { Write-Warning "Could not clean the browser block lists: $_" }
    }
    # websites: the managed section of the hosts file
    $hosts = Join-Path $env:windir 'System32\drivers\etc\hosts'
    if (Test-Path $hosts) {
        $lines = @(Get-Content $hosts)
        $start = -1; $end = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($start -lt 0 -and $lines[$i] -like '# BEGIN SentinelDesk block list*') { $start = $i }
            elseif ($start -ge 0 -and $lines[$i].TrimEnd() -eq '# END SentinelDesk block list') { $end = $i; break }
        }
        if ($start -ge 0) {
            if ($end -lt 0) { $end = $lines.Count - 1 }
            $rest = for ($i = 0; $i -lt $lines.Count; $i++) { if ($i -lt $start -or $i -gt $end) { $lines[$i] } }
            Set-Content -Path $hosts -Value @($rest) -Encoding ascii
            & { $ErrorActionPreference = 'Continue'; ipconfig.exe /flushdns 2>&1 | Out-Null }
        }
    }
}

function Remove-SentinelDesk {
    Set-Content -Path $Marker -Value "authorized $(Get-Date -Format o)" -Force
    foreach ($svc in 'SentinelDeskGuard', 'SentinelDeskAgent') {
        if (Get-Service $svc -ErrorAction SilentlyContinue) {
            sc.exe failure $svc reset= 0 actions= '""' | Out-Null
            Stop-Service $svc -Force -ErrorAction SilentlyContinue
            sc.exe delete $svc | Out-Null
        }
    }
    Get-Process SentinelDesk.Tray, SentinelDesk.Agent, SentinelDesk.Guard -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-SentinelDeskBlocks
    foreach ($mode in 'Minimal', 'Network') {
        foreach ($svc in 'SentinelDeskAgent', 'SentinelDeskGuard') {
            Remove-Item -Path "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$svc" -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'SentinelDesk' -ErrorAction SilentlyContinue
    Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SentinelDesk' -Recurse -Force -ErrorAction SilentlyContinue   # Apps & features entry (NSIS)
    Start-Sleep -Seconds 2
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue
    & { $ErrorActionPreference = 'Continue'; schtasks.exe /Delete /TN $TaskName /F 2>&1 | Out-Null }
}

# -- Phase 2: running as SYSTEM - checks the key (with lockout) and removes the agent when it is valid
if ($SystemPhase) {
    $KeyFile    = Join-Path $PSScriptRoot 'key.txt'
    $ResultFile = Join-Path $PSScriptRoot 'result.txt'
    $UserFile   = Join-Path $PSScriptRoot 'user.txt'
    $key = (Get-Content -Raw $KeyFile).Trim(); Remove-Item -Force $KeyFile
    $verifyArgs = @('verify-uninstall-key', $key)
    if (Test-Path $UserFile) { $verifyArgs += @('--user', (Get-Content -Raw $UserFile).Trim()) }
    & { $ErrorActionPreference = 'Continue'; & $AgentExe @verifyArgs 2>&1 | Out-Null }
    $code = $LASTEXITCODE
    $verdict = switch ($code) { 0 { 'valid' } 3 { 'locked' } default { 'invalid' } }
    Set-Content -Path $ResultFile -Value $verdict -NoNewline
    if ($code -ne 0) { exit 1 }
    Remove-SentinelDesk
    exit 0
}

# -- Remote (console-initiated, signed command verified by the agent)
if ($RemoteAuthorized) {
    if (-not $identity.IsSystem -or -not (Test-Path $Marker)) { throw 'Remote uninstall must be initiated by the agent.' }
    Remove-SentinelDesk
    exit 0
}

# -- Phase 1: interactive admin
$principal = [Security.Principal.WindowsPrincipal]$identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator rights are required to uninstall SentinelDesk.'
}
if (-not $UninstallKey -and $env:SD_UNINSTALL_KEY) {
    # set by Uninstall.exe (NSIS) so the key never appears on a command line
    $UninstallKey = $env:SD_UNINSTALL_KEY
    Remove-Item Env:\SD_UNINSTALL_KEY -ErrorAction SilentlyContinue
}
if (-not $UninstallKey) {
    $secure = Read-Host -AsSecureString 'Enter the SentinelDesk Uninstall Key (or one-time code from the console)'
    $UninstallKey = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}
if (-not (Test-Path $AgentExe)) { throw "SentinelDesk is not installed in $InstallDir." }

# Hand the key to the SYSTEM phase through a private folder (random name, SYSTEM + Administrators only), so a standard
# user cannot plant a script or read the key in the shared Windows\Temp.
$work = Join-Path $env:windir "Temp\sd-uninstall-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
icacls $work /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /Q | Out-Null
$tmp    = Join-Path $work 'key.txt'
$result = Join-Path $work 'result.txt'
$self   = Join-Path $work 'uninstall.ps1'
Set-Content -Path $tmp -Value $UninstallKey -NoNewline
Copy-Item $PSCommandPath $self -Force
Set-Content -Path (Join-Path $work 'user.txt') -Value $identity.Name -NoNewline

$taskCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$self`" -SystemPhase"   # /TR is limited to 261 characters
& {
    $ErrorActionPreference = 'Continue'   # schtasks warns on stderr about the past start time: not an error
    schtasks.exe /Create /TN $TaskName /RU SYSTEM /SC ONCE /ST 23:59 /RL HIGHEST /F /TR $taskCmd 2>&1 | Out-Null
    schtasks.exe /Run /TN $TaskName 2>&1 | Out-Null
}

Write-Host 'Checking the Uninstall Key...' -NoNewline
for ($i = 0; $i -lt 90 -and -not (Test-Path $result); $i++) { Start-Sleep 1 }
$verdict = if (Test-Path $result) { (Get-Content -Raw $result).Trim() } else { 'timeout' }
if ($verdict -ne 'valid') {
    & { $ErrorActionPreference = 'Continue'; schtasks.exe /Delete /TN $TaskName /F 2>&1 | Out-Null }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    switch ($verdict) {
        'locked'  { Write-Host ' locked.' -ForegroundColor Red; Write-Host 'Too many wrong keys. Uninstalling is locked for 30 minutes. This has been reported.' -ForegroundColor Red }
        'timeout' { Write-Host ' failed.' -ForegroundColor Red; Write-Host 'The key could not be checked (the SYSTEM task did not run).' -ForegroundColor Red }
        default   { Write-Host ' invalid.' -ForegroundColor Red; Write-Host 'Invalid Uninstall Key. This attempt has been logged and reported.' -ForegroundColor Red }
    }
    exit 1
}
Write-Host ' ok.'

Write-Host 'Removing SentinelDesk...' -NoNewline
for ($i = 0; $i -lt 60 -and (Get-Service SentinelDeskAgent -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1; Write-Host '.' -NoNewline }
& { $ErrorActionPreference = 'Continue'; schtasks.exe /Delete /TN $TaskName /F 2>&1 | Out-Null }
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
if (Get-Service SentinelDeskAgent -ErrorAction SilentlyContinue) { Write-Host ' failed.' -ForegroundColor Red; exit 1 }
Write-Host ' done.' -ForegroundColor Green
