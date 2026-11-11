// 動態頂點：uGUI 與 sprite 的頂點每批收進來，畫的時候只上傳那一段。
// 每次上傳換緩衝池裡的下一組 VBO／IBO（每格從頭輪），一次 glBufferData 傳完：同一個緩衝一格只上傳一次。
// 以前全部共用一組，一格 orphan＋glBufferSubData 二十幾次，驅動要等 GPU 用完前一份（Mac 的 GL 與手機的 Adreno 都會），
// Diceomancer 寬版戰鬥一格 20 個 Canvas、共 2,600 個頂點就要 5 ms。
// 屬性照 shader 的 vertexInputs（POSITION、COLOR、TEXCOORDn、NORMAL、TANGENT）綁；資料沒有的給常數（顏色白、其他 0）。
using System;
using UnityEngine;

namespace UnityNative.Gl
{
    public sealed unsafe class Stream
    {
        uint vao, meshVao, curVbo;
        /// <summary>vao、meshVao 目前開著的頂點屬性（位元 l ＝ 屬性 l）：每次只開這次要的、只關這次不用的，最後的狀態跟以前「16 個先全關再開」相同</summary>
        uint vaoOn, meshVaoOn;

        static void Enable(ref uint on, uint loc)
        {
            if ((on & (1u << (int)loc)) != 0) return;
            GL.glEnableVertexAttribArray(loc);
            on |= 1u << (int)loc;
        }

        /// <summary>這次沒用到的關掉（want 之外、目前開著的）</summary>
        static void DisableRest(ref uint on, uint want)
        {
            uint off = on & ~want;
            for (uint l = 0; off != 0; l++, off >>= 1) if ((off & 1) != 0) GL.glDisableVertexAttribArray(l);
            on &= want;
        }
        readonly System.Collections.Generic.List<(uint Vbo, uint Ibo)> pool = new System.Collections.Generic.List<(uint, uint)>();
        int poolNext;
        float[] pos = new float[3 * 1024], col = new float[4 * 1024], uv0 = new float[4 * 1024], uv1 = new float[4 * 1024],
            uv2 = new float[4 * 1024], uv3 = new float[4 * 1024], nrm = new float[3 * 1024], tan = new float[4 * 1024];
        int[] idx = new int[3 * 1024];
        float[] upload = new float[30 * 1024];
        uint[] uidx = new uint[3 * 1024];
        public int VertexCount, IndexCount;
        bool hasUv1, hasUv2, hasUv3, hasNrm, hasTan;

        public void Init()
        {
            vao = GL.Gen(GL.glGenVertexArrays);
            meshVao = GL.Gen(GL.glGenVertexArrays);
        }

        /// <summary>每格開頭：緩衝池從頭用</summary>
        public void NewFrame() => poolNext = 0;

        /// <summary>綁上緩衝池的下一組（vao 要先綁好：IBO 記在 vao 裡），傳頂點與索引</summary>
        void Upload(float[] verts, int floats, uint[] indices, int idxCount)
        {
            if (poolNext == pool.Count) pool.Add((GL.Gen(GL.glGenBuffers), GL.Gen(GL.glGenBuffers)));
            var (vb, ib) = pool[poolNext++];
            curVbo = vb;
            GL.glBindBuffer(GL.ARRAY_BUFFER, vb);
            fixed (float* d = verts) GL.glBufferData(GL.ARRAY_BUFFER, floats * 4, d, GL.STREAM_DRAW);
            GL.glBindBuffer(GL.ELEMENT_ARRAY_BUFFER, ib);
            fixed (uint* d = indices) GL.glBufferData(GL.ELEMENT_ARRAY_BUFFER, idxCount * 4, d, GL.STREAM_DRAW);
        }

        public string ColorAt(int v) => v * 4 + 3 < col.Length ? $"{col[v * 4]:F2},{col[v * 4 + 1]:F2},{col[v * 4 + 2]:F2},{col[v * 4 + 3]:F2}" : "";

        public void BeginBatch() { VertexCount = 0; IndexCount = 0; hasUv1 = hasUv2 = hasUv3 = hasNrm = hasTan = false; }

        void Reserve(int v, int i)
        {
            int nv = VertexCount + v;
            if (nv * 4 > col.Length)
            {
                int cap = Math.Max(nv * 2, 1024);
                Array.Resize(ref pos, cap * 3); Array.Resize(ref col, cap * 4); Array.Resize(ref uv0, cap * 4); Array.Resize(ref uv1, cap * 4);
                Array.Resize(ref uv2, cap * 4); Array.Resize(ref uv3, cap * 4); Array.Resize(ref nrm, cap * 3); Array.Resize(ref tan, cap * 4);
            }
            if (IndexCount + i > idx.Length) Array.Resize(ref idx, Math.Max((IndexCount + i) * 2, 1024));
        }

        static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        /// <summary>uGUI 的一個 CanvasRenderer：位置換到根 Canvas 座標，顏色 × CanvasRenderer 顏色（gamma）× 繼承的 alpha，再轉線性</summary>
        public void AddUI(UIGeometry g, Matrix4x4 m, Color tint, float alpha, int batchStart)
        {
            int n = g.VertexCount;
            Reserve(n, g.Indices.Length);
            int b = VertexCount;
            for (int i = 0; i < n; i++)
            {
                float x = g.Pos[i * 3], y = g.Pos[i * 3 + 1], z = g.Pos[i * 3 + 2];
                int o = (b + i) * 3;
                pos[o] = m.m00 * x + m.m01 * y + m.m02 * z + m.m03;
                pos[o + 1] = m.m10 * x + m.m11 * y + m.m12 * z + m.m13;
                pos[o + 2] = m.m20 * x + m.m21 * y + m.m22 * z + m.m23;
                int c = (b + i) * 4, s = i * 4;
                col[c] = Lin(g.Color[s] * tint.r); col[c + 1] = Lin(g.Color[s + 1] * tint.g); col[c + 2] = Lin(g.Color[s + 2] * tint.b);
                col[c + 3] = g.Color[s + 3] * tint.a * alpha;
                Array.Copy(g.UV0, s, uv0, c, 4);
                // 沒有的通道也寫預設值：整個 Canvas 一次上傳時，別的元素有這個通道，這段不能留著上一格的舊資料
                if (g.UV1 != null) { Array.Copy(g.UV1, s, uv1, c, 4); hasUv1 = true; } else Array.Clear(uv1, c, 4);
                if (g.UV2 != null) { Array.Copy(g.UV2, s, uv2, c, 4); hasUv2 = true; } else Array.Clear(uv2, c, 4);
                if (g.UV3 != null) { Array.Copy(g.UV3, s, uv3, c, 4); hasUv3 = true; } else Array.Clear(uv3, c, 4);
                if (g.Normal != null) { Array.Copy(g.Normal, i * 3, nrm, (b + i) * 3, 3); hasNrm = true; } else DefaultNormal(b + i);
                if (g.Tangent != null) { Array.Copy(g.Tangent, s, tan, c, 4); hasTan = true; } else DefaultTangent(b + i);
            }
            for (int i = 0; i < g.Indices.Length; i++) idx[IndexCount + i] = b + g.Indices[i];
            IndexCount += g.Indices.Length;
            VertexCount += n;
        }

        public void AddSprite(NSprite sp, bool flipX, bool flipY, Color color)
        {
            var v = sp.Vertices; var uv = sp.UV; var t = sp.Triangles;
            Reserve(v.Length, t.Length);
            int b = VertexCount;
            float fx = flipX ? -1 : 1, fy = flipY ? -1 : 1;
            for (int i = 0; i < v.Length; i++)
            {
                int o = (b + i) * 3, c = (b + i) * 4;
                pos[o] = v[i].x * fx; pos[o + 1] = v[i].y * fy; pos[o + 2] = 0;
                col[c] = color.r; col[c + 1] = color.g; col[c + 2] = color.b; col[c + 3] = color.a;
                uv0[c] = uv[i].x; uv0[c + 1] = uv[i].y; uv0[c + 2] = 0; uv0[c + 3] = 0;
                Array.Clear(uv1, c, 4); Array.Clear(uv2, c, 4); Array.Clear(uv3, c, 4);
                DefaultNormal(b + i); DefaultTangent(b + i);
            }
            // 翻轉一個軸會反轉三角形方向；2D 的 shader 都是 Cull Off，不另外處理
            for (int i = 0; i < t.Length; i++) idx[IndexCount + i] = b + t[i];
            IndexCount += t.Length;
            VertexCount += v.Length;
        }

        /// <summary>LineRenderer 建好的世界座標網格（Graphics/LineRenderer.cs：位置 3、法線 3、顏色 4、uv0 4，三角形一個子網格）照原樣接上去。
        /// 網格沒有的通道給的值跟 DrawMesh 的常數一樣（顏色白、uv 0、法線 (0,0,−1)、切線 (1,0,0,−1)），所以 shader 讀到的頂點跟一條一條用 DrawMesh 畫時相同</summary>
        public void AddMesh(NMesh m)
        {
            int n = m.VertexCount;
            var sub = m.Subs[0];
            var ind = sub.Indices;
            Reserve(n, ind.Length);
            int b = VertexCount;
            var P = m.Channels[(int)UnityEngine.Rendering.VertexAttribute.Position];
            var N = m.Channels[(int)UnityEngine.Rendering.VertexAttribute.Normal];
            var C = m.Channels[(int)UnityEngine.Rendering.VertexAttribute.Color];
            var T = m.Channels[(int)UnityEngine.Rendering.VertexAttribute.TexCoord0];
            if (N != null) hasNrm = true;
            for (int i = 0; i < n; i++)
            {
                int o = (b + i) * 3, c = (b + i) * 4;
                for (int k = 0; k < 3; k++) pos[o + k] = k < P.Dim ? P.Data[i * P.Dim + k] : 0;
                if (N != null) for (int k = 0; k < 3; k++) nrm[o + k] = k < N.Dim ? N.Data[i * N.Dim + k] : 0;
                else DefaultNormal(b + i);
                for (int k = 0; k < 4; k++)
                {
                    col[c + k] = C != null ? (k < C.Dim ? C.Data[i * C.Dim + k] : 1) : 1;
                    uv0[c + k] = T == null ? 0 : k < T.Dim ? T.Data[i * T.Dim + k] : k == 3 ? 1 : 0; // 通道不到 4 維時 GL 補 (0, 1)
                }
                Array.Clear(uv1, c, 4); Array.Clear(uv2, c, 4); Array.Clear(uv3, c, 4);
                DefaultTangent(b + i);
            }
            for (int i = 0; i < ind.Length; i++) idx[IndexCount + i] = b + ind[i] + sub.BaseVertex;
            IndexCount += ind.Length;
            VertexCount += n;
        }

        /// <summary>SpriteRenderer 的 Sliced／Tiled（drawMode 1、2）：九宮格，網格大小 = size（世界單位），照 sprite 的 pivot 擺；
        /// 四角維持邊框大小（border 像素 ÷ pixelsPerUnit），四邊與中間拉伸。同網頁版 render/spriteSliced.ts：Unity 這段是原生程式，
        /// 照手冊的 9-slicing 定義；size 比左右（上下）邊框加起來還小時等比縮小邊框（照 uGUI 的 GetAdjustedBorders，未查證），
        /// size 是負的時鏡像；Tiled 的中間與四邊應該是重複鋪，這裡也拉伸（中間與四邊沿鋪的方向均勻的圖兩者相同）</summary>
        public void AddSlicedSprite(NSprite sp, Vector2 size, bool flipX, bool flipY, Color color)
        {
            float w = Math.Abs(size.x), h = Math.Abs(size.y);
            float sx = (size.x < 0 ? -1 : 1) * (flipX ? -1 : 1), sy = (size.y < 0 ? -1 : 1) * (flipY ? -1 : 1);
            float ppu = sp.PixelsPerUnit <= 0 ? 100 : sp.PixelsPerUnit;
            var bd = sp.Border;
            float bl = bd.x / ppu, bb = bd.y / ppu, br = bd.z / ppu, bt = bd.w / ppu;
            if (w < bl + br && bl + br > 0) { float k = w / (bl + br); bl *= k; br *= k; }
            if (h < bb + bt && bb + bt > 0) { float k = h / (bb + bt); bb *= k; bt *= k; }
            float x0 = -sp.Pivot.x * w, y0 = -sp.Pivot.y * h;
            Span<float> xs = stackalloc float[] { x0, x0 + bl, x0 + w - br, x0 + w };
            Span<float> ys = stackalloc float[] { y0, y0 + bb, y0 + h - bt, y0 + h };
            var tr = sp.TextureRect;
            float tw = sp.Texture?.Width ?? 1, th = sp.Texture?.Height ?? 1;
            Span<float> us = stackalloc float[] { tr.x / tw, (tr.x + bd.x) / tw, (tr.xMax - bd.z) / tw, tr.xMax / tw };
            Span<float> vs = stackalloc float[] { tr.y / th, (tr.y + bd.y) / th, (tr.yMax - bd.w) / th, tr.yMax / th };
            Reserve(16, 54);
            int b = VertexCount;
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++)
                {
                    int v = b + j * 4 + i, o = v * 3, c = v * 4;
                    pos[o] = xs[i] * sx; pos[o + 1] = ys[j] * sy; pos[o + 2] = 0;
                    col[c] = color.r; col[c + 1] = color.g; col[c + 2] = color.b; col[c + 3] = color.a;
                    uv0[c] = us[i]; uv0[c + 1] = vs[j]; uv0[c + 2] = 0; uv0[c + 3] = 0;
                    Array.Clear(uv1, c, 4); Array.Clear(uv2, c, 4); Array.Clear(uv3, c, 4);
                    DefaultNormal(v); DefaultTangent(v);
                }
            // 2D 的 shader 都是 Cull Off，鏡像不另外反轉三角形方向（同 AddSprite）
            int n = IndexCount;
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++)
                {
                    int a = b + j * 4 + i;
                    idx[n++] = a; idx[n++] = a + 4; idx[n++] = a + 5;
                    idx[n++] = a + 5; idx[n++] = a + 1; idx[n++] = a;
                }
            IndexCount = n;
            VertexCount += 16;
        }

        public void DrawRange(GpuProgram p, int start, int count, int idxStart, int idxCount)
        {
            if (count <= 0 || idxCount <= 0) return;
            attribsFor = null; // 同一個 vao、常數屬性是全域的：DrawUploaded 的快取作廢
            // 版面：pos(3) col(4) uv0(4) uv1(4) uv2(4) uv3(4) nrm(3) tan(4)，各通道連續存
            int need = count * 30;
            if (upload.Length < need) upload = new float[need * 2];
            int oPos = 0, oCol = oPos + count * 3, oUv0 = oCol + count * 4, oUv1 = oUv0 + count * 4, oUv2 = oUv1 + count * 4, oUv3 = oUv2 + count * 4, oNrm = oUv3 + count * 4, oTan = oNrm + count * 3;
            Array.Copy(pos, start * 3, upload, oPos, count * 3);
            Array.Copy(col, start * 4, upload, oCol, count * 4);
            Array.Copy(uv0, start * 4, upload, oUv0, count * 4);
            if (hasUv1) Array.Copy(uv1, start * 4, upload, oUv1, count * 4); else Array.Clear(upload, oUv1, count * 4);
            if (hasUv2) Array.Copy(uv2, start * 4, upload, oUv2, count * 4); else Array.Clear(upload, oUv2, count * 4);
            if (hasUv3) Array.Copy(uv3, start * 4, upload, oUv3, count * 4); else Array.Clear(upload, oUv3, count * 4);
            if (hasNrm) Array.Copy(nrm, start * 3, upload, oNrm, count * 3); else { for (int i = 0; i < count; i++) { upload[oNrm + i * 3] = 0; upload[oNrm + i * 3 + 1] = 0; upload[oNrm + i * 3 + 2] = -1; } }
            if (hasTan) Array.Copy(tan, start * 4, upload, oTan, count * 4); else { for (int i = 0; i < count; i++) { upload[oTan + i * 4] = 1; upload[oTan + i * 4 + 1] = 0; upload[oTan + i * 4 + 2] = 0; upload[oTan + i * 4 + 3] = -1; } }
            if (uidx.Length < idxCount) uidx = new uint[idxCount * 2];
            for (int i = 0; i < idxCount; i++) uidx[i] = (uint)(idx[idxStart + i] - start);

            GL.glBindVertexArray(vao);
            Upload(upload, need, uidx, idxCount);
            uint want = 0;
            foreach (var inp in p.Inputs)
            {
                int off = -1, dim = 4;
                switch (inp.Semantic)
                {
                    case "POSITION": off = oPos; dim = 3; break;
                    case "COLOR": off = oCol; break;
                    case "TEXCOORD": off = inp.Index switch { 0 => oUv0, 1 => oUv1, 2 => oUv2, 3 => oUv3, _ => -1 }; break;
                    case "NORMAL": off = oNrm; dim = 3; break;
                    case "TANGENT": off = oTan; break;
                }
                uint loc = (uint)inp.Location;
                if (off < 0) { Const(loc, inp.Semantic); vaoOn &= ~(1u << (int)loc); continue; }
                Enable(ref vaoOn, loc); want |= 1u << (int)loc;
                GL.glVertexAttribPointer(loc, dim, GL.FLOAT, 0, 0, (void*)(off * 4));
            }
            DisableRest(ref vaoOn, want);
            GL.glDrawElements(GL.TRIANGLES, idxCount, GL.UNSIGNED_INT, null);
            GL.glBindVertexArray(0);
        }

        void DefaultNormal(int v) { nrm[v * 3] = 0; nrm[v * 3 + 1] = 0; nrm[v * 3 + 2] = -1; }
        void DefaultTangent(int v) { tan[v * 4] = 1; tan[v * 4 + 1] = 0; tan[v * 4 + 2] = 0; tan[v * 4 + 3] = -1; }

        // ───────── 整批一次上傳（uGUI：一個 Canvas 的頂點收完傳一次，各批照索引區段畫）─────────

        int upCount;          // 這次上傳的頂點數（各通道的起點跟著它算）
        object attribsFor;    // 目前 vao 的屬性是照哪一組 shader 輸入設的；同一組就不重設
        int attribsGen;       // 設的時候的 constGen：常數屬性（glVertexAttrib4f）是全域的，別的 Stream 或網格改過就要重設
        static int constGen;

        /// <summary>把目前收到的全部頂點與索引傳上去（索引本來就是整條 stream 的絕對位置）</summary>
        public void UploadAll()
        {
            int n = VertexCount;
            upCount = n;
            attribsFor = null;
            // 各通道連續存（版面同 DrawRange），AddUI／AddSprite 沒有的通道已經寫了預設值
            int need = n * 30;
            if (upload.Length < need) upload = new float[need * 2];
            int o = 0;
            void Part(float[] a, int dim) { Array.Copy(a, 0, upload, o, n * dim); o += n * dim; }
            Part(pos, 3); Part(col, 4); Part(uv0, 4); Part(uv1, 4); Part(uv2, 4); Part(uv3, 4); Part(nrm, 3); Part(tan, 4);
            if (uidx.Length < IndexCount) uidx = new uint[IndexCount * 2];
            Buffer.BlockCopy(idx, 0, uidx, 0, IndexCount * 4);
            GL.glBindVertexArray(vao);
            Upload(upload, need, uidx, IndexCount);
            GL.glBindVertexArray(0);
        }

        /// <summary>畫 UploadAll 傳上去的一段索引</summary>
        public void DrawUploaded(GpuProgram p, int idxStart, int idxCount)
        {
            if (idxCount <= 0) return;
            int n = upCount;
            GL.glBindVertexArray(vao);
            if (attribsFor != (object)p.Inputs || attribsGen != constGen)
            {
                attribsFor = p.Inputs;
                GL.glBindBuffer(GL.ARRAY_BUFFER, curVbo); // 屬性指標記的是當下綁的緩衝；中間畫過網格的話 ARRAY_BUFFER 已經換掉
                int oPos = 0, oCol = n * 3, oUv0 = oCol + n * 4, oUv1 = oUv0 + n * 4, oUv2 = oUv1 + n * 4, oUv3 = oUv2 + n * 4, oNrm = oUv3 + n * 4, oTan = oNrm + n * 3;
                uint want = 0;
                foreach (var inp in p.Inputs)
                {
                    int off = -1, dim = 4;
                    switch (inp.Semantic)
                    {
                        case "POSITION": off = oPos; dim = 3; break;
                        case "COLOR": off = oCol; break;
                        case "TEXCOORD": off = inp.Index switch { 0 => oUv0, 1 => oUv1, 2 => oUv2, 3 => oUv3, _ => -1 }; break;
                        case "NORMAL": off = oNrm; dim = 3; break;
                        case "TANGENT": off = oTan; break;
                    }
                    uint loc = (uint)inp.Location;
                    if (off < 0) { Const(loc, inp.Semantic); vaoOn &= ~(1u << (int)loc); continue; }
                    Enable(ref vaoOn, loc); want |= 1u << (int)loc;
                    GL.glVertexAttribPointer(loc, dim, GL.FLOAT, 0, 0, (void*)(off * 4));
                }
                DisableRest(ref vaoOn, want);
                attribsGen = constGen;
            }
            GL.glDrawElements(GL.TRIANGLES, idxCount, GL.UNSIGNED_INT, (void*)(idxStart * 4));
            GL.glBindVertexArray(0);
        }

        /// <summary>網格沒有這個頂點通道時的常數值（同 TS 版 sceneRenderer 的預設）：顏色白、法線 (0,0,−1)（朝攝影機）、
        /// 切線 (1,0,0,−1)，其他 0。Spine 角色（addNormals 關著）沒有法線，給 0 的話有光照的 shader 整隻變黑</summary>
        static void Const(uint loc, string semantic)
        {
            constGen++;
            GL.glDisableVertexAttribArray(loc);
            if (semantic == "COLOR") GL.glVertexAttrib4f(loc, 1, 1, 1, 1);
            else if (semantic == "NORMAL") GL.glVertexAttrib4f(loc, 0, 0, -1, 0);
            else if (semantic == "TANGENT") GL.glVertexAttrib4f(loc, 1, 0, 0, -1);
            else GL.glVertexAttrib4f(loc, 0, 0, 0, semantic == "POSITION" ? 1 : 0);
        }

        public void DrawMesh(GpuProgram p, GpuMesh g, int sub)
        {
            attribsFor = null; // Const 改的常數屬性是全域的
            if (sub >= g.Subs.Count) return;
            var (start, count, topo) = g.Subs[sub];
            if (count == 0) return;
            GL.glBindVertexArray(meshVao);
            uint want = 0;
            foreach (var inp in p.Inputs)
            {
                int ch = inp.Semantic switch
                {
                    "POSITION" => 0, "NORMAL" => 1, "TANGENT" => 2, "COLOR" => 3,
                    "TEXCOORD" => inp.Index < 8 ? 4 + inp.Index : -1, "BLENDWEIGHT" => 12, "BLENDWEIGHTS" => 12, "BLENDINDICES" => 13, _ => -1,
                };
                uint loc = (uint)inp.Location;
                if (ch < 0 || g.Dim[ch] == 0) { Const(loc, inp.Semantic); meshVaoOn &= ~(1u << (int)loc); continue; }
                GL.glBindBuffer(GL.ARRAY_BUFFER, g.Vbo[ch]);
                Enable(ref meshVaoOn, loc); want |= 1u << (int)loc;
                GL.glVertexAttribPointer(loc, g.Dim[ch], GL.FLOAT, 0, 0, null);
            }
            DisableRest(ref meshVaoOn, want);
            GL.glBindBuffer(GL.ELEMENT_ARRAY_BUFFER, g.Ibo);
            GL.glDrawElements(topo, count, GL.UNSIGNED_INT, (void*)(start * 4));
            GL.glBindVertexArray(0);
        }
    }
}
