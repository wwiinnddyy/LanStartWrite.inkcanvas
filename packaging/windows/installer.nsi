; 揽星书写 · Windows 安装包脚本（NSIS 3）
;
; 由 .github/workflows/release.yml 调用（工作目录必须是本文件所在目录，OutFile 是相对它算的）：
;   makensis /DVERSION=1.2.0 /DVERSION4=1.2.0.0 /DBINDIR=<publish 目录> installer.nsi
;   产出的安装包落在 ..\dist\ 下。
;
; **本文件必须带 UTF-8 BOM**：makensis 只在有 BOM 时把脚本当 UTF-8 读，
; 否则按系统代码页读 —— 在英文机器（cp1252）上，安装器界面里的中文会变成乱码。
;
; 设计取舍：
;   - 每用户安装（$LOCALAPPDATA），不写 HKLM、不需要管理员 —— 批注工具不该要 UAC。
;   - 覆盖式安装：不卸旧版、不做版本比较，直接把新文件盖上去（便携场景下这最不容易出错）。
;   - 卸载只删自己装过的东西与快捷方式，不动用户数据（preferences.json 在
;     %LOCALAPPDATA%\LanStartWrite —— 与安装目录是两个文件夹，升级/卸载都保留）。

Unicode true
ManifestDPIAware true

!if "${VERSION}" == ""
  !error "请用 /DVERSION=x.y.z 传入版本号（不带 v 前缀）"
!endif

; NSIS 的版本资源只认四段数字，且每段 ≤ 65535。
; VERSION4 由工作流算好传入（取三段数字补 .0；预发布后缀不能进版本资源）。
!define APPNAME "揽星书写"
!define APPBASENAME "LanStartWrite.Inkcanvas"
!define COMPANY "wwiinnddyy"
!define EXENAME "${APPBASENAME}.exe"

!if "${VERSION4}" == ""
  !error "请用 /DVERSION4=x.y.z.0 传入四段版本号（由工作流从 VERSION 算出）"
!endif

Name "${APPNAME} ${VERSION}"
OutFile "..\dist\${APPBASENAME}-${VERSION}-windows-x64-setup.exe"
InstallDir "$LOCALAPPDATA\${APPBASENAME}"
InstallDirRegKey HKCU "Software\${COMPANY}\${APPNAME}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma

VIProductVersion "${VERSION4}"
VIAddVersionKey /LANG=2052 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=2052 "CompanyName" "${COMPANY}"
VIAddVersionKey /LANG=2052 "FileDescription" "${APPNAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "GPL-3.0"

!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPBASENAME}"

Page Directory
Page InstFiles
; 卸载侧两条都要。关键字是 uninstConfirm（不是 Confirm —— 写错在解析阶段就红：
; Usage: UninstPage ... / Error in script on line 53）；而 instfiles 那一页**必须留着**，
; 删掉的后果不是报错而是警告 —— "Uninstall page instfiles not used, no sections will be executed!"，
; 于是 Uninstall 段（真正删文件与注册表的那一节）根本不跑，卸载点了等于什么都没删。
UninstPage UninstConfirm
UninstPage InstFiles

!macro Files
  ; /r 整个目录：publish 产物里 dll/exe/资源一大把，逐个列举只会与发布清单脱节。
  File /r "${BINDIR}\*.*"
!macroend

Section "Install"
  SetOutPath "$INSTDIR"
  WriteRegStr HKCU "Software\${COMPANY}\${APPNAME}" "InstallDir" "$INSTDIR"

  !insertmacro Files

  ; 卸载器要先于快捷方式生成：UninstallString 是"已安装"的判据。
  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "${COMPANY}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXENAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" "$INSTDIR\uninstall.exe"
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"
SectionEnd

Section "Uninstall"
  ; 安装目录整个删掉：那里面只有发布产物，没有用户的东西。
  ; **用户数据在 %LOCALAPPDATA%\LanStartWrite\（preferences.json：笔锋、工具栏、主题），
  ;   与安装目录（LanStartWrite.Inkcanvas）是两个文件夹 —— 这里绝不碰它，
  ;   升级与卸载都不动用户自己攒的笔锋与工具栏。
  RMDir /r "$INSTDIR"

  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"
  Delete "$DESKTOP\${APPNAME}.lnk"

  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "Software\${COMPANY}\${APPNAME}"
SectionEnd
