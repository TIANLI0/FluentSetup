; Northwind Sync - NSIS installer (Modern UI 2), all users
; makensis /DVERSION=3.2.0 installer\installer.nsi

Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!define APPNAME "Northwind Sync"
!define COMPANYNAME "Northwind Traders"
!define DESCRIPTION "Keeps your Northwind folders in sync"
!ifndef VERSION
  !define VERSION "3.2.0"
!endif
!define EXENAME "NorthwindSync.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\NorthwindSync"
!define HELPURL "https://northwind.example/sync/help"

Name "${APPNAME}"
OutFile "..\dist\NorthwindSync-${VERSION}-x64-setup.exe"
InstallDir "$PROGRAMFILES64\${COMPANYNAME}\${APPNAME}"
InstallDirRegKey HKLM "Software\${COMPANYNAME}\${APPNAME}" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${COMPANYNAME}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "CompanyName" "${COMPANYNAME}"
VIAddVersionKey "FileDescription" "${DESCRIPTION}"
VIAddVersionKey "LegalCopyright" "(c) ${COMPANYNAME}"

!define MUI_ICON "resources\sync.ico"
!define MUI_UNICON "resources\sync.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "license.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_COMPONENTS
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"

LangString DESC_Desktop ${LANG_ENGLISH} "Desktop shortcut"
LangString DESC_Desktop ${LANG_SIMPCHINESE} "桌面快捷方式"

!macro WriteSetting NAME VALUE
  WriteRegStr HKLM "Software\${COMPANYNAME}\${APPNAME}" "${NAME}" "${VALUE}"
!macroend

Section "!${APPNAME} (required)" SecMain
  SectionIn RO
  SetShellVarContext all
  SetOutPath "$INSTDIR"
  File /r /x "*.pdb" "..\build\Release\*.*"
  SetOutPath "$INSTDIR\drivers"
  File "..\third_party\nwfilter\nwfilter.inf"
  SetOutPath "$INSTDIR"
  SetOverwrite off
  File "defaults\sync.ini"
  SetOverwrite on
  CreateDirectory "$INSTDIR\logs"

  WriteUninstaller "$INSTDIR\uninst.exe"

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortCut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"
  CreateShortCut "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\uninst.exe"

  !insertmacro WriteSetting "InstallDir" "$INSTDIR"
  !insertmacro WriteSetting "Version" "${VERSION}"

  WriteRegStr HKLM "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninst.exe"'
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXENAME},0"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINSTKEY}" "Publisher" "${COMPANYNAME}"
  WriteRegStr HKLM "${UNINSTKEY}" "URLInfoAbout" "${HELPURL}"
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1

  ExecWait '"$INSTDIR\${EXENAME}" --install-service'
  nsExec::ExecToLog 'schtasks /delete /tn "NorthwindSync Updater" /f'
SectionEnd

Section "$(DESC_Desktop)" SecDesktop
  SetShellVarContext all
  CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"
SectionEnd

Section /o "Start with Windows" SecAutostart
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "NorthwindSync" '"$INSTDIR\${EXENAME}" --tray'
SectionEnd

Section "Explorer integration" SecShell
  WriteRegStr HKLM "Software\Classes\.nwsync" "" "Northwind.Sync.Link"
  WriteRegStr HKLM "Software\Classes\Northwind.Sync.Link" "" "Northwind Sync link"
  WriteRegStr HKLM "Software\Classes\Northwind.Sync.Link\DefaultIcon" "" "$INSTDIR\${EXENAME},0"
  WriteRegStr HKLM "Software\Classes\Northwind.Sync.Link\shell\open\command" "" '"$INSTDIR\${EXENAME}" "%1"'
  WriteRegStr HKLM "Software\Classes\Directory\shell\NorthwindSync" "" "Sync with Northwind"
SectionEnd

Section "Uninstall"
  SetShellVarContext all
  ExecWait '"$INSTDIR\${EXENAME}" --uninstall-service'
  Delete "$INSTDIR\uninst.exe"
  RMDir /r "$INSTDIR\logs"
  RMDir /r "$INSTDIR"
  Delete "$SMPROGRAMS\${APPNAME}\*.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"
  Delete "$DESKTOP\${APPNAME}.lnk"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "NorthwindSync"
  DeleteRegKey HKLM "Software\${COMPANYNAME}\${APPNAME}"
  DeleteRegKey /ifempty HKLM "Software\${COMPANYNAME}"
  DeleteRegKey HKLM "${UNINSTKEY}"
SectionEnd

Section /o "un.Remove settings and cache" UnSecData
  SetShellVarContext current
  RMDir /r "$LOCALAPPDATA\Northwind\Sync"
SectionEnd

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "64-bit Windows is required."
    Abort
  ${EndIf}
  ReadRegStr $0 HKLM "${UNINSTKEY}" "UninstallString"
FunctionEnd
