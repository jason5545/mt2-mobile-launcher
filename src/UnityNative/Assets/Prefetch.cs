// 背景預讀：讀一個場景、prefab、ScriptableObject 檔時，順手從原始位元組掃出它參照的 guid（"guid":"…"），
// 對得到檔案的（prefab、ScriptableObject）先丟到執行緒池讀檔、解析；主執行緒真的要用時拿現成的，還沒好就等它。
// 素材的參照在載入時就整串載入（Serializer.Ref → Assets.Load），所以預讀的檔幾乎都會用到；MT2 開機的資料圖有四千多個檔，
// 讀檔與解析原本全在主執行緒上一個接一個做。
// 背景只做讀檔、JsonDocument 解析、掃 guid，不碰任何引擎狀態；guid → 檔案的表在 Init 時建好，之後只讀。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace UnityNative
{
    public static class Prefetch
    {
        /// <summary>關掉時照舊在主執行緒讀（UN_PREFETCH=0，比對用）</summary>
        public static bool Enabled = Environment.GetEnvironmentVariable("UN_PREFETCH") != "0";

        /// <summary>每個檔一個 Lazy：背景或主執行緒誰先碰誰讀，另一邊等結果。主執行緒拿走後換成 Taken（放掉文件，也不再預讀）</summary>
        static readonly ConcurrentDictionary<string, Lazy<JsonDocument>> entries = new ConcurrentDictionary<string, Lazy<JsonDocument>>();
        static readonly Lazy<JsonDocument> Taken = new Lazy<JsonDocument>(() => null);
        static Func<string, string> pathOf;

        /// <summary>guid → 完整路徑（沒有對應的檔回 null）；要能在背景執行緒呼叫</summary>
        public static void Init(Func<string, string> guidToPath) => pathOf = guidToPath;

        /// <summary>主執行緒：拿這個檔解析好的 JsonDocument（預讀過就用那份）</summary>
        public static JsonElement Take(string path)
        {
            if (entries.TryGetValue(path, out var lz) && lz != Taken)
            {
                var doc = lz.Value;
                entries.TryUpdate(path, Taken, lz);
                return doc.RootElement;
            }
            entries.TryAdd(path, Taken);
            return Read(path).RootElement;
        }

        static JsonDocument Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            ReadOnlyMemory<byte> mem = bytes;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) mem = mem.Slice(3);
            if (Enabled && pathOf != null) Scan(mem.Span);
            // 直接用讀進來的陣列，不另外複製
            return JsonDocument.Parse(mem);
        }

        static ReadOnlySpan<byte> GuidKey => "\"guid\":\""u8;

        /// <summary>掃出 "guid":"<32 個十六進位>"，有檔案的排進預讀</summary>
        static void Scan(ReadOnlySpan<byte> s)
        {
            int i = 0;
            while (true)
            {
                int k = s.Slice(i).IndexOf(GuidKey);
                if (k < 0) return;
                int a = i + k + GuidKey.Length;
                if (a + 33 > s.Length) return;
                i = a;
                if (s[a + 32] != (byte)'"') continue;
                var g = s.Slice(a, 32);
                bool hex = true;
                foreach (var c in g) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) { hex = false; break; }
                if (!hex) continue;
                var path = pathOf(System.Text.Encoding.ASCII.GetString(g));
                if (path == null || entries.ContainsKey(path)) continue;
                var lz = new Lazy<JsonDocument>(() => Read(path), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
                if (entries.TryAdd(path, lz)) Task.Run(() => _ = lz.Value);
            }
        }
    }
}
