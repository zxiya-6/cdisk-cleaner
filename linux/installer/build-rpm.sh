#!/bin/bash
# 构建 Fedora .rpm 包（rpmbuild）
set -e
cd /home/cleanmaster/build/linux
STAGE=${STAGE_DIR:-installer/stage}
VERSION=1.0.0
DIST=/home/cleanmaster/packages/dist
mkdir -p "$DIST"

if ! command -v rpmbuild >/dev/null 2>&1; then
  echo "installing rpm..."
  sudo apt-get install -y -qq rpm >/dev/null 2>&1
fi

RPMTOP=/home/cleanmaster/packages/rpmtop
rm -rf "$RPMTOP"
mkdir -p "$RPMTOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}

cat > "$RPMTOP/SPECS/cleanmaster.spec" <<EOF
Name:           cleanmaster
Version:        $VERSION
Release:        1%{?dist}
Summary:        磁盘清理助手（Linux 版）安全清理系统与应用缓存
License:        MIT
URL:            https://github.com/zxiya-6/cdisk-cleaner
BuildArch:      x86_64
Requires:       gtk3, libICE, libSM, libX11, libxkbcommon, fontconfig, dejavu-sans-fonts
Requires:       libXrandr, libXcursor

%description
磁盘清理助手（Linux 版）：安全清理系统缓存、桌面应用缓存、开发工具缓存与数据隐私残留。
内置保护名单、备份区与台账，删除可恢复。CLI 与图形界面双形态。

%install
rm -rf %{buildroot}
mkdir -p %{buildroot}
cp -a $STAGE/root/. %{buildroot}/

%files
/usr/bin/cleanmaster
/usr/bin/cleanmaster-gui
/usr/lib/cleanmaster
/usr/share/applications/cleanmaster.desktop
/usr/share/icons/hicolor
/usr/share/doc/cleanmaster
/usr/share/metainfo

%post
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
fi

%changelog
* Wed Sep 16 2026 CleanMaster Team <dev@cleanmaster.local> - 1.0.0-1
- Linux 版首个发布：CLI + Avalonia GUI，四发行版打包
EOF

rpmbuild --define "_topdir $RPMTOP" -bb "$RPMTOP/SPECS/cleanmaster.spec" >/dev/null 2>&1 || {
  rpmbuild --define "_topdir $RPMTOP" -bb "$RPMTOP/SPECS/cleanmaster.spec" 2>&1 | tail -20
  exit 1
}

RPM=$(find "$RPMTOP/RPMS" -name "*.rpm" | head -1)
cp "$RPM" "$DIST/"
echo "== rpm built =="
ls -la "$DIST/"*.rpm
rpm -qip "$DIST/"*.rpm 2>/dev/null | head -8 || echo "(rpm 工具无法读取，忽略)"
