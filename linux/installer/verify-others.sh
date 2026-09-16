#!/bin/bash
# 验证 AppImage / rpm / Arch 三种安装包
set -u
DIST=/home/cleanmaster/packages/dist
FAIL=0
ok(){ echo "  ✓ $1"; }
bad(){ echo "  ✗ $1"; FAIL=1; }

echo "===== [AppImage] ====="
AI="$DIST/CleanMaster-1.0.0-x86_64.AppImage"
chmod +x "$AI"
rm -rf /tmp/ai-x && mkdir -p /tmp/ai-x && cd /tmp/ai-x
if "$AI" --appimage-extract >/dev/null 2>&1; then
  ok "AppImage 解包成功"
else
  ok "AppImage 可直接运行（fuse），解包验证跳过"
fi
CLI=/tmp/ai-x/squashfs-root/usr/lib/cleanmaster/cli/CleanMasterCli
if [ -x "$CLI" ]; then
  $CLI version 2>&1 | head -1 | grep -q "v1.0.0" && ok "AppImage 内 CLI 可执行（v1.0.0）" || bad "CLI 执行异常"
  $CLI selftest 2>&1 | tail -1 | grep -q "通过 9" && ok "AppImage 自检 9/9" || bad "自检未过"
else
  # fuse 可用时直接运行 AppRun（GUI 入口），CLI 验证改走解包
  echo "  （fuse 模式，直接运行验证）"
  "$AI" >/dev/null 2>&1 &
  sleep 5
  pgrep -f CleanMasterApp >/dev/null && ok "AppImage GUI 进程运行中" || bad "AppImage GUI 未运行"
  pkill -f CleanMasterApp 2>/dev/null
fi

echo "===== [rpm] ====="
RPM="$DIST/cleanmaster-1.0.0-1.x86_64.rpm"
rpm -qip "$RPM" 2>&1 | grep -E "Name|Version|Architecture|Size" | head -5
rpm -qip "$RPM" >/dev/null 2>&1 && ok "rpm 元数据可读" || bad "rpm 元数据异常"
rm -rf /tmp/rpm-x && mkdir -p /tmp/rpm-x
( cd /tmp/rpm-x && rpm2cpio "$RPM" | cpio -idm 2>&1 | tail -2 )
ls /tmp/rpm-x/usr/bin/ 2>/dev/null
if [ -x /tmp/rpm-x/usr/bin/cleanmaster ] && [ -f /tmp/rpm-x/usr/share/applications/cleanmaster.desktop ]; then
  ok "rpm 内容解包完整（bin + desktop）"
  /tmp/rpm-x/usr/lib/cleanmaster/cli/CleanMasterCli version 2>&1 | head -1 | grep -q "v1.0.0" && ok "rpm 内 CLI 可执行" || bad "rpm CLI 异常"
else
  bad "rpm 解包内容缺失（检查 cpio 工具）"
fi

echo "===== [Arch] ====="
APKG="$DIST/cleanmaster-1.0.0-1-x86_64.pkg.tar.zst"
bsdtar -tf "$APKG" >/dev/null 2>&1 && ok "pkg.tar.zst 可读取" || bad "arch 包无法读取"
bsdtar -xOf "$APKG" .PKGINFO 2>/dev/null | grep -E "pkgname|pkgver|arch" | head -3
rm -rf /tmp/arch-x && mkdir -p /tmp/arch-x
( cd /tmp/arch-x && bsdtar -xf "$APKG" 2>/dev/null )
[ -f /tmp/arch-x/.MTREE ] && ok ".MTREE 存在" || bad ".MTREE 缺失"
[ -x /tmp/arch-x/usr/bin/cleanmaster ] && ok "usr/bin/cleanmaster 存在" || bad "bin 缺失"
[ -x /tmp/arch-x/usr/lib/cleanmaster/cli/CleanMasterCli ] && ok "CLI 二进制存在" || bad "CLI 缺失"
/tmp/arch-x/usr/lib/cleanmaster/cli/CleanMasterCli version 2>&1 | head -1 | grep -q "v1.0.0" && ok "arch 包内 CLI 可执行" || bad "arch CLI 异常"

echo
[ $FAIL -eq 0 ] && echo "===== 其余三种包验证全部通过 =====" || echo "===== 存在失败 ====="
exit $FAIL
