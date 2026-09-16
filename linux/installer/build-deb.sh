#!/bin/bash
# 构建 Debian .deb 包
set -e
cd /home/cleanmaster/build/linux
STAGE=${STAGE_DIR:-installer/stage}
DIST=${DIST_DIR:-/home/cleanmaster/packages/dist}
mkdir -p "$DIST"
VERSION=1.0.0

bash installer/make-root.sh

DEB="$DIST/cleanmaster_${VERSION}_amd64.deb"
rm -f "$DEB"

# control 文件
mkdir -p "$STAGE/deb/DEBIAN"
cat > "$STAGE/deb/DEBIAN/control" <<EOF
Package: cleanmaster
Version: $VERSION
Section: utils
Priority: optional
Architecture: amd64
Maintainer: CleanMaster Team <dev@cleanmaster.local>
Installed-Size: $(du -sk "$STAGE/root" | cut -f1)
Depends: libgtk-3-0, libice6, libsm6, libx11-6, libxrandr2, libxcursor1, libxkbcommon0, libgl1, libegl1, libfontconfig1, fonts-noto-cjk
Recommends: libgtk-3-bin
Description: 磁盘清理助手（Linux 版）
 安全清理系统与应用缓存、开发工具缓存与数据隐私残留，
 内置保护名单、备份区与台账，删除可恢复。
 CLI 与图形界面双形态。
EOF

cat > "$STAGE/deb/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
fi
exit 0
EOF
chmod 755 "$STAGE/deb/DEBIAN/postinst"

# 复制内容
cp -a "$STAGE/root/." "$STAGE/deb/"

dpkg-deb --build --root-owner-group "$STAGE/deb" "$DEB" >/dev/null
echo "== deb built =="
ls -la "$DEB"
dpkg-deb --info "$DEB" | head -14
dpkg-deb --contents "$DEB" 2>/dev/null | head -6 || true
