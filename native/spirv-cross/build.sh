#!/usr/bin/env bash
# SPIRV-Cross → libspirv-cross-c-shared（上游的 C API，給 C# 的 P/Invoke）。
#
# 用法：native/spirv-cross/build.sh [macos|android|all]（預設 all）
#   macos    macOS arm64 的 dylib，桌面比對用
#   android  Android arm64 的 .so（NDK，API 26，libc++ static，16 KB 分頁）
# 輸出：native/spirv-cross/build/out/<osx-arm64|android-arm64>/
#
# 原始碼在建置時抓上游 tag vulkan-sdk-1.4.363.0（= Homebrew 的 spirv-cross 1.4.363.0），放 build/，不進版控。授權 Apache-2.0。
# 只開 GLSL 與 JSON reflection 兩個後端（shader 轉換只用到這兩個），CMake Release，跟 Homebrew 版一樣。
# 需要：git、cmake、ninja、Xcode 的 clang（macos）、NDK 28（android，路徑用 ANDROID_NDK_HOME 指定）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
B="$HERE/build"
SRC="$B/src"
TAG=vulkan-sdk-1.4.363.0
NDK="${ANDROID_NDK_HOME:-$HOME/Library/Android/sdk/ndk/28.2.13676358}"
API=26
OPTS=(
  -G Ninja -DCMAKE_BUILD_TYPE=Release
  -DSPIRV_CROSS_SHARED=ON -DSPIRV_CROSS_STATIC=OFF -DSPIRV_CROSS_CLI=OFF -DSPIRV_CROSS_ENABLE_TESTS=OFF
  -DSPIRV_CROSS_ENABLE_GLSL=ON -DSPIRV_CROSS_ENABLE_REFLECT=ON
  -DSPIRV_CROSS_ENABLE_HLSL=OFF -DSPIRV_CROSS_ENABLE_MSL=OFF -DSPIRV_CROSS_ENABLE_CPP=OFF -DSPIRV_CROSS_ENABLE_UTIL=OFF
)

fetch() {
  if [[ ! -d "$SRC/.git" ]]; then
    mkdir -p "$B"
    git clone -q --depth 1 --branch "$TAG" https://github.com/KhronosGroup/SPIRV-Cross.git "$SRC"
  fi
  [[ "$(git -C "$SRC" describe --tags)" == "$TAG" ]] || { echo "SPIRV-Cross 不在 $TAG" >&2; exit 1; }
}

build_macos() {
  local dir="$B/cmake-osx-arm64" out="$B/out/osx-arm64"
  cmake -S "$SRC" -B "$dir" "${OPTS[@]}" -DCMAKE_OSX_ARCHITECTURES=arm64 > "$B/cmake-osx-arm64.log"
  ninja -C "$dir" spirv-cross-c-shared > "$B/ninja-osx-arm64.log"
  mkdir -p "$out"
  # 版本號的 dylib 複製成不帶版本的名稱（P/Invoke 找 libspirv-cross-c-shared.dylib）
  cp "$(find "$dir" -maxdepth 1 -name 'libspirv-cross-c-shared.*.*.*.dylib' | head -1)" "$out/libspirv-cross-c-shared.dylib"
  install_name_tool -id @rpath/libspirv-cross-c-shared.dylib "$out/libspirv-cross-c-shared.dylib"
  echo "$out/libspirv-cross-c-shared.dylib"
}

build_android() {
  # 發布用：每次都從頭編（不沿用 ninja 的增量產物），編前編後的原始碼摘要要相同，.source 才記得住編進 .so 的是哪份原始碼
  rm -f "$B/out/android-arm64/libspirv-cross-c-shared.so" "$B/out/android-arm64/libspirv-cross-c-shared.so.source"
  rm -rf "$B/cmake-android-arm64"
  local src_before; src_before="$(python3 -I "$HERE/../../tools/release/srcdigest.py" "$SRC")"
  local dir="$B/cmake-android-arm64" out="$B/out/android-arm64"
  cmake -S "$SRC" -B "$dir" "${OPTS[@]}" \
    -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" -DANDROID_ABI=arm64-v8a \
    -DANDROID_PLATFORM=android-$API -DANDROID_STL=c++_static \
    -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384" > "$B/cmake-android-arm64.log"
  ninja -C "$dir" spirv-cross-c-shared > "$B/ninja-android-arm64.log"
  mkdir -p "$out"
  cp "$dir/libspirv-cross-c-shared.so" "$out/"
  "$NDK/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-strip" --strip-unneeded "$out/libspirv-cross-c-shared.so"
  # 給 tools/release/package.sh 核對：這份 .so 的 SHA-256、上游原始碼（build/src）與這個資料夾（不含 build/）的摘要
  [[ "$(python3 -I "$HERE/../../tools/release/srcdigest.py" "$SRC")" == "$src_before" ]] || { echo "編譯期間 $SRC 被改過，重跑一次" >&2; exit 1; }
  printf '%s %s %s\n' "$(shasum -a 256 "$out/libspirv-cross-c-shared.so" | cut -d" " -f1)" \
    "$src_before" "$(python3 -I "$HERE/../../tools/release/srcdigest.py" "$HERE" --exclude build)" > "$out/libspirv-cross-c-shared.so.source"
  echo "$out/libspirv-cross-c-shared.so"
}

target="${1:-all}"
fetch
case "$target" in
  macos) build_macos ;;
  android) build_android ;;
  all) build_macos; build_android ;;
  *) echo "用法：$0 [macos|android|all]" >&2; exit 2 ;;
esac
