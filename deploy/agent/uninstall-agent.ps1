<#
.SYNOPSIS
  Removes the SentinelDesk agent. Requires BOTH administrator rights AND a valid Uninstall Key
  (the organisation master key, or a one-time code generated per device in the web console).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File "C:\Program Files\SentinelDesk\uninstall-agent.ps1"
  # prompts for the Uninstall Key

.NOTES
  The services are ACL'd so that only SYSTEM can stop/delete them. After the key is verified,
  the privileged removal phase runs as SYSTEM through a one-shot scheduled task.
  Failed attempts are reported to the console as "tamper.uninstallAttempt".
#>
[CmdletBinding()]
param(
    [string] $UninstallKey,
    [switch] $RemoteAuthorized,   # used by the agent itself after a signed "uninstall" command from the console
    [switch] $SystemPhase,
    [string] $KeyFile
)

$ErrorActionPreference = 'Stop'
$InstallDir = Join-Path $env:ProgramFiles 'SentinelDesk'
$DataDir    = Join-Path $env:ProgramData 'SentinelDesk'
$AgentExe   = Join-Path $InstallDir 'SentinelDesk.Agent.exe'
$Marker     = Join-Path $DataDir 'uninstall.authorized'
$TaskName   = 'SentinelDesk-Uninstall'
$identity   = [Security.Principal.WindowsIdentity]::GetCurrent()

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
    Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'SentinelDesk' -ErrorAction SilentlyContinue
    Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SentinelDesk' -Recurse -Force -ErrorAction SilentlyContinue   # Apps & features entry (NSIS)
    Start-Sleep -Seconds 2
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue
    & { $ErrorActionPreference = 'Continue'; schtasks.exe /Delete /TN $TaskName /F 2>&1 | Out-Null }
}

# -- Phase 2: running as SYSTEM
if ($SystemPhase) {
    $key = Get-Content -Raw $KeyFile; Remove-Item -Force $KeyFile
    & $AgentExe verify-uninstall-key $key.Trim() | Out-Null
    if ($LASTEXITCODE -ne 0) { exit 1 }
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
& $AgentExe verify-uninstall-key $UninstallKey | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Invalid Uninstall Key. This attempt has been reported to IT.' -ForegroundColor Red
    exit 1
}

# Hand the key to the SYSTEM phase through an ACL-protected temp file
$tmp = Join-Path $env:windir "Temp\sd-uninstall-$([guid]::NewGuid().ToString('N')).key"
Set-Content -Path $tmp -Value $UninstallKey -NoNewline
icacls $tmp /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' /Q | Out-Null
$self = Join-Path $env:windir "Temp\sd-uninstall.ps1"
Copy-Item $PSCommandPath $self -Force

schtasks.exe /Create /TN $TaskName /RU SYSTEM /SC ONCE /ST 23:59 /RL HIGHEST /F `
    /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$self`" -SystemPhase -KeyFile `"$tmp`"" | Out-Null
schtasks.exe /Run /TN $TaskName | Out-Null

Write-Host 'Removing SentinelDesk...' -NoNewline
for ($i = 0; $i -lt 60 -and (Get-Service SentinelDeskAgent -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1; Write-Host '.' -NoNewline }
& { $ErrorActionPreference = 'Continue'; schtasks.exe /Delete /TN $TaskName /F 2>&1 | Out-Null }
Remove-Item $self -Force -ErrorAction SilentlyContinue
if (Get-Service SentinelDeskAgent -ErrorAction SilentlyContinue) { Write-Host ' failed.' -ForegroundColor Red; exit 1 }
Write-Host ' done.' -ForegroundColor Green
