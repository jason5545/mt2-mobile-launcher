#!/usr/bin/env bash
# FreeType → libfreetype（importer 的 glyphs 步驟用 P/Invoke 呼叫）。
#
# 用法：native/freetype/build.sh [macos|android|all]（預設 all）
#   macos    macOS arm64 的 dylib，桌面比對用
#   android  Android arm64 的 .so（NDK，API 26，16 KB 分頁）
# 輸出：native/freetype/build/out/<osx-arm64|android-arm64>/
#
# 原始碼在建置時抓上游 tag VER-2-13-2（freetype-py 2.5.1 綁的就是 2.13.2），放 build/，不進版控。
# 授權：FreeType License（FTL）或 GPLv2 擇一。
# 編譯選項照 freetype-py 2.5.1 的 setup-build-freetype.py：CMake Release、-O2、共享函式庫、
# 不找 PNG／BZip2／ZLIB／Brotli（gzip 用 FreeType 內附的 zlib）、ftoption.h 用預設值
#（TrueType bytecode interpreter v40）。差別只有一個：freetype-py 另外連了 HarfBuzz 8.3.0，這裡不連。
# HarfBuzz 只給 autohinter 用；importer 的 hinting 字形只有 Diceomancer 的 LiberationSans，
# 那套字有 TrueType 指令，走 bytecode interpreter，不經 autohinter（Phase 3 第 0 步查過）。
# 需要：git、cmake、ninja、Xcode 的 clang（macos）、NDK 28（android，路徑用 ANDROID_NDK_HOME 指定）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
B="$HERE/build"
SRC="$B/src"
TAG=VER-2-13-2
NDK="${ANDROID_NDK_HOME:-$HOME/Library/Android/sdk/ndk/28.2.13676358}"
API=26
OPTS=(
  -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_FLAGS=-O2 -DBUILD_SHARED_LIBS=ON
  -DCMAKE_POLICY_VERSION_MINIMUM=3.5  # 2.13.2 的 CMakeLists 宣告的最低版本比 CMake 4 能接受的舊
  -DFT_DISABLE_HARFBUZZ=TRUE -DFT_DISABLE_PNG=TRUE -DFT_DISABLE_BZIP2=TRUE -DFT_DISABLE_ZLIB=TRUE -DFT_DISABLE_BROTLI=TRUE
)

fetch() {
  if [[ ! -d "$SRC/.git" ]]; then
    mkdir -p "$B"
    git clone -q --depth 1 --branch "$TAG" https://gitlab.freedesktop.org/freetype/freetype.git "$SRC"
  fi
  [[ "$(git -C "$SRC" describe --tags)" == "$TAG" ]] || { echo "FreeType 不在 $TAG" >&2; exit 1; }
}

build_macos() {
  local dir="$B/cmake-osx-arm64" out="$B/out/osx-arm64"
  cmake -S "$SRC" -B "$dir" "${OPTS[@]}" -DCMAKE_OSX_ARCHITECTURES=arm64 > "$B/cmake-osx-arm64.log"
  ninja -C "$dir" freetype > "$B/ninja-osx-arm64.log"
  mkdir -p "$out"
  cp "$(find "$dir" -maxdepth 1 -name 'libfreetype.*.*.*.dylib' | head -1)" "$out/libfreetype.dylib"
  install_name_tool -id @rpath/libfreetype.dylib "$out/libfreetype.dylib"
  echo "$out/libfreetype.dylib"
}

build_android() {
  # 發布用：每次都從頭編（不沿用 ninja 的增量產物），編前編後的原始碼摘要要相同，.source 才記得住編進 .so 的是哪份原始碼
  rm -f "$B/out/android-arm64/libfreetype.so" "$B/out/android-arm64/libfreetype.so.source"
  rm -rf "$B/cmake-android-arm64"
  local src_before; src_before="$(python3 -I "$HERE/../../tools/release/srcdigest.py" "$SRC")"
  local dir="$B/cmake-android-arm64" out="$B/out/android-arm64"
  cmake -S "$SRC" -B "$dir" "${OPTS[@]}" \
    -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" -DANDROID_ABI=arm64-v8a \
    -DANDROID_PLATFORM=android-$API \
    -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384" > "$B/cmake-android-arm64.log"
  ninja -C "$dir" freetype > "$B/ninja-android-arm64.log"
  mkdir -p "$out"
  cp "$dir/libfreetype.so" "$out/"
  "$NDK/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-strip" --strip-unneeded "$out/libfreetype.so"
  # 給 tools/release/package.sh 核對：這份 .so 的 SHA-256、上游原始碼（build/src）與這個資料夾（不含 build/）的摘要
  [[ "$(python3 -I "$HERE/../../tools/release/srcdigest.py" "$SRC")" == "$src_before" ]] || { echo "編譯期間 $SRC 被改過，重跑一次" >&2; exit 1; }
  printf '%s %s %s\n' "$(shasum -a 256 "$out/libfreetype.so" | cut -d" " -f1)" \
    "$src_before" "$(python3 -I "$HERE/../../tools/release/srcdigest.py" "$HERE" --exclude build)" > "$out/libfreetype.so.source"
  echo "$out/libfreetype.so"
}

target="${1:-all}"
fetch
case "$target" in
  macos) build_macos ;;
  android) build_android ;;
  all) build_macos; build_android ;;
  *) echo "用法：$0 [macos|android|all]" >&2; exit 2 ;;
esac
