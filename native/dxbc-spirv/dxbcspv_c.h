/* dxbc-spirv 的薄 C 介面：給 C# 用 P/Invoke 呼叫。
 *
 * dxbcspv_compile 做的事跟 dxbc-spirv 的 tools/dxbc_compiler.cpp 以
 * `dxbc_compiler <檔案> --spv <輸出> --cbv-array` 執行時相同（順序、選項都照它）；
 * dxbcspv_disasm 照 tools/dxbc_disasm.cpp 把整個 shader 反組譯，回傳 stdout 的內容
 *（Python 版先跑 dxbc_disasm，存成 .asm，失敗就算轉換失敗）。
 *
 * 回傳 0 是成功。失敗時 *error 是 malloc 的字串（log 訊息），用 dxbcspv_free 釋放。
 * 函式可以在不同執行緒同時呼叫：dxbc-spirv 的 logger 是 thread_local。 */
#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define DXBCSPV_API __declspec(dllexport)
#else
#define DXBCSPV_API __attribute__((visibility("default")))
#endif

/* dxbcspv_compile 的旗標 */
#define DXBCSPV_CBV_ARRAY 1u /* --cbv-array：cbuffer 一律輸出 vec4 陣列（本機 patch） */

DXBCSPV_API int dxbcspv_compile(const void* data, size_t size, uint32_t flags,
  void** spirv, size_t* spirv_size, char** error);

DXBCSPV_API int dxbcspv_disasm(const void* data, size_t size, char** text, char** error);

DXBCSPV_API void dxbcspv_free(void* ptr);

#ifdef __cplusplus
}
#endif
