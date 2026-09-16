#!/bin/bash
# 构造四种发行版共用的包内容根目录（usr/ 布局）
set -e
cd /home/cleanmaster/build/linux
STAGE=${STAGE_DIR:-installer/stage}
ROOT="$STAGE/root"
VERSION=1.0.0

rm -rf "$ROOT"
mkdir -p "$ROOT/usr/bin" \
         "$ROOT/usr/lib/cleanmaster/cli" \
         "$ROOT/usr/lib/cleanmaster/gui" \
         "$ROOT/usr/share/applications" \
         "$ROOT/usr/share/icons/hicolor/256x256/apps" \
         "$ROOT/usr/share/icons/hicolor/128x128/apps" \
         "$ROOT/usr/share/icons/hicolor/64x64/apps" \
         "$ROOT/usr/share/icons/hicolor/48x48/apps" \
         "$ROOT/usr/share/doc/cleanmaster" \
         "$ROOT/usr/share/metainfo"

# 1. 可执行产物
cp -a "$STAGE/publish/cli/." "$ROOT/usr/lib/cleanmaster/cli/"
cp -a "$STAGE/publish/gui/." "$ROOT/usr/lib/cleanmaster/gui/"
chmod -R a+rX "$ROOT/usr/lib/cleanmaster"

# 2. 启动包装脚本
cat > "$ROOT/usr/bin/cleanmaster" <<'EOF'
#!/bin/sh
exec /usr/lib/cleanmaster/cli/CleanMasterCli "$@"
EOF
cat > "$ROOT/usr/bin/cleanmaster-gui" <<'EOF'
#!/bin/sh
exec /usr/lib/cleanmaster/gui/CleanMasterApp "$@"
EOF
chmod 755 "$ROOT/usr/bin/cleanmaster" "$ROOT/usr/bin/cleanmaster-gui"

# 3. 图标
cp installer/assets/icon/256x256/apps/cleanmaster.png "$ROOT/usr/share/icons/hicolor/256x256/apps/"
cp installer/assets/icon/128x128/apps/cleanmaster.png "$ROOT/usr/share/icons/hicolor/128x128/apps/"
cp installer/assets/icon/64x64/apps/cleanmaster.png "$ROOT/usr/share/icons/hicolor/64x64/apps/"
cp installer/assets/icon/48x48/apps/cleanmaster.png "$ROOT/usr/share/icons/hicolor/48x48/apps/"

# 4. desktop 入口
cat > "$ROOT/usr/share/applications/cleanmaster.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=磁盘清理助手
Name[en]=CleanMaster
GenericName=磁盘清理工具
Comment=安全清理系统与应用缓存，释放磁盘空间
Comment[en]=Safely clean system and application caches
Exec=cleanmaster-gui
Icon=cleanmaster
Terminal=false
Categories=Utility;System;FileTools;
Keywords=clean;disk;cache;storage;清理;磁盘;
StartupNotify=true
EOF

# 5. 文档
cp -r docs/*.md docs/*.png docs/*.html "$ROOT/usr/share/doc/cleanmaster/" 2>/dev/null || true
cat > "$ROOT/usr/share/doc/cleanmaster/README" <<'EOF'
磁盘清理助手（Linux 版）v1.0.0

CLI:   cleanmaster <命令>      （scan/clean/backup-restore/ledger/selftest 等）
GUI:   cleanmaster-gui         （扫描清理 / 备份区 / 台账 / 规则 / 自检 / 关于）

数据目录：$XDG_DATA_HOME/cleanmaster（默认 ~/.local/share/cleanmaster）
规则目录：$XDG_CONFIG_HOME/cleanmaster（默认 ~/.config/cleanmaster）
详见 /usr/share/doc/cleanmaster/ 下文档。
EOF

echo "root content built:"
du -sh "$ROOT"
