# 打包与发布（release workflow）

`.github/workflows/release.yml` 产出两端安装包，并（可选）创建 GitHub Release：

| 平台 | 产物 | 打法 |
|---|---|---|
| Windows | `LanStartWrite.Inkcanvas-<版本>-windows-x64-setup.exe` | NSIS（`packaging/windows/installer.nsi`） |
| Linux | `LanStartWrite.Inkcanvas-<版本>-linux-x64.AppImage` | appimagetool（`packaging/linux/build-appimage.sh`，工具由脚本自己取并解包 —— CI 上没有 FUSE） |

## 怎么发一个版本

1. **手动触发**：Actions → release → Run workflow，填版本号（三段式，如 `1.2.0`，不带 `v` 前缀）。
2. **推 tag**：`git tag v1.2.0 && git push origin v1.2.0` —— 版本号取自 tag（去掉 `v`）。

两个输入项：

- `self_contained`（默认开）：自带 .NET 运行时。关掉装包会小很多，但用户机器得装 .NET 10
  （Windows 要 Desktop Runtime；Linux 要 .NET Runtime）——AppImage 关掉就失去"开箱即跑"了。
- `create_release`（默认**关**）：打完包是否建 GitHub Release 并上传产物。默认关是让它先当
  "能不能编出来、能不能打出包"的探针反复跑；推 tag 那条路不受这一项影响，一定发。

## 两个仓库外依赖（工作流自己处理）

| 依赖 | 怎么进 CI |
|---|---|
| FluentJalium（兄弟仓库，源码直引） | 工作流签出 `wwiinnddyy/FluentJalium` 的 **Astra** 分支（`main` 那份布局里没有 `src/FluentJalium`；ref 可用仓库变量 `FLUENT_JALIUM_REF` 钉到某个提交），再用 `-p:FluentJaliumProject=…` 指给编译器 |
| Dusk（闭源 SDK，本机 feed 在 `../dusk-feed`） | `packaging/feed/` 里**随仓库带了一份拷贝**（NuGet.config 里第二个源，本机仍优先走 `../dusk-feed`） |

⚠️ `packaging/feed/` 里是闭源混淆包 —— 本仓库若转公开，这份拷贝必须挪走（换私有 feed 或 secret 下载）。

## 平台差异（发布前先知道）

- **应用双目标**：`net10.0-windows`（主）+ `net10.0`（Linux）。`EnableWindowsTargeting=true`
  是为了让 Linux 能还原 windows 那一端。
- **Win32 互操作已按 OS 收口**：`NativeWindowZOrder` / `ScreenCapture` 的每个公开入口
  都先 `OperatingSystem.IsWindows()`，非 Windows 返回"没生效 / 没有"。
  于是 Linux 上：**窗口层级不再排原生 Z 序**（模型照常运转）、**冻结模式降级为没有底图**、
  **穿透模式当前依赖 Win32 样式位与 `WM_NCHITTEST` 钩子，Linux 端暂未实现**。
- **FluentJalium 必须多目标**（`net10.0-windows;net10.0`）：这条不只是 Linux 那一端的事 ——
  还原走的是整个双目标图，所以在 Windows 上 `publish -f net10.0-windows` 一样会去问
  FluentJalium 的 net10.0 并红掉（实测：`error NU1201: Project FluentJalium is not compatible with net10.0`）。
  上游已于 `e1f3366` 多目标，工作流里那条 sed 兜底随之删掉；哪天它退回单目标，CI 会红在 NU1201 上。
- **Linux 产物里混着一些 Windows 原生 dll**（`jalium.native.*.dll`）：Jalium 的 build targets
  无条件拷贝所致，AppImage 里是死重（几 MB），不影响运行；该问题应报给上游。

## 本机验证（改了打包脚本先在本地跑一遍）

```powershell
# 1) Windows 发布（和 CI 同一条命令）
dotnet publish src/LanStartWrite.Inkcanvas/LanStartWrite.Inkcanvas.csproj `
  -c Release -f net10.0-windows -r win-x64 --self-contained true `
  -p:Version=1.2.0 `
  -p:FluentJaliumProject="C:\git\Jalium\FluentJalium\src\FluentJalium\FluentJalium.csproj" `
  -o <目录>

# 2) Linux 交叉发布（在 Windows 上即可验证"能不能编过"）
dotnet publish src/LanStartWrite.Inkcanvas/LanStartWrite.Inkcanvas.csproj `
  -c Release -f net10.0 -r linux-x64 --self-contained true `
  -p:Version=1.2.0 `
  -p:FluentJaliumProject="C:\git\Jalium\FluentJalium\src\FluentJalium\FluentJalium.csproj" `
  -o <目录>

# 3) NSIS（本机没装的话：CI 里会自动 choco install；装好后从 packaging/windows 跑）
makensis /DVERSION=1.2.0 /DVERSION4=1.2.0.0 /DBINDIR=<目录> packaging\windows\installer.nsi
```

实测基线（2026-09-24）：Windows 发布 108.7 MB（自包含）、Linux 交叉发布 113.7 MB（自包含，
含 7 个 `libjalium.native.*.so`）、`-p:Version` 会进 exe 的 FileVersion/ProductVersion。
