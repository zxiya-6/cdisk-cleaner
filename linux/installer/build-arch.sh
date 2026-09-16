#!/bin/bash
# 构建 Arch .pkg.tar.zst 包（手工构造，无需 Arch 环境）
set -e
cd /home/cleanmaster/build/linux
STAGE=${STAGE_DIR:-installer/stage}
VERSION=1.0.0
DIST=/home/cleanmaster/packages/dist
mkdir -p "$DIST"

for tool in bsdtar zstd; do
  if ! command -v $tool >/dev/null 2>&1; then
    echo "installing $tool..."
    sudo apt-get install -y -qq libarchive-tools zstd >/dev/null 2>&1
    break
  fi
done

PKG="$STAGE/pkg-arch/cleanmaster-$VERSION-1-x86_64"
rm -rf "$STAGE/pkg-arch"
mkdir -p "$PKG"

# 内容
cp -a "$STAGE/root/." "$PKG/"

# .PKGINFO
SIZE=$(du -sb "$PKG" | cut -f1)
cat > "$PKG/.PKGINFO" <<EOF
pkgname = cleanmaster
pkgbase = cleanmaster
pkgver = $VERSION-1
pkgdesc = 磁盘清理助手（Linux 版）：安全清理系统与应用缓存，CLI + GUI
url = https://github.com/zxiya-6/cdisk-cleaner
builddate = $(date +%s)
packager = CleanMaster Team <dev@cleanmaster.local>
size = $SIZE
arch = x86_64
license = MIT
depend = gtk3
depend = libice
depend = libsm
depend = libx11
depend = libxrandr
depend = libxcursor
depend = libxkbcommon
depend = fontconfig
depend = noto-fonts-cjk
EOF

# .MTREE（bsdtar 生成）
( cd "$PKG" && bsdtar -czf .MTREE --format=mtree --options='!all,use-set,type,uid,gid,mode,time,size,md5,sha256,link' . )

# 打包
OUT="$DIST/cleanmaster-$VERSION-1-x86_64.pkg.tar.zst"
rm -f "$OUT"
( cd "$PKG" && tar -c . | zstd -19 -o "$OUT" )
echo "== arch pkg built =="
ls -la "$OUT"
zstd -l "$OUT" 2>/dev/null | head -3 || echo "(zstd 校验信息跳过)"
