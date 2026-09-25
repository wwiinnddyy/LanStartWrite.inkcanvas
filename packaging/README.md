# 打包与发布（release workflow）

`.github/workflows/release.yml` 一次跑三个平台腿（windows-x64 / linux-x64 / linux-arm64），
产出七件安装包，并（可选）创建 GitHub Release。
Linux 这一端每种架构两个包（deb + AppImage）共用**该架构那一次** `dotnet publish` 的产物；
裸发布目录按架构各留一份中间产物（`linux-publish-x64-*` / `linux-publish-arm64-*`），
Flatpak 与玲珑取 x64 那一份 —— 两个中间产物都不上传 Release。

| 平台 | 产物（v1.0.1 实测大小） | 打法 |
|---|---|---|
| Windows | `…-windows-x64-setup.exe` 32 MiB | NSIS（`packaging/windows/installer.nsi`），每用户装到 `$LOCALAPPDATA` |
| Linux · deb | `…-linux-amd64.deb` 35 MiB · `…-linux-arm64.deb`（arm64 的数待第一轮 CI 填） | `dpkg-deb`（`packaging/linux/build-deb.sh`），载荷在 `/opt/lanstartwrite` |
| Linux · AppImage | `…-linux-x64.AppImage` 42 MiB · `…-linux-arm64.AppImage`（同上） | appimagetool（`packaging/linux/build-appimage.sh`，工具自己解包 —— CI 没 FUSE） |
| Linux · Flatpak | `…-linux-x64.flatpak` 32 MiB | flatpak-builder + 清单 `packaging/linux/<appid>.json`（**只有 x64**） |
| Linux · 玲珑 | `io.github.wwiinnddyy.lanstartwrite_1.0.1.0_x86_64_main.uab` 65 MiB | `ll-builder`（模板 `packaging/linux/linglong.yaml.in`，把上面那个 .deb 摊进容器；**只有 x64**） |

## 16K 页与那条 ELF 门禁

arm64 的 Linux 内核有 4K / 16K / 64K 三种页大小编译档位（Android 15+ 已把 16K 当准入门槛，
发行版的 "16k pages" 变体也在铺开）。ELF 里 `PT_LOAD` 的 `p_align` 若停在 4K，包**装得上、跑不起来**
—— 错在 exec / dlopen 阶段，CI 里一点征兆都没有。所以每个 Linux 腿在打包之前先跑一遍：

```sh
python3 packaging/linux/check-page-align.py publish --require-machine aarch64 --require-align 16384
```

x64 那一腿要求 `--require-machine x86_64 --require-align 4096`。门槛按架构分档，不许一刀切：
实测同一份检查把 x64 的门槛抬到 16384，**23 个 ELF 全部红**（那份产物就是 `p_align = 0x1000`）。
而 arm64 那份实测 23 个 ELF（21 个 `.so` + apphost + createdump）**全部 `p_align = 0x10000`（64K）**，
比 16K 的门槛高一档。

脚本是纯标准库的 Python 而不是 `readelf`：这台开发机上没有 binutils，而这份检查要在本机与
runner 上跑同一份。四条红路都拿变异验过（改掉一个 `p_align`、换错机器类型、指一个空目录、
把门槛抬到真产物之上），"扫到一个 ELF 都没有"也算红 —— 目录写错的绿比没检查更坏。


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
- **`actions/download-artifact` 会丢掉 Unix 的 +x 位**：所以 flatpak 清单与 `build-deb.sh`
  都自己 `chmod +x` 那个 apphost，而 `build-deb.sh` 的入口守门只看"文件在"不看"可执行"
  —— 早先它用 `test -x`，在玲珑那一步里静默把 deb 变成空串，报错报在完全无关的地方。
- **自包含 .NET 在 Ubuntu 24.04 上唯一解析不出来的库是 `liblttng-ust.so.0`**（coreclr 的 LTTng
  追踪 provider 要它；24.04 只提供 `.so.1`，缺了只是没有 LTTng 追踪，运行时自己降级）。
  因此它进 deb 那步的白名单，**不是**往 `Depends` 里加一条装不上的包；白名单外的缺库 CI 直接红。
  deb 的 `Depends` 实测只需要 `libc6, libstdc++6`。
  ⚠️ 这条"唯一"是 **x64** 的账；arm64 那一腿第一次跑要么与它一致、要么在这一步红并把库名打出来。
- **arm64 这一腿第一轮要盯的三件事**（都还没在真 runner 上跑过，所以不写成"已确认"）：
  ① `ubuntu-24.04-arm` 这个标签在本仓库可用（GitHub 的 hosted arm64 Linux runner 已 GA）；
  ② 那台 runner 上有没有 docker（deb 的装包验收要用它；没有就红在"Linux · deb"那一步）；
  ③ arm64 那份产物解析出来的缺库是否只有白名单里那一条。
  已经实测过的：`dotnet publish -r linux-arm64` 在 Windows 上能编出来（263 文件）、两个架构的产物
  本地各扫一遍门禁（arm64 23 个 ELF 全 aarch64 / `0x10000`；x64 23 个全 x86_64 / `0x1000`，
  按各自的门槛一绿一红都对得上）、appimagetool 的 `continuous` 里 `appimagetool-aarch64.AppImage`
  这个资产在（HTTP 200）。
- **Wayland 会话做不到全屏批注**，这不是打包能补的：无边框全屏输入覆盖层与屏幕取帧都被合成器挡住。
  Flatpak 因此只申请 `fallback-x11` + `wayland` + `dri` + `ipc`，X11 会话下正常。玲珑 / deb 同理。
- **玲珑的工具链只认 HTTP(S) 取源**：`kind: file` 是交给 `/usr/bin/wget` 的，`file://` 与相对路径
  都不吃（后者被当主机名解析）。CI 就地起 `python3 -m http.server` 喂同一个 .deb，
  不依赖外部托管也不用先把包公开。另外它默认连接超时 5 秒，海外 runner 取
  `mirror-repo-linglong.deepin.com`（UAB 要 `cn.org.linyaps.builder.utils`）不够，
  这里给 `LINGLONG_CONNECT_TIMEOUT=120`。`ll-builder export` 产的是 **.uab**（离线单文件），不是 `.lca`。

## 装（Linux 四种格式）

```sh
# deb 与 AppImage 各有 x64 / arm64 两份，下面按 x64 写；换架构只改文件名那一段
sudo apt install ./LanStartWrite.Inkcanvas-1.0.1-linux-amd64.deb   # deb（arm64 → -linux-arm64.deb）
chmod +x LanStartWrite.Inkcanvas-1.0.1-linux-x64.AppImage && ./LanStartWrite.Inkcanvas-1.0.1-linux-x64.AppImage   # （arm64 → -linux-arm64.AppImage）
flatpak install ./LanStartWrite.Inkcanvas-1.0.1-linux-x64.flatpak  # Flatpak 单文件包
chmod +x io.github.wwiinnddyy.lanstartwrite_1.0.1.0_x86_64_main.uab && ./*.uab   # 玲珑 UAB（免装）
```

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

# 2b) arm64 同理（-r linux-arm64），编出来的东西当场用门禁扫一遍
dotnet publish src/LanStartWrite.Inkcanvas/LanStartWrite.Inkcanvas.csproj `
  -c Release -f net10.0 -r linux-arm64 --self-contained true `
  -p:Version=1.2.0 `
  -p:FluentJaliumProject="C:\git\Jalium\FluentJalium\src\FluentJalium\FluentJalium.csproj" `
  -o <目录>
python packaging/linux/check-page-align.py <目录> --require-machine aarch64 --require-align 16384

# 3) NSIS（本机没装的话：CI 里会自动 choco install；装好后从 packaging/windows 跑）
makensis /DVERSION=1.2.0 /DVERSION4=1.2.0.0 /DBINDIR=<目录> packaging\windows\installer.nsi
```

实测基线（2026-09-24）：Windows 发布 108.7 MB（自包含）、Linux 交叉发布 113.7 MB（自包含，
含 7 个 `libjalium.native.*.so`）、`-p:Version` 会进 exe 的 FileVersion/ProductVersion。
