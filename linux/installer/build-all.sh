#!/bin/bash
# 一键构建四种发行版安装包（产物与中间文件均在 WSL 原生磁盘，不受 rsync 清理影响）
set -e
cd /home/cleanmaster/build/linux
export STAGE_DIR=/home/cleanmaster/packages/stage
DIST=/home/cleanmaster/packages/dist
mkdir -p "$STAGE_DIR" "$DIST"
rm -f "$DIST"/*.deb "$DIST"/*.rpm "$DIST"/*.pkg.tar.zst "$DIST"/*.AppImage

echo "########## 1/5 publish ##########"
bash installer/publish.sh 2>&1 | tail -2

echo "########## 2/5 .deb ##########"
bash installer/build-deb.sh 2>&1 | tail -4

echo "########## 3/5 .rpm ##########"
bash installer/build-rpm.sh 2>&1 | tail -3

echo "########## 4/5 Arch .pkg.tar.zst ##########"
bash installer/build-arch.sh 2>&1 | tail -3

echo "########## 5/5 AppImage ##########"
bash installer/build-appimage.sh 2>&1 | tail -4

echo
echo "===== 全部产物 ====="
ls -la /home/cleanmaster/packages/dist/
