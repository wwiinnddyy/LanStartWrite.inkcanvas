#!/usr/bin/env bash
# 把 dotnet publish 的产物装成 .deb。
# 用法：build-deb.sh <publish 目录> <版本号> <dpkg 架构> <输出目录>
#   由 .github/workflows/release.yml 调用：packaging/linux/build-deb.sh publish 1.0.0 amd64 dist
#
# 装到哪里：/opt/<短名>/ 放整个发布目录（自带运行时也在里面），
# /usr/bin/<短名> 只是一层薄壳，.desktop / 图标按 FHS 放 —— 与 AppImage 那份内容同源，
# 差别只在"谁去解包"：AppImage 自己解，deb 交给 dpkg 落位。
#
# 反 DNS 的 APPID 是三个格式（deb / Flatpak / 玲珑）共用的标识，改它等于改包名与图标名，
# 所以下面只有一处定义。

set -euo pipefail

PUBLISH_DIR="${1:?用法: build-deb.sh <publish 目录> <版本号> <dpkg 架构> <输出目录>}"
VERSION="${2:?缺少版本号}"
ARCH="${3:?缺少 dpkg 架构（amd64 / arm64）}"
OUTPUT_DIR="${4:?缺少输出目录}"

HERE="$(cd "$(dirname "$0")" && pwd)"
APPID="$(cat "$HERE/appid")"          # 反 DNS：deb / Flatpak / 玲珑 三个格式共用一个标识
PKG="lanstartwrite"          # deb 包名只能小写字母数字与 - . +，不能是点分段的反 DNS
EXEC_NAME="LanStartWrite.Inkcanvas"
MAINTAINER="wwiinnddyy <wwiinnddyy@users.noreply.github.com>"

command -v dpkg-deb >/dev/null || { echo "::error::没有 dpkg-deb（装 dpkg-dev）"; exit 1; }
# 只要求"在"，不要求已经 +x：actions/download-artifact 回来时 Unix 模式位是丢的，
# 而下面本来就要把 apphost 的 +x 显式设回去 —— 在这儿卡住只会让玲珑那一步拿到一个空 deb。
[ -f "$PUBLISH_DIR/$EXEC_NAME" ] \
  || { echo "::error::$PUBLISH_DIR/$EXEC_NAME 不在发布目录里 —— 平台发错了？"; exit 1; }

ROOT="$(mktemp -d)/$PKG"
mkdir -p "$ROOT/DEBIAN" "$ROOT/opt/$PKG" "$ROOT/usr/bin" \
  "$ROOT/usr/share/applications" "$ROOT/usr/share/icons/hicolor/256x256/apps" "$ROOT/usr/share/pixmaps"

# 1) 应用本体。
cp -a "$PUBLISH_DIR/." "$ROOT/opt/$PKG/"
chmod +x "$ROOT/opt/$PKG/$EXEC_NAME"
printf '#!/bin/sh\nexec "/opt/%s/%s" "$@"\n' "$PKG" "$EXEC_NAME" > "$ROOT/usr/bin/$PKG"
chmod 755 "$ROOT/usr/bin/$PKG"

# 2) 桌面项与图标：与 AppImage 用同一份 .desktop，只把文件名换成 APPID
#    （Icon= 必须与图标文件名同名，否则菜单里是张默认图 —— 不报错，只是没图）。
cp "$HERE/$APPID.desktop" "$ROOT/usr/share/applications/$APPID.desktop"
cp "$HERE/$APPID.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/$APPID.png"
cp "$HERE/$APPID.png" "$ROOT/usr/share/pixmaps/$PKG.png"

# 3) control。Installed-Size 按 FHS 惯例是 KiB；Depends 只写实测必需的（多写会让 apt
#    在精简系统上拉一堆无关包，少写则装得上跑不起来 —— 后者靠工作流里那趟容器 ldd 验）。
SIZE_KB="$(du -sk -- "$ROOT" | cut -f1)"
{
  printf 'Package: %s\n' "$PKG"
  printf 'Version: %s\n' "$VERSION"
  printf 'Architecture: %s\n' "$ARCH"
  printf 'Maintainer: %s\n' "$MAINTAINER"
  printf 'Section: graphics\nPriority: optional\n'
  printf 'Installed-Size: %s\n' "$SIZE_KB"
  printf 'Depends: libc6, libstdc++6\n'
  printf 'Recommends: zenity | kdialog\n'   # 文件对话框一类的桌面服务，缺了不影响启动
  printf 'Homepage: https://github.com/wwiinnddyy/LanStartWrite.inkcanvas\n'
  printf 'Description: 揽星书写 —— 全屏批注与书写工具\n'
  printf ' LanStartWrite is a fullscreen screen-annotation and handwriting tool built on\n'
  printf ' Jalium.UI with the Dusk ink engine. Pens, erasers, taper profiles and a\n'
  printf ' data-driven toolbar; the canvas can also be made click-through.\n'
} > "$ROOT/DEBIAN/control"

# 4) md5sums（相对包根目录， Debian 惯例；lintian 会查）。
( cd "$ROOT" && find . -type f ! -path './DEBIAN/*' -printf '%P\n' \
    | sort | xargs -r md5sum ) > "$ROOT/DEBIAN/md5sums"

mkdir -p "$OUTPUT_DIR"
OUT="$OUTPUT_DIR/LanStartWrite.Inkcanvas-${VERSION}-linux-${ARCH}.deb"
dpkg-deb --build --root-owner-group "$ROOT" "$OUT"
rm -rf "$(dirname "$ROOT")"

dpkg-deb -I "$OUT" >/dev/null          # 读不回来的包不算造出来
echo "deb 生成完毕：$OUT  ($(du -h -- "$OUT" | cut -f1))"
