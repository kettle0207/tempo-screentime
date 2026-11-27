!ifdef AMD64_STUB
  Target amd64-unicode
!else
  Target x86-unicode
!endif
Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
Name "Tempo スクリーンタイム"
OutFile "..\dist\Tempo-Setup-x64.exe"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\Tempo"
SetCompressor /SOLID lzma
BrandingText "Tempo"
Icon "..\src\tempo.ico"
UninstallIcon "..\src\tempo.ico"
!ifndef VERSION
  !define VERSION "1.3.0"
!endif
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "Tempo スクリーンタイム"
VIAddVersionKey "FileDescription" "Tempo インストーラー"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Tempo"
!define KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\TempoScreenTime"
!define HOSTCHROME "Software\Google\Chrome\NativeMessagingHosts\local.tempo.screentime"
!define HOSTEDGE "Software\Microsoft\Edge\NativeMessagingHosts\local.tempo.screentime"
Var UpdateMode
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\ScreenTime.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Tempo を起動する"
!define MUI_PAGE_CUSTOMFUNCTION_PRE DirectoryPage
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Japanese"

Function DirectoryPage
  ReadRegStr $0 HKCU "${KEY}" "InstallLocation"
  ${If} ${FileExists} "$0\ScreenTime.exe"
    StrCpy $INSTDIR "$0"
    Abort
  ${EndIf}
FunctionEnd

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Windows 11 の x64 版が必要です。"
    Abort
  ${EndIf}
  SetRegView 64
  ; In-app updates pass /S /UPDATE /D=<current folder>. Keep that folder and restart Tempo afterwards.
  StrCpy $UpdateMode 0
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/UPDATE" $R1
  ${IfNot} ${Errors}
    StrCpy $UpdateMode 1
  ${EndIf}
  ReadRegStr $0 HKCU "${KEY}" "InstallLocation"
  ${If} $UpdateMode == 0
  ${AndIf} ${FileExists} "$0\ScreenTime.exe"
    StrCpy $INSTDIR "$0"
  ${EndIf}
FunctionEnd

Function .onInstSuccess
  ${If} $UpdateMode == 1
    Exec '"$INSTDIR\ScreenTime.exe"'
  ${EndIf}
FunctionEnd

Function .onInstFailed
  ; Keep the current version usable when an update cannot finish.
  ${If} $UpdateMode == 1
  ${AndIf} ${FileExists} "$INSTDIR\ScreenTime.exe"
    Exec '"$INSTDIR\ScreenTime.exe"'
  ${EndIf}
FunctionEnd

!macro CloseTempo
  ${If} ${FileExists} "$INSTDIR\ScreenTime.exe"
    ExecWait '"$INSTDIR\ScreenTime.exe" --shutdown' $0
    ${If} $0 != 0
      MessageBox MB_ICONSTOP "Tempo を終了してから、もう一度実行してください。" /SD IDOK
      Abort
    ${EndIf}
  ${EndIf}
!macroend

Section "Tempo"
  SetShellVarContext current
  !insertmacro CloseTempo
  SetOutPath "$INSTDIR"
  File "stage\ScreenTime.exe"
  Delete "$INSTDIR\ScreenTime.exe.config"
  File "stage\Tempo.Dashboard.exe"
  File "stage\Tempo.Dashboard.exe.config"
  File "stage\Tempo.BrowserHost.exe"
  File "stage\Measure-Tempo.exe"
  File "stage\Measure-Tempo.ps1"
  File "stage\browser-host.json"
  File "stage\Microsoft.Web.WebView2.Core.dll"
  File "stage\Microsoft.Web.WebView2.WinForms.dll"
  File "stage\MinGW-NOTICES.txt"
  File "stage\WebView2-LICENSE.txt"
  File "stage\WebView2-NOTICE.txt"
  SetOutPath "$INSTDIR\Extension"
  File "stage\Extension\manifest.json"
  File "stage\Extension\worker.js"
  SetOutPath "$INSTDIR\runtimes\win-x64\native"
  File "stage\runtimes\win-x64\native\WebView2Loader.dll"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Tempo.lnk" "$INSTDIR\ScreenTime.exe"
  CreateShortcut "$SMPROGRAMS\Tempo の負荷測定.lnk" "$INSTDIR\Measure-Tempo.exe"
  SetRegView 64
  WriteRegStr HKCU "${HOSTCHROME}" "" "$INSTDIR\browser-host.json"
  WriteRegStr HKCU "${HOSTEDGE}" "" "$INSTDIR\browser-host.json"
  WriteRegStr HKCU "${KEY}" "DisplayName" "Tempo スクリーンタイム"
  WriteRegStr HKCU "${KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${KEY}" "DisplayIcon" "$INSTDIR\ScreenTime.exe"
  WriteRegStr HKCU "${KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  !insertmacro CloseTempo
  ReadRegStr $0 HKCU "${HOSTCHROME}" ""
  ${If} $0 == "$INSTDIR\browser-host.json"
    DeleteRegKey HKCU "${HOSTCHROME}"
  ${EndIf}
  ReadRegStr $0 HKCU "${HOSTEDGE}" ""
  ${If} $0 == "$INSTDIR\browser-host.json"
    DeleteRegKey HKCU "${HOSTEDGE}"
  ${EndIf}
  DeleteRegKey HKCU "${KEY}"
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Tempo ScreenTime"
  ${If} $0 == '"$INSTDIR\ScreenTime.exe" --background'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Tempo ScreenTime"
  ${EndIf}
  Delete "$SMPROGRAMS\Tempo.lnk"
  Delete "$SMPROGRAMS\Tempo の負荷測定.lnk"
  Delete "$INSTDIR\Extension\manifest.json"
  Delete "$INSTDIR\Extension\worker.js"
  RMDir "$INSTDIR\Extension"
  Delete "$INSTDIR\browser-host.json"
  Delete "$INSTDIR\Tempo.BrowserHost.exe"
  Delete "$INSTDIR\Measure-Tempo.exe"
  Delete "$INSTDIR\Measure-Tempo.ps1"
  Delete "$INSTDIR\ScreenTime.exe"
  Delete "$INSTDIR\Tempo.Dashboard.exe"
  Delete "$INSTDIR\Tempo.Dashboard.exe.config"
  Delete "$INSTDIR\Microsoft.Web.WebView2.Core.dll"
  Delete "$INSTDIR\Microsoft.Web.WebView2.WinForms.dll"
  Delete "$INSTDIR\runtimes\win-x64\native\WebView2Loader.dll"
  RMDir "$INSTDIR\runtimes\win-x64\native"
  RMDir "$INSTDIR\runtimes\win-x64"
  RMDir "$INSTDIR\runtimes"
  Delete "$INSTDIR\MinGW-NOTICES.txt"
  Delete "$INSTDIR\WebView2-LICENSE.txt"
  Delete "$INSTDIR\WebView2-NOTICE.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  RMDir /r "$LOCALAPPDATA\Tempo\ScreenTime\Updates"
SectionEnd
