#!/usr/bin/env bash
# Phase 0：建出在 Android 上跑 converter 需要的三樣東西，放到 $OUT（預設 /tmp/mt2-phase0-build）。
#   pub-bionic/   converter 的 linux-bionic-arm64 self-contained 發布（Mono runtime）
#   libnotag.so   LD_PRELOAD，關掉 bionic 的 heap pointer tagging
#   ssl/          NDK 編的 OpenSSL 3（libssl.so、libcrypto.so）
# 不含任何遊戲檔；converter 原始碼複製到 $OUT 再建，不動 unity-port 的 bin/obj。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
CONVERTER="${CONVERTER:-$HOME/GitHub/unity-port/unity-cs2ts/converter}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
NDK="${NDK:-/opt/android-sdk/ndk/30.0.16248370}"
OUT="${OUT:-/tmp/mt2-phase0-build}"
OPENSSL_VER="${OPENSSL_VER:-3.5.9}"
OPENSSL_SHA256="${OPENSSL_SHA256:-603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a}"
API=31
TC="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin"

mkdir -p "$OUT"

echo "== converter：linux-bionic-arm64 self-contained =="
rm -rf "$OUT/converter" "$OUT/pub-bionic"
rsync -a --exclude bin --exclude obj "$CONVERTER/" "$OUT/converter/"
"$DOTNET" publish "$OUT/converter" -c Release -r linux-bionic-arm64 --self-contained -o "$OUT/pub-bionic"

echo "== libnotag.so =="
"$TC/aarch64-linux-android$API-clang" -shared -fPIC -O2 -o "$OUT/libnotag.so" "$HERE/notag.c"

echo "== OpenSSL $OPENSSL_VER =="
SRC="$OUT/openssl-src"
rm -rf "$SRC" "$OUT/ssl"
mkdir -p "$SRC" "$OUT/ssl"
curl -sSL -o "$SRC/openssl.tar.gz" "https://github.com/openssl/openssl/releases/download/openssl-$OPENSSL_VER/openssl-$OPENSSL_VER.tar.gz"
echo "$OPENSSL_SHA256  $SRC/openssl.tar.gz" | sha256sum -c -
tar xzf "$SRC/openssl.tar.gz" -C "$SRC"
(
  cd "$SRC/openssl-$OPENSSL_VER"
  export ANDROID_NDK_ROOT="$NDK" PATH="$TC:$PATH"
  ./Configure android-arm64 -D__ANDROID_API__=$API shared no-tests no-docs --prefix="$SRC/install" > /dev/null
  make -j"$(nproc)" > /dev/null
  make install_sw > /dev/null
)
cp "$SRC/install/lib/libssl.so" "$SRC/install/lib/libcrypto.so" "$OUT/ssl/"

echo "== 完成：$OUT =="
ls -la "$OUT/libnotag.so" "$OUT/ssl"
du -sh "$OUT/pub-bionic"
