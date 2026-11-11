#!/usr/bin/env bash
# dxbc-spirv → libdxbcspv（dxbcspv_c.h 的 C 介面，給 C# 的 P/Invoke）。
#
# 用法：native/dxbc-spirv/build.sh [macos|macos-ref|android|all]（預設 all）
#   macos      macOS arm64 的 dylib，桌面比對用；編譯選項跟 android 相同（-O2、assert 開著）
#   macos-ref  macOS arm64 的 dylib，選項照參考的命令列工具（meson 預設 buildtype debug：-O0、assert 開著）
#   android    Android arm64 的 .so（NDK，API 26，libc++ static，16 KB 分頁）
# 輸出：native/dxbc-spirv/build/out/<osx-arm64|osx-arm64-ref|android-arm64>/
#
# 原始碼在建置時抓：上游 commit c7f0697（子模組 SPIRV-Headers c8ad050），套用同資料夾的 dxbc-spirv-local.patch
#（關掉 FS 輸入寬度裁減、加 --cbv-array）。全部放 build/，不進版控。授權：dxbc-spirv MIT、SPIRV-Headers MIT。
# 需要：git、meson、ninja、Xcode 的 clang（macos）、NDK 28（android，路徑用 ANDROID_NDK_HOME 指定）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
B="$HERE/build"
SRC="$B/src"
REPO=https://github.com/doitsujin/dxbc-spirv.git
COMMIT=c7f069701227dcb800a01bd93e42f8001b7543e7
HEADERS_COMMIT=c8ad050fcb29e42a2f57d9f59e97488f465c436d
NDK="${ANDROID_NDK_HOME:-$HOME/Library/Android/sdk/ndk/28.2.13676358}"
API=26
DEFINES=(-DDXBC_SPV_ENABLE_SM3 -DDXBC_SPV_ENABLE_SM5 -DDXBC_SPV_ENABLE_SPIRV)

fetch() {
  if [[ ! -d "$SRC/.git" ]]; then
    mkdir -p "$B"
    git clone -q "$REPO" "$SRC"
  fi
  git -C "$SRC" checkout -q "$COMMIT"
  git -C "$SRC" submodule update -q --init
  local h
  h="$(git -C "$SRC/submodules/spirv_headers" rev-parse HEAD)"
  [[ "$h" == "$HEADERS_COMMIT" ]] || { echo "SPIRV-Headers 是 ${h}，不是 $HEADERS_COMMIT" >&2; exit 1; }
  if git -C "$SRC" apply --check "$HERE/dxbc-spirv-local.patch" 2>/dev/null; then
    git -C "$SRC" apply "$HERE/dxbc-spirv-local.patch"
  else
    git -C "$SRC" apply --check -R "$HERE/dxbc-spirv-local.patch"  # 已經套用過
  fi
}

# meson 只編靜態庫 libdxbc_spv.a；C 介面另外編、一起連成共享函式庫
lib() {
  local name="$1"; shift
  local dir="$B/meson-$name"
  [[ -f "$dir/build.ninja" ]] || meson setup "$dir" "$SRC" -Ddefault_library=static -Denable_tools=false -Denable_tests=false "$@" > /dev/null
  ninja -C "$dir" libdxbc_spv.a > "$B/ninja-$name.log"
  echo "$dir/libdxbc_spv.a"
}

build_macos() {  # $1 = 輸出名稱，$2 = 編譯 C 介面的最佳化選項，其餘給 meson
  local name="$1" opt="$2"; shift 2
  local a; a="$(lib "$name" "$@")"
  local out="$B/out/$name"
  mkdir -p "$out"
  printf '_dxbcspv_compile\n_dxbcspv_disasm\n_dxbcspv_free\n' > "$B/exports-macos.txt"
  c++ -std=c++17 -fno-exceptions -fPIC $opt "${DEFINES[@]}" -I "$SRC" -c "$HERE/dxbcspv_c.cpp" -o "$B/dxbcspv_c-$name.o"
  c++ -shared -o "$out/libdxbcspv.dylib" "$B/dxbcspv_c-$name.o" "$a" \
    -install_name @rpath/libdxbcspv.dylib -Wl,-exported_symbols_list,"$B/exports-macos.txt"
  echo "$out/libdxbcspv.dylib"
}

build_android() {
  # 發布用：每次都從頭編（不沿用 ninja 的增量產物），編前編後的原始碼摘要要相同，.source 才記得住編進 .so 的是哪份原始碼
  rm -f "$B/out/android-arm64/libdxbcspv.so" "$B/out/android-arm64/libdxbcspv.so.source"
  rm -rf "$B/meson-android-arm64"
  local src_before; src_before="$(python3 -I "$HERE/../../tools/release/srcdigest.py" "$SRC")"
  local tc="$NDK/toolchains/llvm/prebuilt/darwin-x86_64/bin"
  [[ -x "$tc/aarch64-linux-android$API-clang++" ]] || { echo "找不到 NDK：$NDK" >&2; exit 1; }
  local cross="$B/android-cross.ini"
  cat > "$cross" <<EOF
[binaries]
c = '$tc/aarch64-linux-android$API-clang'
cpp = '$tc/aarch64-linux-android$API-clang++'
ar = '$tc/llvm-ar'
strip = '$tc/llvm-strip'

[host_machine]
system = 'android'
cpu_family = 'aarch64'
cpu = 'armv8-a'
endian = 'little'
EOF
  local a; a="$(lib android-arm64 --cross-file "$cross" -Dbuildtype=release -Doptimization=2 -Db_ndebug=false)"
  local out="$B/out/android-arm64"
  mkdir -p "$out"
  printf '{\n  global: dxbcspv_*;\n  local: *;\n};\n' > "$B/exports-android.map"
  "$tc/aarch64-linux-android$API-clang++" -std=c++17 -fno-exceptions -fPIC -O2 "${DEFINES[@]}" -I "$SRC" \
    -c "$HERE/dxbcspv_c.cpp" -o "$B/dxbcspv_c-android.o"
  "$tc/aarch64-linux-android$API-clang++" -shared -o "$out/libdxbcspv.so" "$B/dxbcspv_c-android.o" "$a" \
    -static-libstdc++ -Wl,--version-script="$B/exports-android.map" -Wl,--no-undefined \
    -Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384 -Wl,-soname,libdxbcspv.so
  "$tc/llvm-strip" --strip-unneeded "$out/libdxbcspv.so"
  # 給 tools/release/package.sh 核對：這份 .so 的 SHA-256、上游原始碼（build/src）與這個資料夾（不含 build/）的摘要
  [[ "$(python3 -I "$HERE/../../tools/release/srcdigest.py" "$SRC")" == "$src_before" ]] || { echo "編譯期間 $SRC 被改過，重跑一次" >&2; exit 1; }
  printf '%s %s %s\n' "$(shasum -a 256 "$out/libdxbcspv.so" | cut -d" " -f1)" \
    "$src_before" "$(python3 -I "$HERE/../../tools/release/srcdigest.py" "$HERE" --exclude build)" > "$out/libdxbcspv.so.source"
  echo "$out/libdxbcspv.so"
}

target="${1:-all}"
fetch
case "$target" in
  macos) build_macos osx-arm64 -O2 -Dbuildtype=release -Doptimization=2 -Db_ndebug=false ;;
  macos-ref) build_macos osx-arm64-ref "-O0 -g" -Dbuildtype=debug ;;
  android) build_android ;;
  all)
    build_macos osx-arm64 -O2 -Dbuildtype=release -Doptimization=2 -Db_ndebug=false
    build_macos osx-arm64-ref "-O0 -g" -Dbuildtype=debug
    build_android
    ;;
  *) echo "用法：$0 [macos|macos-ref|android|all]" >&2; exit 2 ;;
esac
