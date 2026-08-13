Unicode true
!include "MUI2.nsh"
!include "SafeWorld.UninstallRegistration.nsh"

!ifndef PRODUCT_ROOT
  !error "PRODUCT_ROOT is required"
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required"
!endif
!ifndef PRODUCT_VERSION
  !error "PRODUCT_VERSION is required"
!endif
!ifndef UNINSTALL_FILES
  !error "UNINSTALL_FILES is required"
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
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "SafeWorld"
  SetOutPath "$INSTDIR"
  ; Earlier SafeWorld beta packages owned this legacy Steam configuration filename. Remove only
  ; that known package-owned file before copying the canonical safeworld-steam.json so an in-place
  ; upgrade cannot leave both sources behind and trigger the runtime's intentional ambiguity fence.
  Delete "$INSTDIR\steward-steam.json"
  File /r "${PRODUCT_ROOT}\*.*"
  CreateDirectory "$SMPROGRAMS\SafeWorld"
  CreateShortcut "$SMPROGRAMS\SafeWorld\SafeWorld.lnk" "$INSTDIR\SafeWorld.Desktop.exe"
  CreateShortcut "$DESKTOP\SafeWorld.lnk" "$INSTDIR\SafeWorld.Desktop.exe"
  WriteUninstaller "$INSTDIR\Uninstall SafeWorld.exe"
  !insertmacro SafeWorldRegisterUninstall "${PRODUCT_VERSION}"
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\SafeWorld.lnk"
  Delete "$SMPROGRAMS\SafeWorld\SafeWorld.lnk"
  RMDir "$SMPROGRAMS\SafeWorld"
  !insertmacro SafeWorldUnregisterUninstall
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\Uninstall SafeWorld.exe"
  RMDir "$INSTDIR"
SectionEnd
