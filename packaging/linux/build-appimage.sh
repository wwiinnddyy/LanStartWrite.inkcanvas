#!/usr/bin/env bash
# 把 dotnet publish 的产物组装成 AppDir，再用 appimagetool 打成 AppImage。
# 由 .github/workflows/release.yml 调用：
#   ./packaging/linux/build-appimage.sh <publish 目录> <版本号> <输出目录>
#
# 依赖：appimagetool（本脚本自己下到 RUNNER_TEMP 里，且以"解包后直接跑里面的二进制"的方式用 ——
# CI 机器上没有 FUSE，AppImage 挂不起来），以及 desktop-file-validate
# （appimagetool 缺了它就直接退出码 1；工作流里 apt 装上）。

set -euo pipefail

PUBLISH_DIR="${1:?用法: build-appimage.sh <publish 目录> <版本号> <输出目录>}"
VERSION="${2:?用法: build-appimage.sh <publish 目录> <版本号> <输出目录>}"
OUTPUT_DIR="${3:?用法: build-appimage.sh <publish 目录> <版本号> <输出目录>}"

LOWER_APPID="lanstartwrite"
EXEC_NAME="LanStartWrite.Inkcanvas"
# 目录名就是 AppImage 的 appid：appimagetool 拿它当卷标，去掉 .AppDir 后缀决定产物名。
APPDIR="$(mktemp -d)/$LOWER_APPID.AppDir"

mkdir -p "$APPDIR/usr/bin"

# 1) 发布产物整目录拷进去（self-contained：运行时也在里面，AppImage 才是真的"开箱即跑"）。
cp -a "$PUBLISH_DIR/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/$EXEC_NAME"

# 2) 桌面文件与图标：AppImage 的集成约定（appid 与 .desktop 的 Icon= 必须同名）。
#    根目录那一份不是冗余 —— appimagetool 就找 AppDir 根下的 <appid>.desktop 与 <appid>.png，
#    只有 usr/share 那一份时它直接 "Desktop file not found, aborting"（实测红过一次）。
mkdir -p "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp "$(dirname "$0")/lanstartwrite.desktop" "$APPDIR/usr/share/applications/$LOWER_APPID.desktop"
cp "$(dirname "$0")/$LOWER_APPID.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/$LOWER_APPID.png"
cp "$APPDIR/usr/share/icons/hicolor/256x256/apps/$LOWER_APPID.png" "$APPDIR/$LOWER_APPID.png"
cp "$APPDIR/usr/share/applications/$LOWER_APPID.desktop" "$APPDIR/$LOWER_APPID.desktop"

# 3) AppRun：AppImage 的入口就是这个脚本，它进的是 usr/bin 里那个可执行文件。
#    自己写死，而不是让工具去推导。
cat > "$APPDIR/AppRun" <<'EOF'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/LanStartWrite.Inkcanvas" "$@"
EOF
chmod +x "$APPDIR/AppRun"

# 4) 打包。用的不是 linuxdeploy 而是它底下的 appimagetool：
#    linuxdeploy 的本职是往 AppDir 里搬系统库（libc / libGL / X11），而自带运行时的 .NET 应用
#    该带的东西已经在 publish 目录里了 —— 把 glibc 搬进来只会换来"换个发行版就起不来"。
#    CI 镜像里没有 libfuse2，所以不能以 AppImage 的形态跑它：先 --appimage-extract 解包
#    （解包不需要 FUSE），再点名跑解出来的那个二进制。解包结果按 RUNNER_TEMP 缓存，重跑不重复下载。
TOOL_HOME="${APPIMAGETOOL_HOME:-${RUNNER_TEMP:-/tmp}/appimagetool}"
if [ ! -d "$TOOL_HOME/extracted" ]; then
  mkdir -p "$TOOL_HOME"
  curl -L --fail -o "$TOOL_HOME/tool.AppImage" \
    https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$TOOL_HOME/tool.AppImage"
  # 解包落在当前目录的 squashfs-root/，所以进去再解。
  ( cd "$TOOL_HOME" && ./tool.AppImage --appimage-extract >/dev/null && mv squashfs-root extracted )
fi
candidates=("$TOOL_HOME/extracted/usr/bin/appimagetool" "$TOOL_HOME/extracted/AppRun")
APPMAGETOOL=""
for c in "${candidates[@]}"; do
  [ -x "$c" ] && APPMAGETOOL="$c" && break
done
if [ -z "$APPMAGETOOL" ]; then
  echo "::error::解出来的 appimagetool 不在预期位置，实际布局："; ls -R "$TOOL_HOME/extracted" | head -40
  exit 1
fi
echo "appimagetool: $APPMAGETOOL"

mkdir -p "$OUTPUT_DIR"
cd "$OUTPUT_DIR"
# appimagetool 必读 ARCH（它按这个挑 runtime 文件），不给就直接退出。
ARCH=x86_64 "$APPMAGETOOL" "$APPDIR"

# 产物名按 .desktop 与目录名推，这里统一改成"应用-版本-平台"。
produced=0
for f in *.AppImage; do
  [ -e "$f" ] || continue
  mv "$f" "LanStartWrite.Inkcanvas-${VERSION}-linux-x64.AppImage"
  produced=1
done
if [ "$produced" != 1 ]; then
  echo "::error::appimagetool 退出码 0 但一个 AppImage 也没产出（当前目录：$(pwd)）"
  exit 1
fi

echo "AppImage 生成完毕：$OUTPUT_DIR/LanStartWrite.Inkcanvas-${VERSION}-linux-x64.AppImage"
