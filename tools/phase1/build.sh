#!/usr/bin/env bash
# Phase 1 測試 app：不用 Gradle，aapt2 → javac → d8 → zipalign → apksigner。
# 產物（APK、debug keystore）都放 $OUT（預設 /tmp/mt2-phase1-build），不進 repo。
#   $OUT/mt2-phase1.apk   lib/arm64-v8a/ 放 libcsport.so（launcher.c 編的執行檔）、
#                         converter 發布裡的 .so（Mono runtime、hostfxr、System.*.Native）與 OpenSSL
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SDK="${ANDROID_HOME:-/opt/android-sdk}"
BT="$SDK/build-tools/36.0.0"
PLATFORM="$SDK/platforms/android-36/android.jar"
NDK="${NDK:-$SDK/ndk/30.0.16248370}"
TC="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin"
OUT="${OUT:-/tmp/mt2-phase1-build}"
KEYSTORE="$OUT/debug.keystore"
BUILD0="${BUILD0:-/tmp/mt2-phase0-build}"   # tools/phase0/build.sh 的輸出：pub-bionic/、ssl/

rm -rf "$OUT/apk"
mkdir -p "$OUT/apk/classes" "$OUT/apk/dex" "$OUT/apk/stage/lib/arm64-v8a"

echo "== launcher（libcsport.so）=="
"$TC/aarch64-linux-android31-clang" -O2 -Wall -fPIE -pie \
  -o "$OUT/apk/stage/lib/arm64-v8a/libcsport.so" "$HERE/launcher.c"

echo "== runtime 與 OpenSSL 的 .so =="
cp "$BUILD0"/pub-bionic/*.so "$BUILD0"/ssl/libssl.so "$BUILD0"/ssl/libcrypto.so "$OUT/apk/stage/lib/arm64-v8a/"
ls "$OUT/apk/stage/lib/arm64-v8a"

echo "== aapt2 link =="
"$BT/aapt2" link -o "$OUT/apk/base.apk" -I "$PLATFORM" \
  --manifest "$HERE/AndroidManifest.xml" \
  --min-sdk-version 31 --target-sdk-version 36

echo "== javac + d8 =="
find "$HERE/src" -name '*.java' > "$OUT/apk/sources.txt"
javac --release 17 -classpath "$PLATFORM" -d "$OUT/apk/classes" @"$OUT/apk/sources.txt" 2>&1 \
  | grep -v 'system modules' || true
"$BT/d8" --release --min-api 31 --lib "$PLATFORM" --output "$OUT/apk/dex" \
  $(find "$OUT/apk/classes" -name '*.class')

echo "== 打包、簽章 =="
cp "$OUT/apk/base.apk" "$OUT/apk/unsigned.apk"
(cd "$OUT/apk/dex" && zip -q "$OUT/apk/unsigned.apk" classes*.dex)
(cd "$OUT/apk/stage" && zip -q "$OUT/apk/unsigned.apk" lib/arm64-v8a/*.so)
"$BT/zipalign" -f -p 4 "$OUT/apk/unsigned.apk" "$OUT/apk/aligned.apk"
if [ ! -f "$KEYSTORE" ]; then
  keytool -genkeypair -keystore "$KEYSTORE" -storepass android -keypass android \
    -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 \
    -dname "CN=Android Debug,O=Android,C=US" > /dev/null 2>&1
fi
"$BT/apksigner" sign --ks "$KEYSTORE" --ks-pass pass:android --key-pass pass:android \
  --out "$OUT/mt2-phase1.apk" "$OUT/apk/aligned.apk"
"$BT/apksigner" verify "$OUT/mt2-phase1.apk"
unzip -l "$OUT/mt2-phase1.apk"
