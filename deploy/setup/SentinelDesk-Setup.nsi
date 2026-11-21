; ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
;  SentinelDesk Agent - NSIS installer + uninstaller  (built by build.ps1)
;
;    makensis /DVERSION=1.0.0 /DFILEVERSION=1.0.0.0 /DAGENTDIR=<publish\agent> /DOUTFILE=<SentinelDesk-Setup.exe> SentinelDesk-Setup.nsi
;
;  Install
;    SentinelDesk-Setup.exe                 guided install (UAC prompt)
;    SentinelDesk-Setup.exe /quiet  (or /S) silent install with the settings embedded by the web console (GPO / Intune / SCCM)
;    Overrides: /server URL  /token TOKEN  /key UNINSTALLKEY  /pin SHA256  /tofu      (also accepted as /server=URL ...)
;    Exit code 0 = installed (or already installed). Full log: C:\Windows\Temp\SentinelDesk-Install.log
;
;  Uninstall  (Apps & features, or "C:\Program Files\SentinelDesk\Uninstall.exe")
;    Asks for the organisation Uninstall Key or a per-device one-time code; silent: Uninstall.exe /S /key=KEY
;
;  Embedded settings: the console appends a trailer to this exe  [key=value lines (UTF-8)][length: int32 LE]["SDSETUP1"]
;  NSIS only checks the data it wrote itself, so the appended bytes do not break the installer.
; ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
Unicode true
ManifestDPIAware true
RequestExecutionLevel admin
SetCompressor /SOLID lzma

!ifndef AGENTDIR
  !error "Pass /DAGENTDIR=<path to publish\agent> (run build.ps1)"
!endif
!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef FILEVERSION
  !define FILEVERSION "1.0.0.0"
!endif
!ifndef OUTFILE
  !define OUTFILE "SentinelDesk-Setup.exe"
!endif
!ifndef ICON
  !define ICON "setup.ico"   ; makensis runs from the script's folder
!endif

!define PRODUCT  "SentinelDesk Agent"
!define REGKEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\SentinelDesk"
!define SVCKEY   "SYSTEM\CurrentControlSet\Services\SentinelDeskAgent"
!define PS       "$WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
!define LOGFILE  "$WINDIR\Temp\SentinelDesk-Install.log"

!include MUI2.nsh
!include nsDialogs.nsh
!include LogicLib.nsh
!include x64.nsh
!include FileFunc.nsh
!include WordFunc.nsh

Name "${PRODUCT}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\SentinelDesk"   ; fixed - install-agent.ps1 installs to %ProgramFiles%\SentinelDesk
BrandingText "SentinelDesk · © 2026 Yohan Koshala"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${FILEVERSION}"
VIAddVersionKey "ProductName"     "${PRODUCT}"
VIAddVersionKey "CompanyName"     "Yohan Koshala (LordeFX)"
VIAddVersionKey "FileDescription" "SentinelDesk Agent Setup"
VIAddVersionKey "FileVersion"     "${VERSION}"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "LegalCopyright"  "© 2026 Yohan Koshala. All rights reserved."
VIAddVersionKey "Comments"        "Created and developed by Yohan Koshala · www.lordefx.com · github.com/tenzaicbt"

Var Server
Var Token
Var Key
Var Pin
Var Tofu
Var Org
Var Branch
Var Preconfigured
Var Cfg
Var WelcomeText
Var InstallOk
Var hServer
Var hToken
Var hKey
Var hTofu
Var UnKey
Var hUnKey

; ── pages ────────────────────────────────────────────────────────────────────────────────────────────────────
!define MUI_ICON   "${ICON}"
!define MUI_UNICON "${ICON}"
; brand artwork (regenerate with make-installer-art.ps1): ink side panel + white header with the globe wordmark
!define MUI_WELCOMEFINISHPAGE_BITMAP   "wizard.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "wizard.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP   "header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "header.bmp"
!define MUI_TEXTCOLOR "171717"
!define MUI_BGCOLOR   "FFFFFF"
!define MUI_ABORTWARNING
!define MUI_UNABORTWARNING

!define MUI_WELCOMEPAGE_TITLE "Install SentinelDesk Agent"
!define MUI_WELCOMEPAGE_TEXT  "$WelcomeText"
!insertmacro MUI_PAGE_WELCOME
!define MUI_LICENSEPAGE_TEXT_TOP "SentinelDesk is proprietary software created and developed by Yohan Koshala."
!insertmacro MUI_PAGE_LICENSE "..\..\LICENSE"
Page custom SettingsPage SettingsLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_PAGE_CUSTOMFUNCTION_PRE FinishPre
!define MUI_FINISHPAGE_TITLE "SentinelDesk is installed"
!define MUI_FINISHPAGE_TEXT  "This computer is now protected and reporting to IT.$\r$\n$\r$\nThe tray icon appears within about 30 seconds.$\r$\n$\r$\n$\r$\nSentinelDesk · © 2026 Yohan Koshala. All rights reserved."
!define MUI_FINISHPAGE_LINK          "www.lordefx.com"
!define MUI_FINISHPAGE_LINK_LOCATION "https://www.lordefx.com"
!insertmacro MUI_PAGE_FINISH

UninstPage custom un.KeyPage un.KeyLeave
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────
; Strips a leading "=" / spaces and surrounding quotes from a command-line option value.
!macro TrimOptFn PREFIX
Function ${PREFIX}TrimOpt
  Exch $0
  Push $1
  lead:
    StrCpy $1 $0 1
    StrCmp $1 "=" +3
    StrCmp $1 " " +2
    StrCmp $1 '"' 0 trail
    StrCpy $0 $0 "" 1
    Goto lead
  trail:
    StrCpy $1 $0 1 -1
    StrCmp $1 " " +2
    StrCmp $1 '"' 0 done
    StrCpy $0 $0 -1
    Goto trail
  done:
  Pop $1
  Exch $0
FunctionEnd
!macroend
!insertmacro TrimOptFn ""
!insertmacro TrimOptFn "un."

; Reads /NAME value from the command line ($R0) into VAR (unchanged when the option is absent).
!macro GetOpt NAME VAR PREFIX
  ClearErrors
  ${GetOptions} $R0 "/${NAME}" $R1
  ${IfNot} ${Errors}
    Push $R1
    Call ${PREFIX}TrimOpt
    Pop ${VAR}
  ${EndIf}
!macroend

; Value of KEY from the embedded settings ($Cfg = "\n" + body + "\n"), or "" if missing.
!macro GetCfg KEY VAR
  ClearErrors
  ${WordFind2XS} "$Cfg" "$\n${KEY}=" "$\n" "E+1" ${VAR}
  ${If} ${Errors}
    StrCpy ${VAR} ""
  ${EndIf}
!macroend

; Removes double quotes (values are passed to PowerShell inside quotes).
!macro NoQuotes VAR
  ${WordReplace} "${VAR}" '"' "" "+" ${VAR}
!macroend

; Reads the trailer appended by the console into $Cfg.
Function ReadTrailer
  StrCpy $Cfg ""
  ClearErrors
  FileOpen $0 "$EXEPATH" r
  ${If} ${Errors}
    Return
  ${EndIf}
  FileSeek $0 -12 END
  FileReadByte $0 $1
  FileReadByte $0 $2
  FileReadByte $0 $3
  FileReadByte $0 $4
  IntOp $2 $2 << 8
  IntOp $3 $3 << 16
  IntOp $4 $4 << 24
  IntOp $1 $1 | $2
  IntOp $1 $1 | $3
  IntOp $1 $1 | $4
  FileRead $0 $5 8
  ${If} $5 S== "SDSETUP1"
  ${AndIf} $1 > 0
  ${AndIf} $1 < 4000
    IntOp $6 $1 + 12
    IntOp $6 0 - $6
    FileSeek $0 $6 END
    IntOp $7 $1 + 2
    System::Alloc $7          ; zero-filled, so the UTF-8 buffer is NUL-terminated
    Pop $7
    System::Call "kernel32::ReadFile(p r0, p r7, i r1, *i .r8, p 0) i .r9"
    System::Call "kernel32::MultiByteToWideChar(i 65001, i 0, p r7, i -1, w .r5, i ${NSIS_MAX_STRLEN}) i .r9"
    System::Free $7
    StrCpy $Cfg "$\n$5$\n"
  ${EndIf}
  FileClose $0
FunctionEnd

; ── installer ────────────────────────────────────────────────────────────────────────────────────────────────
Function .onInit
  StrCpy $InstallOk 0
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "SentinelDesk requires 64-bit Windows 10 or 11." /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
  SetRegView 64

  Call ReadTrailer
  !insertmacro GetCfg "server"           $Server
  !insertmacro GetCfg "token"            $Token
  !insertmacro GetCfg "uninstallKeyHash" $Key
  !insertmacro GetCfg "pin"              $Pin
  !insertmacro GetCfg "tofu"             $Tofu
  !insertmacro GetCfg "org"              $Org
  !insertmacro GetCfg "branch"           $Branch
  ${If} $Tofu == "true"
    StrCpy $Tofu 1
  ${EndIf}

  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/quiet" $R1
  ${IfNot} ${Errors}
    SetSilent silent
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/silent" $R1
  ${IfNot} ${Errors}
    SetSilent silent
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/tofu" $R1
  ${IfNot} ${Errors}
    StrCpy $Tofu 1
  ${EndIf}
  !insertmacro GetOpt "server" $Server ""
  !insertmacro GetOpt "token"  $Token  ""
  !insertmacro GetOpt "key"    $Key    ""
  !insertmacro GetOpt "pin"    $Pin    ""
  ${If} $Tofu != 1
    StrCpy $Tofu 0
  ${EndIf}

  ReadRegStr $R2 HKLM "${SVCKEY}" "ImagePath"
  ${If} $R2 != ""
    MessageBox MB_ICONINFORMATION|MB_OK "SentinelDesk is already installed on this computer and reporting to IT. No action is needed.$\r$\n$\r$\nTo reinstall, uninstall it first with the Uninstall Key or a one-time code from the console." /SD IDOK
    SetErrorLevel 0
    Quit
  ${EndIf}

  StrCpy $Preconfigured 0
  ${If} $Server != ""
  ${AndIf} $Token != ""
  ${AndIf} $Key != ""
    StrCpy $Preconfigured 1
  ${EndIf}

  ${If} ${Silent}
  ${AndIf} $Preconfigured != 1
    SetErrorLevel 2   ; server address, enrollment token and uninstall key are required
    Quit
  ${EndIf}

  StrCpy $WelcomeText "This installs the SentinelDesk monitoring agent on this company computer. It runs as a protected Windows service with a tray icon, and securely reports device health, inventory, application and network usage to IT."
  ${If} $Preconfigured == 1
    StrCpy $WelcomeText "$WelcomeText$\r$\n$\r$\nManagement server:  $Server"
    ${If} $Branch != ""
      StrCpy $WelcomeText "$WelcomeText$\r$\nBranch:  $Branch"
    ${ElseIf} $Org != ""
      StrCpy $WelcomeText "$WelcomeText$\r$\nOrganization:  $Org"
    ${EndIf}
  ${EndIf}
  StrCpy $WelcomeText "$WelcomeText$\r$\n$\r$\nAdministrator rights are required. After installation the agent can only be removed with the organization's Uninstall Key."
FunctionEnd

Function SettingsPage
  ${If} $Preconfigured == 1
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "Connection settings" "Enter the details from the web console (Agent management)."
  nsDialogs::Create 1018
  Pop $0
  ${If} $Server == ""
    StrCpy $Server "https://"
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 10u "Management server address"
  Pop $0
  ${NSD_CreateText} 0 11u 100% 13u "$Server"
  Pop $hServer
  ${NSD_CreateLabel} 0 32u 100% 10u "Enrollment token"
  Pop $0
  ${NSD_CreateText} 0 43u 100% 13u "$Token"
  Pop $hToken
  ${NSD_CreateLabel} 0 64u 100% 10u "Organization uninstall key (at least 8 characters)"
  Pop $0
  ${NSD_CreatePassword} 0 75u 100% 13u "$Key"
  Pop $hKey
  ${NSD_CreateCheckbox} 0 98u 100% 12u "Trust the server certificate on first connection (private / self-signed certificate)"
  Pop $hTofu
  ${If} $Tofu == 1
    ${NSD_Check} $hTofu
  ${EndIf}
  ${NSD_SetFocus} $hServer
  nsDialogs::Show
FunctionEnd

Function SettingsLeave
  ${NSD_GetText} $hServer $Server
  ${NSD_GetText} $hToken  $Token
  ${NSD_GetText} $hKey    $Key
  ${NSD_GetState} $hTofu $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $Tofu 1
  ${Else}
    StrCpy $Tofu 0
  ${EndIf}
  StrCpy $0 $Server 8
  StrCpy $1 $Server 7
  StrLen $2 $Server
  ${If} $0 != "https://"
  ${AndIf} $1 != "http://"
    MessageBox MB_ICONEXCLAMATION|MB_OK "Enter a valid server address, e.g. https://monitor.company.lk:5443"
    Abort
  ${EndIf}
  ${If} $2 < 10
    MessageBox MB_ICONEXCLAMATION|MB_OK "Enter a valid server address, e.g. https://monitor.company.lk:5443"
    Abort
  ${EndIf}
  StrLen $2 $Token
  ${If} $2 < 6
    MessageBox MB_ICONEXCLAMATION|MB_OK "Enter the enrollment token."
    Abort
  ${EndIf}
  StrLen $2 $Key
  ${If} $2 < 8
    MessageBox MB_ICONEXCLAMATION|MB_OK "The uninstall key must be at least 8 characters."
    Abort
  ${EndIf}
FunctionEnd

Section "SentinelDesk Agent" SecMain
  SectionIn RO
  SetDetailsPrint both
  DetailPrint "Extracting agent package..."
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\agent"
  File /r "${AGENTDIR}\*"
  ; the uninstaller is copied into %ProgramFiles%\SentinelDesk together with the agent files by install-agent.ps1
  WriteUninstaller "$PLUGINSDIR\agent\Uninstall.exe"

  !insertmacro NoQuotes $Server
  !insertmacro NoQuotes $Token
  !insertmacro NoQuotes $Key
  !insertmacro NoQuotes $Pin
  StrCpy $1 '"${PS}" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\agent\install-agent.ps1" -SourceDir "$PLUGINSDIR\agent" -ServerUrl "$Server" -EnrollToken "$Token" -UninstallKey "$Key"'
  ${If} $Pin != ""
    StrCpy $1 '$1 -ServerCertSha256 "$Pin"'
  ${EndIf}
  ${If} $Tofu == 1
    StrCpy $1 "$1 -TrustOnFirstUse"
  ${EndIf}

  DetailPrint "Installing and enrolling with $Server ..."
  ${DisableX64FSRedirection}   ; 64-bit PowerShell, so %ProgramFiles% is "C:\Program Files"
  nsExec::ExecToLog $1
  Pop $0
  ${EnableX64FSRedirection}
  SetOutPath "$TEMP"           ; release $PLUGINSDIR so it can be cleaned up

  ReadRegStr $2 HKLM "${SVCKEY}" "ImagePath"
  ${If} $0 != "0"
  ${OrIf} $2 == ""
    ${If} $0 == "0"
    ${OrIf} $0 == "error"
      StrCpy $0 1
    ${EndIf}
    DetailPrint "Installation failed (code $0). Log: ${LOGFILE}"
    MessageBox MB_ICONSTOP|MB_OK "SentinelDesk could not be installed (code $0).$\r$\n$\r$\nDetails are shown in the installer window and in ${LOGFILE}" /SD IDOK
    SetErrorLevel $0
    Abort
  ${EndIf}

  ; Apps & features entry
  WriteRegStr   HKLM "${REGKEY}" "DisplayName"     "${PRODUCT}"
  WriteRegStr   HKLM "${REGKEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   HKLM "${REGKEY}" "Publisher"       "Yohan Koshala (LordeFX)"
  WriteRegStr   HKLM "${REGKEY}" "URLInfoAbout"    "https://www.lordefx.com"
  WriteRegStr   HKLM "${REGKEY}" "HelpLink"        "https://github.com/tenzaicbt"
  WriteRegStr   HKLM "${REGKEY}" "Comments"        "Created and developed by Yohan Koshala"
  WriteRegStr   HKLM "${REGKEY}" "DisplayIcon"     "$INSTDIR\SentinelDesk.Tray.exe"
  WriteRegStr   HKLM "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${REGKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${REGKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $3 $4 $5
  IntFmt $3 "0x%08X" $3
  WriteRegDWORD HKLM "${REGKEY}" "EstimatedSize" $3
  StrCpy $InstallOk 1
  SetErrorLevel 0
SectionEnd

Function FinishPre
  ${If} $InstallOk != 1
    Abort
  ${EndIf}
FunctionEnd

; ── uninstaller ──────────────────────────────────────────────────────────────────────────────────────────────
Function un.onInit
  SetRegView 64
  ${GetParameters} $R0
  !insertmacro GetOpt "key" $UnKey "un."
FunctionEnd

Function un.KeyPage
  ${If} $UnKey != ""
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "Uninstall SentinelDesk Agent" "Removing the agent requires the organization's Uninstall Key."
  nsDialogs::Create 1018
  Pop $0
  ${NSD_CreateLabel} 0 0 100% 30u "Enter the SentinelDesk Uninstall Key, or a one-time code generated for this device in the web console.$\r$\n$\r$\nFailed attempts are reported to IT."
  Pop $0
  ${NSD_CreateLabel} 0 40u 100% 10u "Uninstall Key / one-time code"
  Pop $0
  ${NSD_CreatePassword} 0 51u 100% 13u ""
  Pop $hUnKey
  ${NSD_SetFocus} $hUnKey
  nsDialogs::Show
FunctionEnd

Function un.KeyLeave
  ${NSD_GetText} $hUnKey $UnKey
  ${If} $UnKey == ""
    MessageBox MB_ICONEXCLAMATION|MB_OK "Enter the Uninstall Key."
    Abort
  ${EndIf}
FunctionEnd

Section "Uninstall"
  SetDetailsPrint both
  ${If} $UnKey == ""
    DetailPrint "An Uninstall Key is required: Uninstall.exe /S /key=<key>"
    SetErrorLevel 2
    Abort
  ${EndIf}
  ${IfNot} ${FileExists} "$INSTDIR\uninstall-agent.ps1"
    MessageBox MB_ICONSTOP|MB_OK "uninstall-agent.ps1 was not found in $INSTDIR." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}

  DetailPrint "Verifying the Uninstall Key and removing SentinelDesk..."
  ; hand the key over in the environment, not on the command line (visible to other processes)
  StrCpy $0 $UnKey
  System::Call 'kernel32::SetEnvironmentVariable(t "SD_UNINSTALL_KEY", t r0)'
  ${DisableX64FSRedirection}
  nsExec::ExecToLog '"${PS}" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\uninstall-agent.ps1"'
  Pop $0
  ${EnableX64FSRedirection}
  System::Call 'kernel32::SetEnvironmentVariable(t "SD_UNINSTALL_KEY", p 0)'
  StrCpy $UnKey ""

  ${If} $0 != "0"
    ${If} $0 == "error"
      StrCpy $0 1
    ${EndIf}
    MessageBox MB_ICONSTOP|MB_OK "SentinelDesk was not removed.$\r$\n$\r$\nCheck the Uninstall Key - invalid attempts are reported to IT." /SD IDOK
    SetErrorLevel $0
    Abort
  ${EndIf}

  DeleteRegKey HKLM "${REGKEY}"
  SetErrorLevel 0
SectionEnd
