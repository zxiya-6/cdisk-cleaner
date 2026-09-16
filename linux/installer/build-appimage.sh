#!/bin/bash
# 构建通用 AppImage（自包含，任何发行版可运行）
set -e
cd /home/cleanmaster/build/linux
STAGE=${STAGE_DIR:-installer/stage}
VERSION=1.0.0
DIST=/home/cleanmaster/packages/dist
mkdir -p "$DIST"

# 1. 定位 appimagetool（优先使用已下载缓存的完整文件）
CACHE_TOOL=/home/cleanmaster/packages/appimagetool
TOOL="$STAGE/appimagetool"
if [ -x "$CACHE_TOOL" ]; then
  TOOL="$CACHE_TOOL"
elif [ -x "$TOOL" ]; then
  :
else
  echo "downloading appimagetool（断点续传直到完整）..."
  URL="https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
  TARGET=8811712
  rm -f "$TOOL"
  for i in $(seq 1 30); do
    SIZE=$(stat -c%s "$TOOL" 2>/dev/null || echo 0)
    [ "$SIZE" -ge "$TARGET" ] && break
    curl -sL -C - -o "$TOOL" "$URL" --connect-timeout 15 --max-time 90
    sleep 2
  done
  chmod +x "$TOOL" 2>/dev/null || true
fi
[ -s "$TOOL" ] || { echo "appimagetool 不可用（下载失败），AppImage 构建跳过"; exit 1; }
echo "using appimagetool: $TOOL"

# 2. 构造 AppDir
APPDIR="$STAGE/AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/lib/cleanmaster/cli" "$APPDIR/usr/lib/cleanmaster/gui" \
         "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"

cp -a "$STAGE/publish/cli/." "$APPDIR/usr/lib/cleanmaster/cli/"
cp -a "$STAGE/publish/gui/." "$APPDIR/usr/lib/cleanmaster/gui/"
chmod -R a+rX "$APPDIR/usr/lib/cleanmaster"

# 相对路径 wrapper（AppImage 挂载点不定）
cat > "$APPDIR/usr/bin/cleanmaster" <<'EOF'
#!/bin/sh
exec "$APPDIR/usr/lib/cleanmaster/cli/CleanMasterCli" "$@"
EOF
cat > "$APPDIR/usr/bin/cleanmaster-gui" <<'EOF'
#!/bin/sh
exec "$APPDIR/usr/lib/cleanmaster/gui/CleanMasterApp" "$@"
EOF
chmod 755 "$APPDIR/usr/bin/cleanmaster" "$APPDIR/usr/bin/cleanmaster-gui"

cp installer/assets/icon/256x256/apps/cleanmaster.png "$APPDIR/usr/share/icons/hicolor/256x256/apps/cleanmaster.png"
cp installer/assets/icon/256x256/apps/cleanmaster.png "$APPDIR/cleanmaster.png"
cp installer/assets/icon/256x256/apps/cleanmaster.png "$APPDIR/.DirIcon"

cat > "$APPDIR/cleanmaster.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=磁盘清理助手
Name[en]=CleanMaster
Comment=安全清理系统与应用缓存
Exec=cleanmaster-gui
Icon=cleanmaster
Terminal=false
Categories=Utility;System;
EOF
cp "$APPDIR/cleanmaster.desktop" "$APPDIR/usr/share/applications/"

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
SELF=$(readlink -f "$0")
HERE=$(dirname "$SELF")
export APPDIR="$HERE"
exec "$HERE/usr/bin/cleanmaster-gui" "$@"
EOF
chmod 755 "$APPDIR/AppRun"

# 3. 打包（无 fuse 环境：先解包 appimagetool 自身再用真实二进制）
OUT="$DIST/CleanMaster-$VERSION-x86_64.AppImage"
rm -f "$OUT"

EXTRACT_DIR=/tmp/ax-squashfs
rm -rf "$EXTRACT_DIR"
mkdir -p "$EXTRACT_DIR"
( cd "$EXTRACT_DIR" && "$TOOL" --appimage-extract >/dev/null 2>&1 )
REAL_TOOL="$EXTRACT_DIR/squashfs-root/AppRun"
[ -x "$REAL_TOOL" ] || REAL_TOOL="$EXTRACT_DIR/squashfs-root/usr/bin/appimagetool"
if [ -x "$REAL_TOOL" ]; then
  "$REAL_TOOL" "$APPDIR" "$OUT" 2>&1 | tail -4
else
  APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" --appimage-extract-and-run "$APPDIR" "$OUT" 2>&1 | tail -4
fi
echo "== AppImage built =="
ls -la "$OUT" 2>/dev/null || ls -la "$DIST/"
