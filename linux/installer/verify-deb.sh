#!/bin/bash
# 安装包验证：.deb 全流程（安装 → CLI → GUI → 清理沙箱 → 卸载）
set -u
DIST=/home/cleanmaster/packages/dist
DEB="$DIST/cleanmaster_1.0.0_amd64.deb"
FAIL=0
ok(){ echo "  ✓ $1"; }
bad(){ echo "  ✗ $1"; FAIL=1; }

echo "===== [deb] 1. 安装 ====="
sudo apt-get install -y -qq "$DEB" >/tmp/deb-install.log 2>&1
if command -v cleanmaster >/dev/null 2>&1 && command -v cleanmaster-gui >/dev/null 2>&1; then
  ok "cleanmaster / cleanmaster-gui 已安装到 PATH"
else
  bad "命令未安装"; tail -5 /tmp/deb-install.log
fi
[ -f /usr/share/applications/cleanmaster.desktop ] && ok ".desktop 入口存在" || bad ".desktop 缺失"
[ -f /usr/share/icons/hicolor/256x256/apps/cleanmaster.png ] && ok "图标已安装" || bad "图标缺失"

echo "===== [deb] 2. CLI 冒烟 ====="
V=$(cleanmaster version 2>&1)
echo "$V" | head -2
echo "$V" | grep -q "v1.0.0" && ok "version 正常" || bad "version 异常"
S=$(cleanmaster selftest 2>&1 | tail -1)
echo "$S"
echo "$S" | grep -q "通过 9" && ok "自检 9/9" || bad "自检未全过"

echo "===== [deb] 3. 清理沙箱往返 ====="
export CC5_DATA_DIR=/tmp/cc5-deb-test
export XDG_DATA_HOME=/tmp/cc5-deb-test-xdg
mkdir -p /tmp/cc5-deb-src
head -c 2048 /dev/urandom > /tmp/cc5-deb-src/a.tmp
head -c 1024 /dev/urandom > /tmp/cc5-deb-src/b.tmp
C=$(cleanmaster clean --category user-temp --mode backup --yes --set-root user-temp=/tmp/cc5-deb-src 2>&1)
echo "$C" | tail -3
[ ! -e /tmp/cc5-deb-src/a.tmp ] && ok "文件已移入备份区" || bad "清理未生效"
SID=$(cleanmaster backup-list --json /tmp/cc5-deb-sessions.json >/dev/null 2>&1; grep -oE '"id": "[^"]+"' /tmp/cc5-deb-sessions.json | head -1 | cut -d'"' -f4)
[ -n "$SID" ] && ok "备份会话存在: $SID" || bad "无备份会话"
R=$(cleanmaster backup-restore --session "$SID" 2>&1 | tail -2)
echo "$R"
[ -e /tmp/cc5-deb-src/a.tmp ] && ok "文件已恢复" || bad "恢复失败"
L=$(cleanmaster ledger --limit 5 2>&1 | tail -2)
echo "$L"
unset CC5_DATA_DIR XDG_DATA_HOME

echo "===== [deb] 4. GUI 启动（WSLg）====="
pkill -f "net8.0/CleanMasterApp" 2>/dev/null
/usr/lib/cleanmaster/gui/CleanMasterApp >/tmp/deb-gui.log 2>&1 &
sleep 6
if pgrep -f "CleanMasterApp" >/dev/null; then
  ok "GUI 进程运行中"
else
  bad "GUI 未运行"; cat /tmp/deb-gui.log | head -3
fi
export DISPLAY=:0
WID=$(xdotool search --name "磁盘" 2>/dev/null | head -1)
if [ -n "$WID" ]; then
  ok "窗口已创建"
  xwd -id "$WID" -out /tmp/deb-gui.xwd 2>/dev/null && convert /tmp/deb-gui.xwd /mnt/d/dev/cdisk-cleaner/linux/docs/gui-deb-installed.png 2>/dev/null && ok "已抓取安装后 GUI 截图"
else
  bad "未找到窗口"
fi

echo "===== [deb] 5. 卸载 ====="
sudo apt-get remove -y -qq cleanmaster >/tmp/deb-remove.log 2>&1
if ! command -v cleanmaster >/dev/null 2>&1; then
  ok "命令已卸载"
else
  bad "卸载不彻底"
fi

rm -rf /tmp/cc5-deb-src /tmp/cc5-deb-test /tmp/cc5-deb-test-xdg /tmp/cc5-deb-sessions.json
echo
[ $FAIL -eq 0 ] && echo "===== deb 验证全部通过 =====" || echo "===== deb 验证存在失败 ====="
exit $FAIL
