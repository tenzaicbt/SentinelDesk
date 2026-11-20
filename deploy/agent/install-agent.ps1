<#
.SYNOPSIS
  Installs the SentinelDesk endpoint agent (Agent service + Guard watchdog + Tray) on this PC.

.DESCRIPTION
  Must run elevated (UAC / admin credentials). Normally launched by SentinelDesk-Setup.exe (NSIS, deploy/setup); can also be used
  directly from GPO startup scripts, Intune (Win32 app) or SCCM:

    powershell -ExecutionPolicy Bypass -File install-agent.ps1 `
        -ServerUrl https://monitor.company.lk:5443 -EnrollToken SD-XXXXXXXX -UninstallKey "<secret>"

  What it does:
    1. Cleans up leftovers of an earlier, incomplete install
    2. Copies binaries to %ProgramFiles%\SentinelDesk
    3. Enrolls the device (TPM-backed key + CSR -> device certificate)
    4. Stores the PBKDF2 hash of the Uninstall Key
    5. Registers both services as LocalSystem, auto-start, restart-on-failure
    6. Locks down folder ACLs, starts the services, hardens the service DACLs (only SYSTEM can stop/delete)
    7. Registers the tray app for every user session
  Any failure rolls the install back. Full log: C:\Windows\Temp\SentinelDesk-Install.log
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ServerUrl,
    [Parameter(Mandatory)] [string] $EnrollToken,
    [Parameter(Mandatory)] [string] $UninstallKey,
    [string] $ServerCertSha256,
    [switch] $TrustOnFirstUse,
    [string] $SourceDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$InstallDir = Join-Path $env:ProgramFiles 'SentinelDesk'
$DataDir    = Join-Path $env:ProgramData 'SentinelDesk'
$AgentExe   = Join-Path $InstallDir 'SentinelDesk.Agent.exe'
$GuardExe   = Join-Path $InstallDir 'SentinelDesk.Guard.exe'
$TrayExe    = Join-Path $InstallDir 'SentinelDesk.Tray.exe'
$LogFile    = Join-Path $env:windir 'Temp\SentinelDesk-Install.log'
$Services   = 'SentinelDeskAgent', 'SentinelDeskGuard'

function Write-Step($msg) { Write-Host "  > $msg" }

# Runs a native command and checks its exit code. In Windows PowerShell 5.1, "2>&1" turns every stderr line into an
# error record, which throws under $ErrorActionPreference = 'Stop' even when the command succeeds - so relax it here.
function Invoke-Native([string] $what, [scriptblock] $cmd) {
    $ErrorActionPreference = 'Continue'
    $out = & $cmd 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE): $($out -join ' ')" }
    return $out
}

# Same, for best-effort calls whose result does not matter.
function Invoke-Quiet([scriptblock] $cmd) {
    $ErrorActionPreference = 'Continue'
    & $cmd 2>&1 | Out-Null
}

# Takes back control of a folder left locked (SYSTEM-only, or with broken ACLs) by an earlier install, then deletes it.
function Remove-LockedFolder($path) {
    if (-not (Test-Path $path)) { return }
    Invoke-Quiet { takeown.exe /F $path /R /A /D Y }          # uses the take-ownership privilege: works whatever the ACL says
    Invoke-Quiet { icacls.exe $path /reset /T /C /Q }         # back to inherited permissions (Administrators: full control)
    Invoke-Quiet { icacls.exe $path /grant '*S-1-5-32-544:(OI)(CI)F' /C /Q }
    Remove-Item -Recurse -Force $path -ErrorAction SilentlyContinue
    if (Test-Path $path) { throw "Could not remove leftover folder $path (a file may be in use - reboot and retry)." }
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator rights are required to install SentinelDesk.'
}
if ($UninstallKey.Length -lt 8) { throw 'UninstallKey must be at least 8 characters.' }
if (-not (Test-Path (Join-Path $SourceDir 'SentinelDesk.Agent.exe'))) { throw "Agent binaries not found in $SourceDir" }
if (Get-Service SentinelDeskAgent -ErrorAction SilentlyContinue) {
    throw 'SentinelDesk is already installed. Use the console (Agent management -> Releases) to upgrade, or uninstall first with the Uninstall Key.'
}

try { Start-Transcript -Path $LogFile -Append -Force | Out-Null } catch { }
$createdServices = $false
try {
    Write-Host "Installing SentinelDesk Agent -> $InstallDir  (server: $ServerUrl)"

    # 1. leftovers of an earlier failed/partial install (no services registered, but folders/processes remain)
    if ((Test-Path $InstallDir) -or (Test-Path $DataDir)) {
        Write-Step 'Removing leftovers of a previous incomplete installation'
        Get-Process SentinelDesk.Agent, SentinelDesk.Guard, SentinelDesk.Tray -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -like "$InstallDir*" -or -not $_.Path } | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        Remove-LockedFolder $InstallDir
        Remove-LockedFolder $DataDir
    }

    # 2. files
    Write-Step 'Copying files'
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force -Exclude 'install-agent.ps1'
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    New-Item -ItemType File -Force -Path (Join-Path $DataDir 'guard-events.jsonl') | Out-Null

    # 3-4. identity
    Write-Step 'Enrolling with the management server'
    $enrollArgs = @('enroll', '--server', $ServerUrl, '--token', $EnrollToken)
    if ($ServerCertSha256) { $enrollArgs += @('--pin', $ServerCertSha256) }
    if ($TrustOnFirstUse)  { $enrollArgs += '--tofu' }
    $ErrorActionPreference = 'Continue'
    $out = & $AgentExe @enrollArgs 2>&1 | ForEach-Object { "$_" }
    $enrollExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $out | ForEach-Object { Write-Host "    $_" }
    if ($enrollExit -ne 0) {
        throw "Enrollment failed. Check that this PC can reach $ServerUrl (firewall / port 5443), that the token is valid and not used up, and TLS trust (use -TrustOnFirstUse for a self-signed certificate)."
    }
    Write-Step 'Storing uninstall key hash'
    Invoke-Native 'Storing uninstall key' { & $AgentExe set-uninstall-key $UninstallKey } | Out-Null

    # 5. services
    Write-Step 'Registering event log sources'
    foreach ($src in 'SentinelDesk Agent', 'SentinelDesk Guard') {
        if (-not [Diagnostics.EventLog]::SourceExists($src)) { New-EventLog -LogName Application -Source $src }
    }
    Write-Step 'Creating services'
    $createdServices = $true
    Invoke-Native 'Creating agent service' { sc.exe create SentinelDeskAgent binPath= "`"$AgentExe`"" start= auto obj= LocalSystem DisplayName= 'SentinelDesk Agent' } | Out-Null
    Invoke-Native 'Creating guard service' { sc.exe create SentinelDeskGuard binPath= "`"$GuardExe`"" start= auto obj= LocalSystem DisplayName= 'SentinelDesk Guard' } | Out-Null
    sc.exe description SentinelDeskAgent 'Collects device health, inventory and usage telemetry for IT management. Managed by IT.' | Out-Null
    sc.exe description SentinelDeskGuard 'Watchdog and updater for the SentinelDesk agent.' | Out-Null
    foreach ($svc in $Services) {
        sc.exe failure $svc reset= 0 actions= restart/5000/restart/5000/restart/10000 | Out-Null
        sc.exe failureflag $svc 1 | Out-Null
    }

    # 6. lock down + start
    Write-Step 'Locking down folder permissions'
    # Protected ACL on the top folder only; everything below inherits it. (Do NOT use /inheritance:r + /grant:r with /T:
    # (OI)(CI) ACEs are meaningless on files, so each file ends up with an EMPTY DACL - even SYSTEM cannot start the exe.)
    Invoke-Native 'Securing install folder' { icacls $InstallDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)RX' '*S-1-5-32-545:(OI)(CI)RX' /Q } | Out-Null
    Invoke-Native 'Securing install folder contents' { icacls (Join-Path $InstallDir '*') /reset /T /C /Q } | Out-Null
    Invoke-Native 'Securing data folder' { icacls $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)RX' /Q } | Out-Null
    Invoke-Native 'Securing data folder contents' { icacls (Join-Path $DataDir '*') /reset /T /C /Q } | Out-Null
    try { [IO.File]::OpenRead($AgentExe).Dispose() } catch { throw "Install folder permissions are broken - the agent executable cannot be read: $($_.Exception.Message)" }
    icacls (Join-Path $DataDir 'guard-events.jsonl') /grant '*S-1-5-32-544:M' /Q | Out-Null   # lets failed uninstall attempts be recorded

    Write-Step 'Starting services'
    foreach ($svc in $Services) {
        Start-Service $svc
        (Get-Service $svc).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    }
    Start-Sleep -Seconds 5
    if ((Get-Service SentinelDeskAgent).Status -ne 'Running') {
        $err = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-2) } -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error', 'SentinelDesk Agent' } | Select-Object -First 1
        throw "The agent service stopped right after starting. $($err.Message)"
    }

    Write-Step 'Hardening service permissions (only SYSTEM may stop / change / delete)'
    # SY = full control. BA (Administrators) and AU (Authenticated Users): query status/config only.
    $sddl = 'D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWLOCRRC;;;BA)(A;;CCLCSWLOCRRC;;;AU)'
    foreach ($svc in $Services) { Invoke-Native "Hardening $svc" { sc.exe sdset $svc $sddl } | Out-Null }

    # 7. tray: all future sign-ins; the agent also starts it in already signed-in sessions within ~30 s
    Write-Step 'Registering tray app for all users'
    New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'SentinelDesk' -Value "`"$TrayExe`"" -PropertyType String -Force | Out-Null

    Write-Host ''
    Write-Host 'SentinelDesk installed, enrolled and running. The tray icon appears within about 30 seconds.'
    & $AgentExe status
    exit 0
}
catch {
    Write-Host ''
    Write-Host "INSTALL FAILED: $($_.Exception.Message)"
    if ($createdServices) {
        Write-Host '  rolling back services...'
        foreach ($svc in $Services) { Invoke-Quiet { sc.exe stop $svc }; Invoke-Quiet { sc.exe delete $svc } }
    }
    Write-Host "  log: $LogFile"
    exit 1
}
finally {
    try { Stop-Transcript | Out-Null } catch { }
}
