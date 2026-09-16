#!/bin/bash
# Linux 版打包脚本：publish 阶段（STAGE_DIR 由 build-all.sh 提供，默认 installer/stage）
set -e
cd /home/cleanmaster/build/linux
STAGE=${STAGE_DIR:-installer/stage}
rm -rf "$STAGE"
mkdir -p "$STAGE/publish/cli" "$STAGE/publish/gui"

echo "== publish CLI (self-contained) =="
dotnet publish src/CleanMaster.Cli -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=false -o "$STAGE/publish/cli" -v q 2>&1 | tail -3

echo "== publish GUI (self-contained) =="
dotnet publish src/CleanMaster.App -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=false -o "$STAGE/publish/gui" -v q 2>&1 | tail -3

echo "== sizes =="
du -sh "$STAGE/publish/cli" "$STAGE/publish/gui"
ls "$STAGE/publish/cli" | head -8
echo "..."
ls "$STAGE/publish/gui" | head -8
echo "publish OK"
