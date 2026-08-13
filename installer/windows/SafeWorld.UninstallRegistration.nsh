!macro SafeWorldRegisterUninstall Version
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SafeWorld" "DisplayName" "SafeWorld"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SafeWorld" "DisplayVersion" "${Version}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SafeWorld" "Publisher" "SafeWorld"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SafeWorld" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SafeWorld" "DisplayIcon" "$INSTDIR\SafeWorld.Desktop.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SafeWorld" "UninstallString" "$\"$INSTDIR\Uninstall SafeWorld.exe$\""
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SafeWorld" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SafeWorld" "NoRepair" 1
!macroend

!macro SafeWorldUnregisterUninstall
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SafeWorld"
!macroend
