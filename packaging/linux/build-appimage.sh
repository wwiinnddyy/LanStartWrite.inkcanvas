#!/usr/bin/env bash
# 把 dotnet publish 的产物组装成 AppDir，再用 linuxdeploy 打成 AppImage。
# 由 .github/workflows/release.yml 调用：
#   ./packaging/linux/build-appimage.sh <publish 目录> <版本号> <输出目录>
#
# 依赖：linuxdeploy（走 --appimage-extract-and-run，CI 上没有 FUSE）。

set -euo pipefail

PUBLISH_DIR="${1:?用法: build-appimage.sh <publish 目录> <版本号> <输出目录>}"
VERSION="${2:?用法: build-appimage.sh <publish 目录> <版本号> <输出目录>}"
OUTPUT_DIR="${3:?用法: build-appimage.sh <publish 目录> <版本号> <输出目录>}"

APPDIR="$(mktemp -d)/AppDir"
EXEC_NAME="LanStartWrite.Inkcanvas"
LOWER_APPID="lanstartwrite"

mkdir -p "$APPDIR/usr/bin"

# 1) 发布产物整目录拷进去（self-contained：运行时也在里面，AppImage 才是真的"开箱即跑"）。
cp -a "$PUBLISH_DIR/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/$EXEC_NAME"

# 2) 桌面文件与图标：AppImage 的集成约定（linuxdeploy 按这个结构找它们）。
mkdir -p "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp "$(dirname "$0")/lanstartwrite.desktop" "$APPDIR/usr/share/applications/$LOWER_APPID.desktop"
cp "$(dirname "$0")/$LOWER_APPID.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/$LOWER_APPID.png"
cp "$APPDIR/usr/share/icons/hicolor/256x256/apps/$LOWER_APPID.png" "$APPDIR/$LOWER_APPID.png"

# 3) AppRun：linuxdeploy 会生成一个，但我们要保证它进的是 usr/bin 里那个 exe。
#    这里直接写一份，免得依赖 linuxdeploy 的推导。
cat > "$APPDIR/AppRun" <<'EOF'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/LanStartWrite.Inkcanvas" "$@"
EOF
chmod +x "$APPDIR/AppRun"

# 4) 打包。--appimage-extract-and-run：CI 上没有 FUSE，AppImage 自己解包运行是标准做法。
mkdir -p "$OUTPUT_DIR"
cd "$OUTPUT_DIR"
linuxdeploy --appdir "$APPDIR" --output appimage \
  --appimage-extract-and-run \
  >/dev/null

# linuxdeploy 用 .desktop 的 Name 产物名可能带空格，这里统一重命名成"应用-版本-平台"。
for f in *.AppImage; do
  [ -e "$f" ] || continue
  mv "$f" "LanStartWrite.Inkcanvas-${VERSION}-linux-x64.AppImage"
done

echo "AppImage 生成完毕：$OUTPUT_DIR/LanStartWrite.Inkcanvas-${VERSION}-linux-x64.AppImage"
