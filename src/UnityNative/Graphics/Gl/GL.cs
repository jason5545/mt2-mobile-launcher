// OpenGL ES 3.0 的函式表：宿主給一個 getProcAddress（桌面是 GLFW、Android 是 EGL），這裡拿函式指標。
// 桌面（macOS 的 GL 4.1 core）跟 GLES 3.0 用到的函式名稱一樣；shader 版本字串由 ShaderSource.Adapt 換。
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityNative.Gl
{
    public static unsafe class GL
    {
        public const uint DEPTH_BUFFER_BIT = 0x100, STENCIL_BUFFER_BIT = 0x400, COLOR_BUFFER_BIT = 0x4000;
        public const uint POINTS = 0, LINES = 1, LINE_STRIP = 3, TRIANGLES = 4, TRIANGLE_STRIP = 5;
        public const uint ZERO = 0, ONE = 1, SRC_COLOR = 0x300, ONE_MINUS_SRC_COLOR = 0x301, SRC_ALPHA = 0x302, ONE_MINUS_SRC_ALPHA = 0x303,
            DST_ALPHA = 0x304, ONE_MINUS_DST_ALPHA = 0x305, DST_COLOR = 0x306, ONE_MINUS_DST_COLOR = 0x307, SRC_ALPHA_SATURATE = 0x308;
        public const uint FUNC_ADD = 0x8006, FUNC_SUBTRACT = 0x800A, FUNC_REVERSE_SUBTRACT = 0x800B, MIN = 0x8007, MAX = 0x8008;
        public const uint NEVER = 0x200, LESS = 0x201, EQUAL = 0x202, LEQUAL = 0x203, GREATER = 0x204, NOTEQUAL = 0x205, GEQUAL = 0x206, ALWAYS = 0x207;
        public const uint KEEP = 0x1E00, REPLACE = 0x1E01, INCR = 0x1E02, DECR = 0x1E03, INVERT = 0x150A, INCR_WRAP = 0x8507, DECR_WRAP = 0x8508;
        public const uint FRONT = 0x404, BACK = 0x405, FRONT_AND_BACK = 0x408, CW = 0x900, CCW = 0x901;
        public const uint VIEWPORT = 0xBA2, CULL_FACE = 0xB44, DEPTH_TEST = 0xB71, STENCIL_TEST = 0xB90, BLEND = 0xBE2, SCISSOR_TEST = 0xC11;
        public const uint TEXTURE_2D = 0xDE1, TEXTURE_2D_ARRAY = 0x8C1A, TEXTURE0 = 0x84C0;
        public const uint TEXTURE_MAG_FILTER = 0x2800, TEXTURE_MIN_FILTER = 0x2801, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
        public const uint NEAREST = 0x2600, LINEAR = 0x2601, LINEAR_MIPMAP_LINEAR = 0x2703, LINEAR_MIPMAP_NEAREST = 0x2701, NEAREST_MIPMAP_NEAREST = 0x2700;
        public const uint REPEAT = 0x2901, CLAMP_TO_EDGE = 0x812F, MIRRORED_REPEAT = 0x8370;
        public const uint RGBA = 0x1908, RGBA8 = 0x8058, SRGB8_ALPHA8 = 0x8C43, UNSIGNED_BYTE = 0x1401, FLOAT = 0x1406, UNSIGNED_SHORT = 0x1403, UNSIGNED_INT = 0x1405, HALF_FLOAT = 0x140B;
        public const uint RGBA32F = 0x8814, R11F_G11F_B10F = 0x8C3A, RGB = 0x1907;
        public const uint RGBA16F = 0x881A, DEPTH24_STENCIL8 = 0x88F0, DEPTH_STENCIL_ATTACHMENT = 0x821A, DEPTH_COMPONENT24 = 0x81A6;
        public const uint UNPACK_ALIGNMENT = 0xCF5, PACK_ALIGNMENT = 0xD05;
        public const uint FRAGMENT_SHADER = 0x8B30, VERTEX_SHADER = 0x8B31, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82, INFO_LOG_LENGTH = 0x8B84;
        public const uint ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, UNIFORM_BUFFER = 0x8A11, STREAM_DRAW = 0x88E0, DYNAMIC_DRAW = 0x88E8, STATIC_DRAW = 0x88E4;
        public const uint FRAMEBUFFER_SRGB = 0x8DB9;
        public const uint FRAMEBUFFER = 0x8D40, READ_FRAMEBUFFER = 0x8CA8, DRAW_FRAMEBUFFER = 0x8CA9, RENDERBUFFER = 0x8D41, COLOR_ATTACHMENT0 = 0x8CE0, FRAMEBUFFER_COMPLETE = 0x8CD5;
        public const uint INVALID_INDEX = 0xFFFFFFFF;
        public const uint VENDOR = 0x1F00, RENDERER = 0x1F01, VERSION = 0x1F02, SHADING_LANGUAGE_VERSION = 0x8B8C;
        public const uint FRAMEBUFFER_BINDING = 0x8CA6, MAX_TEXTURE_SIZE = 0xD33;
        public const uint EXTENSIONS = 0x1F03, TEXTURE_BASE_LEVEL = 0x813C, TEXTURE_MAX_LEVEL = 0x813D;
        // 壓縮貼圖（docs/perf-battle.md「B 段」）：S3TC（GL_EXT_texture_compression_s3tc、_s3tc_srgb）與 BPTC（GL_EXT_texture_compression_bptc）。
        // DXT1 用有 alpha 的那個（D3D 的 BC1 一律把 3 色模式的第 4 色當透明黑，AssetRipper 解 PNG 也是）
        public const uint COMPRESSED_RGBA_S3TC_DXT1 = 0x83F1, COMPRESSED_RGBA_S3TC_DXT5 = 0x83F3;
        public const uint COMPRESSED_SRGB_ALPHA_S3TC_DXT1 = 0x8C4D, COMPRESSED_SRGB_ALPHA_S3TC_DXT5 = 0x8C4F;
        public const uint COMPRESSED_RGBA_BPTC_UNORM = 0x8E8C, COMPRESSED_SRGB_ALPHA_BPTC_UNORM = 0x8E8D, COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT = 0x8E8F;

        public static delegate* unmanaged<int, int, int, int, void> glViewport;
        public static delegate* unmanaged<int, int, int, int, void> glScissor;
        public static delegate* unmanaged<float, float, float, float, void> glClearColor;
        public static delegate* unmanaged<float, void> glClearDepthf;
        public static delegate* unmanaged<int, void> glClearStencil;
        public static delegate* unmanaged<uint, void> glClear;
        public static delegate* unmanaged<uint, void> glEnable;
        public static delegate* unmanaged<uint, void> glDisable;
        public static delegate* unmanaged<uint, uint, uint, uint, void> glBlendFuncSeparate;
        public static delegate* unmanaged<uint, uint, void> glBlendEquationSeparate;
        public static delegate* unmanaged<byte, byte, byte, byte, void> glColorMask;
        public static delegate* unmanaged<byte, void> glDepthMask;
        public static delegate* unmanaged<uint, void> glDepthFunc;
        public static delegate* unmanaged<uint, int, uint, void> glStencilFunc;
        public static delegate* unmanaged<uint, uint, uint, void> glStencilOp;
        public static delegate* unmanaged<uint, void> glStencilMask;
        public static delegate* unmanaged<uint, void> glCullFace;
        public static delegate* unmanaged<uint, void> glFrontFace;
        public static delegate* unmanaged<int, uint*, void> glGenTextures;
        public static delegate* unmanaged<int, uint*, void> glDeleteTextures;
        public static delegate* unmanaged<uint, uint, void> glBindTexture;
        public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> glTexImage2D;
        public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> glTexSubImage2D;
        public static delegate* unmanaged<uint, int, int, int, int, int, int, uint, uint, void*, void> glTexImage3D;
        public static delegate* unmanaged<uint, int, uint, int, int, int, int, void*, void> glCompressedTexImage2D;
        public static delegate* unmanaged<uint, uint, int, void> glTexParameteri;
        public static delegate* unmanaged<uint, void> glGenerateMipmap;
        public static delegate* unmanaged<uint, void> glActiveTexture;
        public static delegate* unmanaged<uint, int, void> glPixelStorei;
        public static delegate* unmanaged<uint, uint> glCreateShader;
        public static delegate* unmanaged<uint, int, byte**, int*, void> glShaderSource;
        public static delegate* unmanaged<uint, void> glCompileShader;
        public static delegate* unmanaged<uint, uint, int*, void> glGetShaderiv;
        public static delegate* unmanaged<uint, int, int*, byte*, void> glGetShaderInfoLog;
        public static delegate* unmanaged<uint, void> glDeleteShader;
        public static delegate* unmanaged<uint> glCreateProgram;
        public static delegate* unmanaged<uint, uint, void> glAttachShader;
        public static delegate* unmanaged<uint, uint, byte*, void> glBindAttribLocation;
        public static delegate* unmanaged<uint, void> glLinkProgram;
        public static delegate* unmanaged<uint, uint, int*, void> glGetProgramiv;
        public static delegate* unmanaged<uint, int, int*, byte*, void> glGetProgramInfoLog;
        public static delegate* unmanaged<uint, void> glUseProgram;
        public static delegate* unmanaged<uint, void> glDeleteProgram;
        public static delegate* unmanaged<uint, byte*, int> glGetUniformLocation;
        public static delegate* unmanaged<uint, byte*, uint> glGetUniformBlockIndex;
        public static delegate* unmanaged<uint, uint, uint, void> glUniformBlockBinding;
        public static delegate* unmanaged<int, int, void> glUniform1i;
        public static delegate* unmanaged<int, int, float*, void> glUniform4fv, glUniform2fv, glUniform1fv;
        public static delegate* unmanaged<int, int, byte, float*, void> glUniformMatrix4fv;
        public static delegate* unmanaged<int, uint*, void> glGenBuffers;
        public static delegate* unmanaged<int, uint*, void> glDeleteBuffers;
        public static delegate* unmanaged<uint, uint, void> glBindBuffer;
        public static delegate* unmanaged<uint, nint, void*, uint, void> glBufferData;
        public static delegate* unmanaged<uint, nint, nint, void*, void> glBufferSubData;
        public static delegate* unmanaged<uint, uint, uint, void> glBindBufferBase;
        public static delegate* unmanaged<int, uint*, void> glGenVertexArrays;
        public static delegate* unmanaged<int, uint*, void> glDeleteVertexArrays;
        public static delegate* unmanaged<uint, void> glBindVertexArray;
        public static delegate* unmanaged<uint, int, uint, byte, int, void*, void> glVertexAttribPointer;
        public static delegate* unmanaged<uint, void> glEnableVertexAttribArray;
        public static delegate* unmanaged<uint, void> glDisableVertexAttribArray;
        public static delegate* unmanaged<uint, float, float, float, float, void> glVertexAttrib4f;
        public static delegate* unmanaged<uint, int, uint, void*, void> glDrawElements;
        public static delegate* unmanaged<uint, int, int, void> glDrawArrays;
        public static delegate* unmanaged<int, uint*, void> glGenFramebuffers;
        public static delegate* unmanaged<int, uint*, void> glDeleteFramebuffers;
        public static delegate* unmanaged<uint, uint, void> glBindFramebuffer;
        public static delegate* unmanaged<uint, uint, uint, uint, int, void> glFramebufferTexture2D;
        public static delegate* unmanaged<uint, uint> glCheckFramebufferStatus;
        public static delegate* unmanaged<int, uint*, void> glGenRenderbuffers;
        public static delegate* unmanaged<int, uint*, void> glDeleteRenderbuffers;
        public static delegate* unmanaged<uint, uint, void> glBindRenderbuffer;
        public static delegate* unmanaged<uint, uint, int, int, void> glRenderbufferStorage;
        public static delegate* unmanaged<uint, uint, uint, uint, void> glFramebufferRenderbuffer;
        public static delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void> glBlitFramebuffer;
        public static delegate* unmanaged<int, int, int, int, uint, uint, void*, void> glReadPixels;
        public static delegate* unmanaged<uint> glGetError;
        public static delegate* unmanaged<uint, int*, void> glGetIntegerv;
        public static delegate* unmanaged<uint, byte*> glGetString;
        public static delegate* unmanaged<void> glFinish;
        public static delegate* unmanaged<void> glFlush;
        public static delegate* unmanaged<uint, int, int, int, int, int, int, int, void> glCopyTexSubImage2D;

        public static bool Desktop;

        public static void Load(Func<string, IntPtr> get, bool desktop)
        {
            Desktop = desktop;
            IntPtr P(string n)
            {
                var p = get(n);
                if (p == IntPtr.Zero) throw new EntryPointNotFoundException("GL：找不到 " + n);
                return p;
            }
            glViewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
            glScissor = (delegate* unmanaged<int, int, int, int, void>)P("glScissor");
            glClearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor");
            glClearDepthf = (delegate* unmanaged<float, void>)P("glClearDepthf");
            glClearStencil = (delegate* unmanaged<int, void>)P("glClearStencil");
            glClear = (delegate* unmanaged<uint, void>)P("glClear");
            glEnable = (delegate* unmanaged<uint, void>)P("glEnable");
            glDisable = (delegate* unmanaged<uint, void>)P("glDisable");
            glBlendFuncSeparate = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glBlendFuncSeparate");
            glBlendEquationSeparate = (delegate* unmanaged<uint, uint, void>)P("glBlendEquationSeparate");
            glColorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)P("glColorMask");
            glDepthMask = (delegate* unmanaged<byte, void>)P("glDepthMask");
            glDepthFunc = (delegate* unmanaged<uint, void>)P("glDepthFunc");
            glStencilFunc = (delegate* unmanaged<uint, int, uint, void>)P("glStencilFunc");
            glStencilOp = (delegate* unmanaged<uint, uint, uint, void>)P("glStencilOp");
            glStencilMask = (delegate* unmanaged<uint, void>)P("glStencilMask");
            glCullFace = (delegate* unmanaged<uint, void>)P("glCullFace");
            glFrontFace = (delegate* unmanaged<uint, void>)P("glFrontFace");
            glGenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
            glDeleteTextures = (delegate* unmanaged<int, uint*, void>)P("glDeleteTextures");
            glBindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture");
            glTexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
            glTexSubImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexSubImage2D");
            glTexImage3D = (delegate* unmanaged<uint, int, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage3D");
            glTexParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri");
            glGenerateMipmap = (delegate* unmanaged<uint, void>)P("glGenerateMipmap");
            glActiveTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture");
            glPixelStorei = (delegate* unmanaged<uint, int, void>)P("glPixelStorei");
            glCreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
            glShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
            glCompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
            glGetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
            glGetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
            glDeleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
            glCreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
            glAttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
            glBindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)P("glBindAttribLocation");
            glLinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
            glGetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
            glGetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetProgramInfoLog");
            glUseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
            glDeleteProgram = (delegate* unmanaged<uint, void>)P("glDeleteProgram");
            glGetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
            glGetUniformBlockIndex = (delegate* unmanaged<uint, byte*, uint>)P("glGetUniformBlockIndex");
            glUniformBlockBinding = (delegate* unmanaged<uint, uint, uint, void>)P("glUniformBlockBinding");
            glUniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
            glUniform4fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform4fv");
            glUniform2fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform2fv");
            glUniform1fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform1fv");
            glUniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
            glGenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
            glDeleteBuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteBuffers");
            glBindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
            glBufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)P("glBufferData");
            glBufferSubData = (delegate* unmanaged<uint, nint, nint, void*, void>)P("glBufferSubData");
            glBindBufferBase = (delegate* unmanaged<uint, uint, uint, void>)P("glBindBufferBase");
            glGenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
            glDeleteVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glDeleteVertexArrays");
            glBindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
            glVertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)P("glVertexAttribPointer");
            glEnableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray");
            glDisableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glDisableVertexAttribArray");
            glVertexAttrib4f = (delegate* unmanaged<uint, float, float, float, float, void>)P("glVertexAttrib4f");
            glDrawElements = (delegate* unmanaged<uint, int, uint, void*, void>)P("glDrawElements");
            glDrawArrays = (delegate* unmanaged<uint, int, int, void>)P("glDrawArrays");
            glGenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
            glDeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteFramebuffers");
            glBindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
            glFramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
            glCheckFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
            glGenRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glGenRenderbuffers");
            glDeleteRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteRenderbuffers");
            glBindRenderbuffer = (delegate* unmanaged<uint, uint, void>)P("glBindRenderbuffer");
            glRenderbufferStorage = (delegate* unmanaged<uint, uint, int, int, void>)P("glRenderbufferStorage");
            glFramebufferRenderbuffer = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glFramebufferRenderbuffer");
            glBlitFramebuffer = (delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void>)P("glBlitFramebuffer");
            glReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)P("glReadPixels");
            glGetError = (delegate* unmanaged<uint>)P("glGetError");
            glGetIntegerv = (delegate* unmanaged<uint, int*, void>)P("glGetIntegerv");
            glGetString = (delegate* unmanaged<uint, byte*>)P("glGetString");
            glCompressedTexImage2D = (delegate* unmanaged<uint, int, uint, int, int, int, int, void*, void>)P("glCompressedTexImage2D");
            glFinish = (delegate* unmanaged<void>)P("glFinish");
            glFlush = (delegate* unmanaged<void>)P("glFlush");
            glCopyTexSubImage2D = (delegate* unmanaged<uint, int, int, int, int, int, int, int, void>)P("glCopyTexSubImage2D");
            // 寫進 SRGB8_ALPHA8 附件時轉成 sRGB：GL ES／WebGL2 一律會轉，desktop GL 要開這個才轉。
            // 沒開的話桌面版少編碼一次，整個畫面暗一個 gamma（10/19 跟手機、WebView 版對照才發現）
            if (desktop) glEnable(FRAMEBUFFER_SRGB);
        }

        public static string Str(uint name)
        {
            var p = glGetString(name);
            return p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p);
        }

        public static uint Gen(delegate* unmanaged<int, uint*, void> fn) { uint id; fn(1, &id); return id; }

        // ───────── state 快取（docs/perf-battle.md A4）─────────
        // 引擎與宿主改下面這些 state 一律走這裡，不直接叫 glEnable 等函式指標：值跟上次設的一樣就不進驅動。
        // 戰鬥一格約 500 個 draw，每個 draw 的 Draw.ApplyState 都把 blend、cull、color mask、depth、stencil 設一遍，大多跟上一個相同。
        // 只記這個 context 裡我們自己設過的值；context 重建（宿主的 OnSurfaceCreated）叫 ResetStateCache，全部當成不知道。
        // 不在快取裡的 target（例如影片的 GL_TEXTURE_EXTERNAL_OES）照舊每次都呼叫

        static int capsKnown, capsOn;
        static long blendFunc = -1, blendEq = -1, colorMask = -1, depthMask = -1, depthFunc = -1, stencilOp = -1, stencilMask = -1, cullFace = -1, frontFace = -1, program = -1;
        static bool stencilFuncKnown;
        static uint stencilFn, stencilFnMask;
        static int stencilRef;
        static uint activeUnit = uint.MaxValue;
        // 每個 texture unit 綁的 TEXTURE_2D、TEXTURE_2D_ARRAY（-1 是不知道）
        static readonly long[] bound2D = Unknown(32), boundArray = Unknown(32);
        static long[] Unknown(int n) { var a = new long[n]; Array.Fill(a, -1); return a; }

        /// <summary>量測（ctl「glcache on|off」、「gl」）：關掉時照樣記值、每次都送進驅動；Skipped／Issued 是省掉與送出的次數</summary>
        public static bool StateCache = true;
        public static long StateSkipped, StateIssued;
        static bool Skip(bool same) { if (same && StateCache) { StateSkipped++; return true; } StateIssued++; return false; }

        static int CapBit(uint cap) => cap switch { BLEND => 1, CULL_FACE => 2, DEPTH_TEST => 4, STENCIL_TEST => 8, SCISSOR_TEST => 16, _ => 0 };

        public static void ResetStateCache()
        {
            capsKnown = capsOn = 0;
            blendFunc = blendEq = colorMask = depthMask = depthFunc = stencilOp = stencilMask = cullFace = frontFace = program = -1;
            stencilFuncKnown = false;
            activeUnit = uint.MaxValue;
            Array.Fill(bound2D, -1); Array.Fill(boundArray, -1);
        }

        public static void Enable(uint cap)
        {
            int b = CapBit(cap);
            if (Skip(b != 0 && (capsKnown & capsOn & b) != 0)) return;
            glEnable(cap);
            capsKnown |= b; capsOn |= b;
        }

        public static void Disable(uint cap)
        {
            int b = CapBit(cap);
            if (Skip(b != 0 && (capsKnown & b) != 0 && (capsOn & b) == 0)) return;
            glDisable(cap);
            capsKnown |= b; capsOn &= ~b;
        }

        public static void BlendFuncSeparate(uint src, uint dst, uint srcA, uint dstA)
        {
            long k = (long)src << 48 | (long)dst << 32 | (long)srcA << 16 | dstA;
            if (Skip(k == blendFunc)) return;
            blendFunc = k; glBlendFuncSeparate(src, dst, srcA, dstA);
        }

        public static void BlendEquationSeparate(uint rgb, uint alpha)
        {
            long k = (long)rgb << 32 | alpha;
            if (Skip(k == blendEq)) return;
            blendEq = k; glBlendEquationSeparate(rgb, alpha);
        }

        public static void ColorMask(byte r, byte g, byte b, byte a)
        {
            long k = r << 24 | g << 16 | b << 8 | a;
            if (Skip(k == colorMask)) return;
            colorMask = k; glColorMask(r, g, b, a);
        }

        public static void DepthMask(byte flag) { if (Skip(flag == depthMask)) return; depthMask = flag; glDepthMask(flag); }
        public static void DepthFunc(uint func) { if (Skip(func == depthFunc)) return; depthFunc = func; glDepthFunc(func); }
        public static void StencilMask(uint mask) { if (Skip(mask == stencilMask)) return; stencilMask = mask; glStencilMask(mask); }
        public static void CullFace(uint mode) { if (Skip(mode == cullFace)) return; cullFace = mode; glCullFace(mode); }
        public static void FrontFace(uint mode) { if (Skip(mode == frontFace)) return; frontFace = mode; glFrontFace(mode); }
        public static void UseProgram(uint p) { if (Skip(p == program)) return; program = p; glUseProgram(p); }

        public static void StencilFunc(uint func, int reference, uint mask)
        {
            if (Skip(stencilFuncKnown && func == stencilFn && reference == stencilRef && mask == stencilFnMask)) return;
            stencilFuncKnown = true; stencilFn = func; stencilRef = reference; stencilFnMask = mask;
            glStencilFunc(func, reference, mask);
        }

        public static void StencilOp(uint sfail, uint dpfail, uint dppass)
        {
            long k = (long)sfail << 32 | (long)dpfail << 16 | dppass;
            if (Skip(k == stencilOp)) return;
            stencilOp = k; glStencilOp(sfail, dpfail, dppass);
        }

        public static void ActiveTexture(uint unit) { if (Skip(unit == activeUnit)) return; activeUnit = unit; glActiveTexture(unit); }

        public static void BindTexture(uint target, uint tex)
        {
            uint u = activeUnit - TEXTURE0;
            var slots = target == TEXTURE_2D ? bound2D : target == TEXTURE_2D_ARRAY ? boundArray : null;
            bool cached = slots != null && u < (uint)slots.Length;
            if (Skip(cached && slots[u] == tex)) return;
            if (cached) slots[u] = tex;
            glBindTexture(target, tex);
        }

        /// <summary>刪掉的貼圖：GL 會把綁著它的 unit 設回 0，快取跟著改</summary>
        public static void DeleteTextures(int n, uint* ids)
        {
            for (int i = 0; i < n; i++)
                for (int u = 0; u < bound2D.Length; u++)
                {
                    if (bound2D[u] == ids[i]) bound2D[u] = 0;
                    if (boundArray[u] == ids[i]) boundArray[u] = 0;
                }
            glDeleteTextures(n, ids);
        }

        public static int Uniform(uint prog, string name)
        {
            var b = Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* p = b) return glGetUniformLocation(prog, p);
        }

        public static uint BlockIndex(uint prog, string name)
        {
            var b = Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* p = b) return glGetUniformBlockIndex(prog, p);
        }

        /// <summary>編譯、link 的次數與累計時間（報告用）</summary>
        public static int Compiles, Links;
        public static double CompileMs;

        public static uint Compile(uint type, string src, out string log)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return CompileCore(type, src, out log); }
            finally { Compiles++; CompileMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; }
        }

        static uint CompileCore(uint type, string src, out string log)
        {
            var sh = glCreateShader(type);
            var b = Encoding.UTF8.GetBytes(src);
            fixed (byte* p = b)
            {
                byte* pp = p;
                int len = b.Length;
                glShaderSource(sh, 1, &pp, &len);
            }
            glCompileShader(sh);
            int ok;
            glGetShaderiv(sh, COMPILE_STATUS, &ok);
            log = null;
            if (ok == 0)
            {
                int n;
                glGetShaderiv(sh, INFO_LOG_LENGTH, &n);
                var buf = new byte[Math.Max(1, n)];
                fixed (byte* p = buf) glGetShaderInfoLog(sh, buf.Length, null, p);
                log = Encoding.UTF8.GetString(buf).TrimEnd('\0');
                glDeleteShader(sh);
                return 0;
            }
            return sh;
        }

        public static uint Link(uint vs, uint fs, out string log)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return LinkCore(vs, fs, out log); }
            finally { Links++; CompileMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; }
        }

        static uint LinkCore(uint vs, uint fs, out string log)
        {
            var p = glCreateProgram();
            glAttachShader(p, vs);
            glAttachShader(p, fs);
            glLinkProgram(p);
            int ok;
            glGetProgramiv(p, LINK_STATUS, &ok);
            log = null;
            if (ok == 0)
            {
                int n;
                glGetProgramiv(p, INFO_LOG_LENGTH, &n);
                var buf = new byte[Math.Max(1, n)];
                fixed (byte* b = buf) glGetProgramInfoLog(p, buf.Length, null, b);
                log = Encoding.UTF8.GetString(buf).TrimEnd('\0');
                glDeleteProgram(p);
                return 0;
            }
            return p;
        }
    }
}
