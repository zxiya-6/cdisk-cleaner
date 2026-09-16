#!/bin/bash
# Linux 版 e2e 沙箱测试：不触碰真实用户数据，全部在 /tmp 沙箱内完成
set -u
cd /home/cleanmaster/build/linux
D=src/CleanMaster.Cli/bin/Debug/net8.0/CleanMasterCli.dll

SANDBOX=/tmp/cc5-e2e
export CC5_DATA_DIR="$SANDBOX/data"
export XDG_DATA_HOME="$SANDBOX/xdg"      # 回收站也进入沙箱
export HOME="$SANDBOX/home"              # 模拟用户主目录（受保护目录用）
mkdir -p "$HOME"
# 规则里 ~ 会展开为 $HOME，因此重建用户目录
mkdir -p "$SANDBOX/thumbnails/normal" "$SANDBOX/thumbnails/large" \
         "$SANDBOX/npm/_cacache" "$SANDBOX/tmp" "$SANDBOX/downloads" \
         "$HOME/Documents"

fail=0
ok() { echo "  ✓ $1"; }
bad() { echo "  ✗ $1"; fail=1; }

# ---- 造样本 ----
head -c 2048 /dev/urandom > "$SANDBOX/thumbnails/normal/a.png"
head -c 3072 /dev/urandom > "$SANDBOX/thumbnails/large/b.png"
head -c 5120 /dev/urandom > "$SANDBOX/npm/_cacache/x"
head -c 1024 /dev/urandom > "$SANDBOX/tmp/one.tmp"
head -c 1024 /dev/urandom > "$SANDBOX/tmp/two.log"
head -c 4096 /dev/urandom > "$SANDBOX/downloads/old-installer.deb"
touch -d "40 days ago" "$SANDBOX/downloads/old-installer.deb"
head -c 4096 /dev/urandom > "$SANDBOX/downloads/recent.deb"
touch -d "1 day ago" "$SANDBOX/downloads/recent.deb"
head -c 8192 /dev/urandom > "$HOME/Documents/secret.docx"

SR="--set-root thumbnails=$SANDBOX/thumbnails --set-root npm-cache=$SANDBOX/npm --set-root user-temp=$SANDBOX/tmp --set-root downloads-old-installers=$SANDBOX/downloads"

echo "== 1/6 扫描计数 =="
SCAN=$(dotnet "$D" scan --category thumbnails,npm-cache,user-temp,downloads-old-installers $SR 2>&1)
echo "$SCAN" | tail -8
echo "$SCAN" | grep -q "文件管理器缩略图缓存.*2" && ok "thumbnails 命中 2 个文件" || bad "thumbnails 计数错误"
echo "$SCAN" | grep -q "npm 缓存.*1" && ok "npm 命中 1 个文件" || bad "npm 计数错误"
echo "$SCAN" | grep -q "用户临时文件.*2" && ok "tmp 命中 2 个文件" || bad "tmp 计数错误"
echo "$SCAN" | grep -q "下载目录中的旧安装包.*1" && ok "downloads 仅命中 1 个（30 天前的旧包，recent.deb 被排除）" || bad "downloads 计数错误（应只 1 个）"

echo "== 2/6 真实清理（Delete 策略）=="
CL=$(dotnet "$D" clean --category thumbnails,npm-cache,user-temp --yes $SR 2>&1)
echo "$CL" | tail -6
[ ! -e "$SANDBOX/thumbnails/normal/a.png" ] && ok "thumbnails/a.png 已删除" || bad "a.png 仍存在"
[ ! -e "$SANDBOX/npm/_cacache/x" ] && ok "npm/x 已删除" || bad "npm/x 仍存在"
[ ! -e "$SANDBOX/tmp/one.tmp" ] && ok "tmp/one.tmp 已删除" || bad "one.tmp 仍存在"

echo "== 3/6 回收站策略 =="
CL2=$(dotnet "$D" clean --category downloads-old-installers --yes $SR 2>&1)
echo "$CL2" | tail -4
[ ! -e "$SANDBOX/downloads/old-installer.deb" ] && ok "旧安装包已移走" || bad "旧安装包仍存在"
[ -e "$SANDBOX/downloads/recent.deb" ] && ok "30 天内的安装包保留" || bad "recent.deb 被误删"
[ -f "$SANDBOX/xdg/Trash/files/old-installer.deb" ] && ok "回收站 files/ 中存在" || bad "回收站中未找到文件"
[ -f "$SANDBOX/xdg/Trash/info/old-installer.deb.trashinfo" ] && ok ".trashinfo 已写入" || bad ".trashinfo 缺失"

echo "== 4/6 备份区往返 =="
head -c 2048 /dev/urandom > "$SANDBOX/tmp/backup-me.tmp"
B1=$(dotnet "$D" clean --category user-temp --mode backup --yes $SR 2>&1)
echo "$B1" | tail -4
[ ! -e "$SANDBOX/tmp/backup-me.tmp" ] && ok "文件已移入备份区" || bad "文件未移走"
SID=$(dotnet "$D" backup-list --json "$SANDBOX/sessions.json" >/dev/null 2>&1; grep -oE '"id": "[^"]+"' "$SANDBOX/sessions.json" 2>/dev/null | head -1 | cut -d'"' -f4)
[ -n "$SID" ] && ok "备份区会话存在：$SID" || bad "未找到备份会话"
R1=$(dotnet "$D" backup-restore --session "$SID" --yes 2>&1)
echo "$R1" | tail -3
[ -e "$SANDBOX/tmp/backup-me.tmp" ] && ok "文件已恢复" || bad "恢复失败"

echo "== 5/6 台账 =="
LG=$(dotnet "$D" ledger --limit 20 2>&1)
echo "$LG" | tail -4
echo "$LG" | grep -q "清理" && ok "台账记录清理操作" || bad "台账无清理记录"
LE=$(dotnet "$D" ledger-export --format csv --out "$SANDBOX/ledger.csv" 2>&1)
[ -s "$SANDBOX/ledger.csv" ] && ok "CSV 导出成功（$(wc -l < "$SANDBOX/ledger.csv") 行）" || bad "CSV 导出失败"

echo "== 6/6 安全保护 =="
SP=$(dotnet "$D" scan --category thumbnails --set-root thumbnails=$HOME/Documents 2>&1)
echo "$SP" | grep -E "跳过|0 个文件|受保护|警告" | head -3
echo "$SP" | grep -q "0 个文件" && ok "受保护目录扫描结果为 0（未触碰 Documents）" || bad "Documents 被扫描出内容"
[ -e "$HOME/Documents/secret.docx" ] && ok "secret.docx 完好" || bad "受保护文件被破坏!"

echo
if [ $fail -eq 0 ]; then
  echo "===== E2E 全部通过 ====="
else
  echo "===== E2E 存在失败 ====="
fi
rm -rf "$SANDBOX"
exit $fail
