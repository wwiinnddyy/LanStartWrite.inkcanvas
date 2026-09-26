; 揽星书写 · Windows 安装包脚本（NSIS 3.12 / MUI2）
;
; 由 .github/workflows/release.yml 调用（工作目录必须是本文件所在目录，OutFile 是相对它算的）：
;   makensis /DVERSION=1.2.0 /DVERSION4=1.2.0.0 /DBINDIR=<publish 目录> installer.nsi
;   产出的安装包落在 ..\dist\ 下。
;
; **本文件必须带 UTF-8 BOM**：makensis 只在有 BOM 时把脚本当 UTF-8 读，
; 否则按系统代码页读 —— 在英文机器（cp1252）上，安装器界面里的中文会变成乱码。
;
; ## 这一版换成 MUI2 的理由（上一版是 NSIS 3 的裸页面流程）
;
; 上一版只有 `Page Directory` + `Page InstFiles` 两页，也就是 NSIS 1.x 时代那个
; "选个目录 → 进度条 → 完事"的骨架。它不是"老"，是**不像当代安装器**：
; 没有欢迎页、没有语言选择、没有"安装完成"页，图标是默认回环箭头。
; MUI2 是 NSIS 自带的那套（`MUI2.nsh`），换过去之后向导是四页、能选语言、
; 装完有"立即运行"勾选，深色系统上界面随主题走。
;
; ## 换版 / 改这个脚本时，四条自查清单（每一条都踩过）
;
; 1. **`MUI_*` 的 !define 必须在 `!include "MUI2.nsh"` 之前。**
;    MUI2.nsh 在自己体内读这些键并据此生成页面与对话框；顺序反了它读到的是空值，
;    而症状是"界面还是默认那套、没有任何报错"。
; 2. **不要引用不存在的文件。** `MUI_PAGE_LICENSE "…\LICENSE"` 与 `MUI_ICON "….ico"`
;    指向缺失文件时，makensis 在 CI 上直接红。**本仓库现在既没有 LICENSE 也没有 .ico**，
;    所以这两项故意没写 —— 真要加，先把文件放进仓库再改脚本。
; 3. **安装器的 LangString 与卸载器的 LangString 分开写。** 同一个键名在
;    `MUI_LANGUAGE` 与 `MUI_UNLANGUAGE` 下各写一遍，卸载器取到的会是安装器那份。
; 4. **字符串里没有 markdown。** `**粗体**` 在 NSIS 标签里是字面的星号。
;
; ## 仍然是每用户安装 —— 产品的决定，不是 NSIS 的限制
;
; 批注工具不该要 UAC。所以 `RequestExecutionLevel user`、装进 `$LOCALAPPDATA`、
; 注册表只写 HKCU。想装到系统级的人可以把整个目录复制到 Program Files 直接跑 ——
; 那个文件夹里没有必须登记的东西。

Unicode true

!if "${VERSION}" == ""
  !error "请用 /DVERSION=x.y.z 传入版本号（不带 v 前缀）"
!endif
!if "${VERSION4}" == ""
  !error "请用 /DVERSION4=x.y.z.0 传入四段版本号（由工作流从 VERSION 算出）"
!endif

!define APPNAME     "揽星书写"
!define APPBASENAME "LanStartWrite.Inkcanvas"
!define COMPANY     "wwiinnddyy"
!define EXENAME     "${APPBASENAME}.exe"
; 文件关联用的 ProgId 前缀。与 Linux 那边 .desktop 的 APPID 同一套命名，
; 两边各按平台的规矩写同一个身份，不是同一个字符串。
!define APPPDN      "${APPBASENAME}"
!define UNINSTKEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPBASENAME}"

; ── MUI2 的外观键：必须在 include 之前（清单第 1 条）────────────────────────
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN          "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_RUN_TEXT     "立即运行 ${APPNAME}"
!define MUI_FINISHPAGE_RUN_CHECKED
!define MUI_FINISHPAGE_TITLE        "${APPNAME} 已装好"

!include "MUI2.nsh"
; LogicLib 给 .onInit 里的条件判断；WinVer 给 ${RunningX64}（NSIS 3.09+ 自带 WinVer.nsh）。
!include "LogicLib.nsh"
!include "WinVer.nsh"

Name        "${APPNAME} ${VERSION}"
OutFile     "..\dist\${APPPDN}-${VERSION}-windows-x64-setup.exe"
InstallDir  "$LOCALAPPDATA\${APPBASENAME}"
InstallDirRegKey HKCU "Software\${COMPANY}\${APPNAME}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma

VIProductVersion "${VERSION4}"
VIAddVersionKey /LANG=2052 "ProductName"     "${APPNAME}"
VIAddVersionKey /LANG=2052 "CompanyName"     "${COMPANY}"
VIAddVersionKey /LANG=2052 "FileDescription" "${APPNAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion"     "${VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion"  "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright"  "GPL-3.0"

; 语言。**简体中文放第一个**：它在没有 Language 字符串表时是回退项，
; 而 English 放第二个，用户在英文机器上仍能得到英文界面而不是中文。
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ── 页面（顺序即向导顺序）────────────────────────────────────────────────────
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

; 卸载侧两条都要。少 instfiles 那一页只警告不报错，卸载器一节都不跑
; （"no sections will be executed"），退出码照样 0、安装包照样产出 ——
; 直到有人真去卸载才发现什么都没删。工作流因此把 makensis 日志读一遍。
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_UNLANGUAGE "SimpChinese"
!insertmacro MUI_UNLANGUAGE "English"

; ── 字符串表 ────────────────────────────────────────────────────────────────
; 安装器的键只写在 MUI_LANGUAGE 下，卸载器的只写在 MUI_UNLANGUAGE 下（清单第 3 条）。
; 页面上那两行字全靠这些键：**不给它们，MUI 就落到内置英文** ——
; 而一个中文产品装出来第一页写着 Welcome，是那种用户会记住的错。
!insertmacro MUI_LANGUAGE "SimpChinese"
  LangString MUI_WELCOMEPAGE_TITLE  ${LANG_SIMPCHINESE} "欢迎使用 ${APPNAME}"
  LangString MUI_WELCOMEPAGE_TEXT   ${LANG_SIMPCHINESE} "全屏批注与书写工具。$r$n$r$n本安装程序会装到你的用户目录，不需要管理员权限。$r$n$r$n已装过的话直接覆盖成新版本，不卸旧的。$r$n$r$n你的笔锋、工具栏与主题存在另一个文件夹里，升级与卸载都不会动它。$r$n$r$n将要安装到：$INSTDIR"
  LangString INSTALLUNINSTALLED     ${LANG_SIMPCHINESE} "已卸载 ${APPNAME}。"
!insertmacro MUI_LANGUAGE "English"
  LangString MUI_WELCOMEPAGE_TITLE  ${LANG_ENGLISH} "Welcome to ${APPNAME}"
  LangString MUI_WELCOMEPAGE_TEXT   ${LANG_ENGLISH} "Full-screen annotation and handwriting.$r$n$r$nThis installer writes to your per-user folder and does not need administrator rights.$r$n$r$nAn existing install is overwritten in place.$r$n$r$nYour pen tips, toolbar and theme live in a different folder and survive both upgrade and uninstall.$r$n$r$nInstalling to: $INSTDIR"
  LangString INSTALLUNINSTALLED     ${LANG_ENGLISH} "${APPNAME} has been uninstalled."
!insertmacro MUI_UNLANGUAGE "SimpChinese"
  LangString MUI_UNCONFIRMPAGE_TITLE ${LANG_SIMPCHINESE} "卸载 ${APPNAME}"
  LangString MUI_UNCONFIRMPAGE_TEXT  ${LANG_SIMPCHINESE} "安装目录（含你在其中存的笔记）会被删除。$r$n$r$n笔锋、工具栏、主题等设置在另一个文件夹，会保留。"
!insertmacro MUI_UNLANGUAGE "English"
  LangString MUI_UNCONFIRMPAGE_TITLE ${LANG_ENGLISH} "Uninstall ${APPNAME}"
  LangString MUI_UNCONFIRMPAGE_TEXT  ${LANG_ENGLISH} "The install folder (including any notes you keep in it) will be removed.$r$n$r$nYour pen tips, toolbar and theme live elsewhere and are kept."

; ── 兼容检查 ────────────────────────────────────────────────────────────────
; 这个包是 x64 的，装到 32 位 Windows 上会跑不起来。装之前说清，
; 而不是装完在桌面上双击才发现。
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "这个安装包是 64 位的。当前系统是 32 位 Windows，装上之后程序无法运行。$\n$\n请改用 32 位版本。$\n$\n按确定退出安装。"
    Abort
  ${EndIf}
FunctionEnd

; ── 安装 ────────────────────────────────────────────────────────────────────
Section "Install"
  SetOutPath "$INSTDIR"
  SetOverwrite on
  WriteRegStr HKCU "Software\${COMPANY}\${APPNAME}" "InstallDir" "$INSTDIR"

  ; /r 整个目录：publish 产物里 dll/exe/**原生库**一大把，逐个列举只会与发布清单脱节 ——
  ; 而原生库那份清单恰恰是最容易漏的（它们是 runtimes/ 下的传递资产，
  ; bblanchon.PDFium.Win32 与 Linux 两份加起来几十 MB）。
  File /r "${BINDIR}\*.*"

  ; 卸载器要先于快捷方式生成：UninstallString 是"已安装"的判据。
  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName"          "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher"            "${COMPANY}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon"          "$INSTDIR\${EXENAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString"      "$INSTDIR\uninstall.exe"
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" "$INSTDIR\uninstall.exe /S"
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk"                  "$INSTDIR\${EXENAME}"

  ; ── 文件关联：只给 PDF，**不碰图像那几个** ──────────────────────────────────
  ;
  ; 为什么只写 ProgId 与 OpenWithProgids 而不碰 UserChoice：
  ; Windows 10 1703 之后 UserChoice 带哈希保护，任何程序改
  ; HKCU\...\Explorer\FileExts\.pdf\UserChoice 都会被系统**静默还原**
  ; （症状：装完双击 .pdf 还是用别的程序打开，且没有任何提示）。
  ; 所以这里老老实实只登记 ProgId —— 效果是"双击弹选择列表，列表里有本应用"，
  ; 而**不**抢别人的默认。用户选一次之后就用本应用了，
  ; 那份选择是系统替他们记的，不归安装器管。
  ;
  ; DefaultIcon 写两处是因为 Windows 各版本认的地方不一样（`HKCR\.pdf` 与
  ; `HKCR\<ProgId>\DefaultIcon`）；少一处的后果是"图标有时有有时没有"。
  WriteRegStr HKCU "Software\Classes\${APPPDN}.pdf" "" "PDF 文档"
  WriteRegStr HKCU "Software\Classes\${APPPDN}.pdf" "DefaultIcon" "$INSTDIR\${EXENAME},0"
  WriteRegStr HKCU "Software\Classes\${APPPDN}.pdf\DefaultIcon" "" "$INSTDIR\${EXENAME},0"
  WriteRegStr HKCU "Software\Classes\${APPPDN}.pdf\shell\open\command" "" '"$INSTDIR\${EXENAME}" "%1"'
  WriteRegStr HKCU "Software\Classes\${APPPDN}.pdf\OpenWithProgids" "${APPPDN}.pdf" "1"
  WriteRegStr HKCU "Software\Classes\Applications\${EXENAME}\SupportedTypes" ".pdf" ""
  WriteRegStr HKCU "Software\Classes\Applications\${EXENAME}\shell\open\command" "" '"$INSTDIR\${EXENAME}" "%1"'

  ; 通知外壳重读：改了 ProgId 与快捷方式之后要它刷新，否则图标与"打开方式"列表
  ; 要等到下次登录才对 —— **这就是"装完立刻能用"与"要重启一下"的差别**。
  ; 后两个参数是 LPCVOID，用 p 而不是 i：x64 上按 int 传会让后面的栈参数错位。
  System::Call 'shell32::SHChangeNotify(i 0x8000000, i 0, p 0, p 0)'
SectionEnd

Section "Uninstall"
  ; 安装目录整个删掉：那里面只有发布产物与用户自己的笔记。
  ; **用户设置在 %LOCALAPPDATA%\LanStartWrite\（笔锋、工具栏、主题），
  ;   与安装目录（LanStartWrite.Inkcanvas）是两个文件夹 —— 这里绝不碰它。**
  RMDir /r "$INSTDIR"

  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  RMDir  "$SMPROGRAMS\${APPNAME}"
  Delete "$DESKTOP\${APPNAME}.lnk"

  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "Software\${COMPANY}\${APPNAME}"

  ; 文件关联要跟着卸。**留着它比不装更糟**：应用没了，ProgId 还在，
  ; 于是"双击一个 .pdf"在选择列表里点一下得到"找不到程序" ——
  ; 而那条记录是安装器自己写进去的，必须由安装器自己收回。
  ; 不碰 UserChoice：那是系统按用户选择写的，删它等于替用户改默认。
  DeleteRegKey HKCU "Software\Classes\${APPPDN}.pdf"
  DeleteRegValue HKCU "Software\Classes\Applications\${EXENAME}\SupportedTypes" ".pdf"
  DeleteRegValue HKCU "Software\Classes\Applications\${EXENAME}\shell\open\command" ""

  System::Call 'shell32::SHChangeNotify(i 0x8000000, i 0, p 0, p 0)'
SectionEnd
