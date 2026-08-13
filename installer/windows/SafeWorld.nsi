Unicode true
!include "MUI2.nsh"

!ifndef PRODUCT_ROOT
  !error "PRODUCT_ROOT is required"
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required"
!endif

Name "SafeWorld"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\SafeWorld"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "SafeWorld"

!define MUI_FINISHPAGE_RUN "$INSTDIR\SafeWorld.Desktop.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch SafeWorld"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "SafeWorld"
  SetOutPath "$INSTDIR"
  File /r "${PRODUCT_ROOT}\*.*"
  CreateDirectory "$SMPROGRAMS\SafeWorld"
  CreateShortcut "$SMPROGRAMS\SafeWorld\SafeWorld.lnk" "$INSTDIR\SafeWorld.Desktop.exe"
  CreateShortcut "$DESKTOP\SafeWorld.lnk" "$INSTDIR\SafeWorld.Desktop.exe"
  WriteUninstaller "$INSTDIR\Uninstall SafeWorld.exe"
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\SafeWorld.lnk"
  Delete "$SMPROGRAMS\SafeWorld\SafeWorld.lnk"
  RMDir "$SMPROGRAMS\SafeWorld"
  RMDir /r "$INSTDIR"
SectionEnd
