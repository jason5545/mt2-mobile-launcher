// LD_PRELOAD 用：在 main 之前關掉 bionic 的 heap pointer tagging（TBI）。
// linux-bionic 的 .NET runtime 是 Mono，會把 0xb4… 開頭的 tagged pointer 位移後再用，啟動時 SIGSEGV。
#include <malloc.h>

__attribute__((constructor(101))) static void notag(void) {
    mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL, M_HEAP_TAGGING_LEVEL_NONE);
}
