// 素材庫：匯入器輸出的索引（public/game/assets/*.json）＋ PPtr（guid + fileID）→ 載入好的 UnityEngine.Object。
// 每種素材由對應的模組登記載入函式（Loaders）：貼圖、sprite、材質在繪製模組，聲音在聲音模組…
// 這裡只負責認出 guid 是哪一種、快取、Resources 路徑。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public static class Assets
    {
        public const string BuiltinExtra = "0000000000000000f000000000000000";
        public const string BuiltinDefault = "0000000000000000e000000000000000";

        public static string Dir;      // public/game
        public static string AssetDir; // public/game/assets

        static readonly Dictionary<string, JsonObject> indexes = new Dictionary<string, JsonObject>();
        static readonly Dictionary<(string, string), UnityEngine.Object> cache = new Dictionary<(string, string), UnityEngine.Object>();
        static readonly Dictionary<string, PrefabFile> prefabs = new Dictionary<string, PrefabFile>();
        static HashSet<string> prefabGuids, meshGuids;
        static Dictionary<string, (string kind, string guid)> resources;

        /// <summary>素材種類 → 載入函式（guid, fileID, 索引裡的那筆, 想要的型別）</summary>
        public static readonly Dictionary<string, Func<string, string, JsonObject, Type, UnityEngine.Object>> Loaders =
            new Dictionary<string, Func<string, string, JsonObject, Type, UnityEngine.Object>>();

        public static void Init(string gameDir)
        {
            Dir = gameDir;
            AssetDir = Path.Combine(gameDir, "assets");
            // 索引檔先在背景解析（頂層字典也展開）：開機第一次 KindOf 就會把十幾個索引全讀一遍，materials.json 一個在手機上就 300 ms
            foreach (var name in new List<string>(kindIndex.Values) { "builtin", "glyphs" })
            {
                var f = Path.Combine(AssetDir, name + ".json");
                if (File.Exists(f)) indexTasks[name] = System.Threading.Tasks.Task.Run(() => { var o = ParseFile(f); _ = o.Count; return o; });
            }
            prefabGuids = new HashSet<string>();
            var pd = Path.Combine(gameDir, "scenes", "prefabs");
            if (Directory.Exists(pd)) foreach (var f in Directory.EnumerateFiles(pd, "*.json")) prefabGuids.Add(Path.GetFileNameWithoutExtension(f));
            meshGuids = new HashSet<string>();
            var md = Path.Combine(AssetDir, "meshes");
            if (Directory.Exists(md)) foreach (var f in Directory.EnumerateFiles(md, "*.json")) meshGuids.Add(Path.GetFileNameWithoutExtension(f));
            // 預讀：參照到的 prefab、ScriptableObject 先在背景解析（兩個集合之後只讀，背景執行緒查得安全）
            var objectGuids = new HashSet<string>();
            var od = Path.Combine(AssetDir, "objects");
            if (Directory.Exists(od)) foreach (var f in Directory.EnumerateFiles(od, "*.json")) objectGuids.Add(Path.GetFileNameWithoutExtension(f));
            var pg = prefabGuids;
            Prefetch.Init(g => pg.Contains(g) ? Path.Combine(Dir, "scenes/prefabs/" + g + ".json") : objectGuids.Contains(g) ? Path.Combine(AssetDir, "objects/" + g + ".json") : null);
            Loaders["prefab"] = (g, f, r, w) => Prefab(g)?.Get(f, w);
            Loaders["monobehaviour"] = (g, f, r, w) => ScriptableObjects.Load(g, f, r);
        }

        static readonly Dictionary<string, System.Threading.Tasks.Task<JsonObject>> indexTasks = new Dictionary<string, System.Threading.Tasks.Task<JsonObject>>();

        public static JsonObject Index(string name)
        {
            if (indexes.TryGetValue(name, out var o)) return o;
            var f = Path.Combine(AssetDir, name + ".json");
            if (indexTasks.Remove(name, out var t)) o = t.GetAwaiter().GetResult();
            else o = File.Exists(f) ? ParseFile(f) : new JsonObject();
            indexes[name] = o;
            return o;
        }

        public static JsonObject Record(string index, string guid) => Index(index)[guid] as JsonObject;

        public static JsonObject ReadJson(string relative) => ParseFile(Path.Combine(Dir, relative));
        public static JsonObject ReadAssetJson(string relative) => ParseFile(Path.Combine(AssetDir, relative));

        /// <summary>直接從 UTF-8 讀（ReadAllText 先轉成 UTF-16 字串、Parse 再轉回 UTF-8，大檔多配置兩三倍）</summary>
        public static JsonObject ParseFile(string path)
        {
            using var fs = File.OpenRead(path);
            return (JsonObject)JsonNode.Parse(fs);
        }

        /// <summary>只讀不改的大檔（ScriptableObject、場景、prefab）：JsonDocument，欄位直接從 JsonElement 讀，不建 JsonNode 樹。
        /// 不 Dispose：讀出來的 JsonElement（或包成的 JsonNode）還指著它的緩衝區，物件不用了 GC 自己收</summary>
        public static System.Text.Json.JsonElement ParseDocFile(string path) => Prefetch.Take(path);

        public static string AssetPath(string relative) => Path.Combine(AssetDir, relative);

        /// <summary>guid（和 fileID、欄位型別）是哪一種素材</summary>
        public static string KindOf(string guid, string fileId, Type want)
        {
            if (guid == BuiltinExtra || guid == BuiltinDefault) return "builtin";
            if (prefabGuids.Contains(guid)) return "prefab";
            if (want == typeof(Sprite) && Index("sprites").ContainsKey(guid)) return "sprite";
            if (fileId == "2800000" && Index("textures").ContainsKey(guid)) return "texture";
            if (Index("sprites").ContainsKey(guid)) return "sprite";
            if (Index("textures").ContainsKey(guid)) return "texture";
            if (Index("monobehaviours").ContainsKey(guid)) return "monobehaviour";
            if (Index("materials").ContainsKey(guid)) return "material";
            if (Index("shaders").ContainsKey(guid)) return "shader";
            if (Index("texts").ContainsKey(guid)) return "text";
            if (Index("audioclips").ContainsKey(guid)) return "audioclip";
            if (Index("fonts").ContainsKey(guid)) return "font";
            if (Index("animations").ContainsKey(guid)) return "animation";
            if (Index("animators").ContainsKey(guid)) return "animator";
            if (Index("audiomixers").ContainsKey(guid)) return "audiomixer";
            if (Index("physicMaterials").ContainsKey(guid)) return "physicmaterial";
            if (meshGuids.Contains(guid)) return "mesh";
            return null;
        }

        static readonly Dictionary<string, string> kindIndex = new Dictionary<string, string>
        {
            ["sprite"] = "sprites", ["texture"] = "textures", ["monobehaviour"] = "monobehaviours", ["material"] = "materials", ["shader"] = "shaders",
            ["text"] = "texts", ["audioclip"] = "audioclips", ["font"] = "fonts", ["animation"] = "animations", ["animator"] = "animators",
            ["audiomixer"] = "audiomixers", ["physicmaterial"] = "physicMaterials",
        };

        static readonly HashSet<string> warned = new HashSet<string>();

        public static UnityEngine.Object Load(string guid, string fileId, Type want = null)
        {
            want ??= typeof(UnityEngine.Object);
            var key = (guid, fileId);
            if (cache.TryGetValue(key, out var hit))
            {
                if ((object)hit == null || hit != null) return hit;
                cache.Remove(key); // 已經摧毀
            }
            var kind = KindOf(guid, fileId, want);
            UnityEngine.Object r = null;
            if (kind == null)
            {
                foreach (var f in Missing) if ((r = f(guid, fileId, want)) != null) break;
                if (r == null) Warn("素材庫找不到 " + guid + ":" + fileId + "（" + want.Name + "）");
            }
            else
            {
                JsonObject rec = null;
                if (kind == "builtin") rec = Index("builtin")[guid + ":" + fileId] as JsonObject;
                else if (kindIndex.TryGetValue(kind, out var idx)) rec = Record(idx, guid);
                if (Loaders.TryGetValue(kind == "builtin" ? "builtin:" + (rec?["type"]?.ToString() ?? "") : kind, out var fn))
                {
                    try { r = fn(guid, fileId, rec, want); }
                    catch (Exception e) { Engine.LogException(e); }
                }
                else Warn("素材種類 " + kind + " 還沒有載入函式（" + guid + "）");
            }
            // prefab 的 Get 自己有快取（同一個 guid 不同 fileID），其他照 (guid, fileID) 快取
            if (kind != "prefab") cache[key] = r;
            return r;
        }

        /// <summary>素材庫沒有的素材，由各模組補（例如 build 時清空、AssetRipper 沒匯出的動態字型 atlas）</summary>
        public static readonly List<Func<string, string, Type, UnityEngine.Object>> Missing = new List<Func<string, string, Type, UnityEngine.Object>>();

        public static void Warn(string msg)
        {
            if (warned.Add(msg)) Engine.Log(LogType.Warning, msg);
        }

        /// <summary>已經載入的素材登記到快取（Resources.Load、Addressables 用同一份物件）</summary>
        public static void Remember(string guid, string fileId, UnityEngine.Object o) => cache[(guid, fileId)] = o;

        public static PrefabFile Prefab(string guid)
        {
            if (prefabs.TryGetValue(guid, out var p)) return p;
            if (!prefabGuids.Contains(guid)) return null;
            p = new PrefabFile(guid);
            prefabs[guid] = p;
            p.Load();
            return p;
        }

        // ───────── Resources ─────────

        /// <summary>Resources.Load 的路徑（Resources/ 之後、不含副檔名）→ 素材</summary>
        public static UnityEngine.Object LoadResource(string path, Type want)
        {
            if (resources == null)
            {
                resources = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in kindIndex)
                    foreach (var p in Index(kv.Value))
                    {
                        var src = (p.Value as JsonObject)?["src"]?.ToString();
                        AddResource(src, kv.Key, p.Key);
                    }
                // prefab：scenes/index.json 的 prefabs 有 src（Resources.Load<元件>("Prefabs/…") 拿 prefab 根物件上的元件，Diceomancer 的地圖背景靠它）
                if (ReadJson("scenes/index.json")?["prefabs"] is JsonObject prefabs)
                    foreach (var p in prefabs) AddResource((p.Value as JsonObject)?["src"]?.ToString(), "prefab", p.Key);
            }
            path = path.Replace('\\', '/');
            // build 的 ResourceManager 容器（匯入器的 supplement）：同一個路徑可能有好幾個物件，回第一個型別對的
            if (Container().TryGetValue(path, out var refs))
                foreach (var (g, f) in refs)
                {
                    var o = Load(g, f, want);
                    if (o == null) continue;
                    if (want.IsInstanceOfType(o)) return o;
                    var fit = Serializer.Fit(o, want);
                    if (fit != null) return fit;
                }
            if (!resources.TryGetValue(path, out var hit)) return null;
            var fileId = hit.kind switch { "monobehaviour" => "11400000", "text" => "4900000", "texture" => "2800000", "material" => "2100000", "prefab" => "100100000", _ => "0" };
            var r = Load(hit.guid, fileId, want);
            return want.IsInstanceOfType(r) ? r : Serializer.Fit(r, want);
        }

        static Dictionary<string, List<(string guid, string fileId)>> container;

        /// <summary>assets/resources.json：build 的 ResourceManager 容器，路徑（小寫）→ 匯出參照（對不到的跳過）。舊的匯入沒有這個檔，就是空的</summary>
        static Dictionary<string, List<(string guid, string fileId)>> Container()
        {
            if (container != null) return container;
            container = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
            var f = Path.Combine(AssetDir, "resources.json");
            if (!File.Exists(f)) return container;
            foreach (var kv in ParseFile(f))
            {
                var list = new List<(string, string)>();
                if (kv.Value is JsonArray a)
                    foreach (var r in a)
                        if (r?["guid"] != null) list.Add((r["guid"].ToString(), r["fileID"]?.ToString() ?? "0"));
                container[kv.Key] = list;
            }
            return container;
        }

        /// <summary>Resources.LoadAll：路徑是檔案就是它底下的全部物件（主資產與子資產），是資料夾就是底下全部；照型別篩</summary>
        public static UnityEngine.Object[] LoadAllResources(string path, Type want)
        {
            path = (path ?? "").Replace('\\', '/').Trim('/');
            var outList = new List<UnityEngine.Object>();
            foreach (var kv in Container())
            {
                if (path.Length > 0 && !kv.Key.Equals(path, StringComparison.OrdinalIgnoreCase) && !kv.Key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var (g, f) in kv.Value)
                {
                    var o = Load(g, f, want);
                    if (o != null && want.IsInstanceOfType(o) && !outList.Contains(o)) outList.Add(o);
                }
            }
            if (outList.Count == 0)
            {
                var one = LoadResource(path, want);
                if (one != null) outList.Add(one);
            }
            return outList.ToArray();
        }

        static void AddResource(string src, string kind, string guid)
        {
            if (src == null) return;
            int i = src.IndexOf("Resources/", StringComparison.Ordinal);
            if (i < 0 || (i > 0 && src[i - 1] != '/')) return;
            var rel = src.Substring(i + "Resources/".Length);
            var dot = rel.LastIndexOf('.');
            if (dot > 0) rel = rel.Substring(0, dot);
            // .atlas.txt 這種雙副檔名：Unity 的路徑只拿掉最後一個
            resources[rel] = (kind, guid);
        }
    }
}
