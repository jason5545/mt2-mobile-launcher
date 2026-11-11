// AssetBundle 的原生半邊：不讀 bundle 檔本身。Addressables（或遊戲自己）照原版的 catalog 載入 bundle、
// 用素材路徑（"Assets/GameData/…/X.prefab"）要東西時，照匯入器的 addressables.json（路徑 → guid）交給素材庫。
// 這樣原版的 Addressables 程式整套照跑，只有最底下的 AssetBundle API 接到匯入好的資料。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public class NAssetBundle : NObject
    {
        public string Path;
        public bool Scenes;
    }

    public static class Bundles
    {
        static Dictionary<string, string> byPath;
        static readonly List<NAssetBundle> loaded = new List<NAssetBundle>();

        static Dictionary<string, string> Paths
        {
            get
            {
                if (byPath != null) return byPath;
                byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (Assets.Index("addressables")["addresses"] is JsonObject a)
                    foreach (var kv in a) byPath[kv.Key] = kv.Value?.ToString();
                return byPath;
            }
        }

        static Dictionary<string, string> bySrc;

        /// <summary>備用對照：素材自己記的 src（AssetRipper 匯出的路徑，Assets/ 底下）。addressables.json 沒收的素材靠這個</summary>
        static Dictionary<string, string> Sources
        {
            get
            {
                if (bySrc != null) return bySrc;
                bySrc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var idx in new[] { "textures", "sprites", "materials", "monobehaviours", "texts", "audioclips", "fonts", "animations", "animators", "shaders", "audiomixers" })
                    foreach (var kv in Assets.Index(idx))
                    {
                        var src = kv.Value?["src"]?.ToString();
                        if (src != null) bySrc.TryAdd("Assets/" + src, kv.Key);
                    }
                var pd = System.IO.Path.Combine(Assets.Dir, "scenes", "prefabs");
                if (System.IO.Directory.Exists(pd))
                    foreach (var f in System.IO.Directory.EnumerateFiles(pd, "*.json"))
                    {
                        // 檔頭就有 src，不整份讀
                        using var fs = System.IO.File.OpenRead(f);
                        var buf = new byte[1024];
                        int n = fs.Read(buf, 0, buf.Length);
                        var head = System.Text.Encoding.UTF8.GetString(buf, 0, n);
                        int i = head.IndexOf("\"src\":\"", StringComparison.Ordinal);
                        if (i < 0) continue;
                        int a = i + 7, b = head.IndexOf('"', a);
                        if (b <= a) continue;
                        var src = System.Text.RegularExpressions.Regex.Unescape(head.Substring(a, b - a));
                        bySrc.TryAdd("Assets/" + src, System.IO.Path.GetFileNameWithoutExtension(f));
                    }
                return bySrc;
            }
        }

        static Dictionary<string, List<string>> spritesOfTexture;

        /// <summary>
        /// 原版的 .png 匯入成 Sprite 時，Sprite 是貼圖的子素材；匯出資料把每個 Sprite 拆成自己的 guid。
        /// 以 Sprite 型別要 .png 時，照 sprites.json 的 m_RD.texture.guid 找回那張貼圖的 Sprite。
        /// 一張貼圖切成多個 Sprite 時，優先拿跟檔名同名的，沒有就拿第一個。
        /// </summary>
        static string SpriteOfTexture(string textureGuid, string name)
        {
            if (spritesOfTexture == null)
            {
                spritesOfTexture = new Dictionary<string, List<string>>();
                foreach (var kv in Assets.Index("sprites"))
                {
                    var tex = kv.Value?["m_RD"]?["texture"]?["guid"]?.ToString();
                    if (tex == null) continue;
                    if (!spritesOfTexture.TryGetValue(tex, out var l)) spritesOfTexture[tex] = l = new List<string>();
                    l.Add(kv.Key);
                }
                foreach (var l in spritesOfTexture.Values) l.Sort(StringComparer.Ordinal);
            }
            if (!spritesOfTexture.TryGetValue(textureGuid, out var list)) return null;
            var stem = System.IO.Path.GetFileNameWithoutExtension(name);
            foreach (var g in list)
                if (string.Equals(Assets.Record("sprites", g)?["m_Name"]?.ToString(), stem, StringComparison.OrdinalIgnoreCase)) return g;
            return list[0];
        }

        /// <summary>以 Sprite 型別要整張貼圖的子素材：回傳切出來的所有 Sprite（Sprite[]、IList&lt;Sprite&gt; 走這條）</summary>
        public static UnityEngine.Object[] SubSprites(string name, Type type)
        {
            if (type != typeof(Sprite)) return null;
            var guid = GuidOf(name);
            if (guid == null || Assets.Index("sprites").ContainsKey(guid)) return null;
            SpriteOfTexture(guid, name);
            if (!spritesOfTexture.TryGetValue(guid, out var list)) return null;
            var r = new List<UnityEngine.Object>();
            foreach (var g in list) if (Assets.Load(g, "21300000", typeof(Sprite)) is Sprite sp) r.Add(sp);
            return r.ToArray();
        }

        /// <summary>
        /// 子資產：原版放在主資產檔裡的物件（TMP 字型資產的 Atlas Material、atlas 貼圖），AssetRipper 拆成獨立檔、主資產改用參照指過去。
        /// 主資產是 ScriptableObject 時，它的序列化欄位參照到、型別對的資產都當子資產（Addressables 的「位址[子資產名]」再照名稱挑；
        /// Diceomancer 的 TextAndFont 資產表要「素材集市社会体 SDF[素材集市社会体 Atlas Material]」）
        /// </summary>
        public static UnityEngine.Object[] SubAssets(string name, Type type)
        {
            var main = LoadAsset(name, typeof(UnityEngine.Object));
            var r = new List<UnityEngine.Object>();
            if ((object)main == null) return r.ToArray();
            if (type.IsInstanceOfType(main)) r.Add(main);
            if (main is ScriptableObject so)
                foreach (var f in SerialFields.Of(so.GetType()))
                {
                    var v = f.GetValue(so);
                    if (v is UnityEngine.Object o) { if (type.IsInstanceOfType(o) && !r.Contains(o)) r.Add(o); }
                    else if (v is System.Collections.IEnumerable seq && v is not string)
                        foreach (var x in seq) if (x is UnityEngine.Object xo && type.IsInstanceOfType(xo) && !r.Contains(xo)) r.Add(xo);
                }
            return r.ToArray();
        }

        public static string GuidOf(string assetPath)
        {
            if (assetPath == null) return null;
            if (Paths.TryGetValue(assetPath, out var g)) return g;
            if (Sources.TryGetValue(assetPath, out g)) return g;
            // Addressables 的 key 有時是 GUID 本身
            if (assetPath.Length == 32 && Uri.IsHexDigit(assetPath[0])) return assetPath;
            return null;
        }

        public static UnityEngine.Object LoadAsset(string name, Type type)
        {
            var guid = GuidOf(name);
            if (guid == null) { Assets.Warn("AssetBundle：找不到素材路徑 " + name); return null; }
            type ??= typeof(UnityEngine.Object);
            var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            string fileId = ext switch
            {
                ".mat" => "2100000", ".png" or ".jpg" or ".tga" or ".psd" => type == typeof(Sprite) ? "21300000" : "2800000",
                ".asset" => "11400000", ".txt" or ".json" or ".bytes" or ".csv" => "4900000", ".controller" => "9100000", ".anim" => "7400000",
                ".wav" or ".ogg" or ".mp3" => "8300000", ".ttf" or ".otf" => "12800000", ".shader" => "4800000", _ => "0",
            };
            if (type == typeof(Sprite) && !Assets.Index("sprites").ContainsKey(guid) && SpriteOfTexture(guid, name) is string sg) guid = sg;
            var o = Assets.Load(guid, fileId, type);
            if ((object)o != null && !type.IsInstanceOfType(o)) Assets.Warn("AssetBundle：" + name + " → " + guid + " 是 " + o.GetType().Name + "，要的是 " + type.Name);
            return type.IsInstanceOfType(o) ? o : Serializer.Fit(o, type);
        }

        public static AssetBundle Open(string path)
        {
            var b = (AssetBundle)Native.NewShell(typeof(AssetBundle));
            var n = Native.Bind(new NAssetBundle { Name = System.IO.Path.GetFileNameWithoutExtension(path ?? ""), Path = path, Scenes = path != null && path.Contains("_scenes_") }, b, true);
            loaded.Add(n);
            return b;
        }

        public static AssetBundle[] All()
        {
            var r = new List<AssetBundle>();
            foreach (var n in loaded) if (n.Alive) r.Add((AssetBundle)n.Managed);
            return r.ToArray();
        }

        public static void Unload(NAssetBundle n) { loaded.Remove(n); Native.Release(n); }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_AssetBundle
    {
        static AssetBundleCreateRequest Req(AssetBundle b)
        {
            var op = NAsyncOperation.New<NAsyncOperation>(typeof(AssetBundleCreateRequest));
            op.Asset = b;
            op.Complete();
            return (AssetBundleCreateRequest)op.Managed;
        }

        public static UnityEngine.Object returnMainAsset(AssetBundle bundle) => null;
        public static void UnloadAllAssetBundles(bool unloadAllObjects) { foreach (var b in Bundles.All()) Bundles.Unload(Native.Of<NAssetBundle>(b)); }
        public static AssetBundle[] GetAllLoadedAssetBundles_Native() => Bundles.All();
        public static AssetBundleCreateRequest LoadFromFileAsync_Internal(string path, uint crc, ulong offset) => Req(Bundles.Open(path));
        public static AssetBundle LoadFromFile_Internal(string path, uint crc, ulong offset) => Bundles.Open(path);
        public static AssetBundleCreateRequest LoadFromMemoryAsync_Internal(byte[] binary, uint crc) => Req(Bundles.Open(null));
        public static AssetBundle LoadFromMemory_Internal(byte[] binary, uint crc) => Bundles.Open(null);
        public static bool get_isStreamedSceneAssetBundle(AssetBundle self) => Native.Self<NAssetBundle>(self).Scenes;
        public static bool Contains(AssetBundle self, string name) => Bundles.GuidOf(name) != null;
        public static UnityEngine.Object LoadAsset_Internal(AssetBundle self, string name, Type type) => Bundles.LoadAsset(name, type);

        public static AssetBundleRequest LoadAssetAsync_Internal(AssetBundle self, string name, Type type)
        {
            var op = NAsyncOperation.New<NAsyncOperation>(typeof(AssetBundleRequest));
            op.Asset = Bundles.LoadAsset(name, type);
            op.Complete();
            return (AssetBundleRequest)op.Managed;
        }

        public static UnityEngine.Object[] LoadAssetWithSubAssets_Internal(AssetBundle self, string name, Type type)
        {
            var subs = Bundles.SubSprites(name, type);
            if (subs != null) return subs;
            return Bundles.SubAssets(name, type ?? typeof(UnityEngine.Object));
        }

        public static AssetBundleRequest LoadAssetWithSubAssetsAsync_Internal(AssetBundle self, string name, Type type)
        {
            var op = NAsyncOperation.New<NAsyncOperation>(typeof(AssetBundleRequest));
            op.AllAssets = LoadAssetWithSubAssets_Internal(self, name, type);
            op.Asset = op.AllAssets.Length > 0 ? op.AllAssets[0] : null;
            op.Complete();
            return (AssetBundleRequest)op.Managed;
        }
        public static void Unload(AssetBundle self, bool unloadAllLoadedObjects) => Bundles.Unload(Native.Self<NAssetBundle>(self));
        public static string[] GetAllAssetNames(AssetBundle self) => Array.Empty<string>();
        public static string[] GetAllScenePaths(AssetBundle self) => Array.Empty<string>();
    }

    public static unsafe partial class UnityEngine_AssetBundleCreateRequest
    {
        public static AssetBundle get_assetBundle(AssetBundleCreateRequest self) => NAsyncOperation.Of(self)?.Asset as AssetBundle;
        public static void SetEnableCompatibilityChecks(AssetBundleCreateRequest self, bool set) { }
    }

    public static unsafe partial class UnityEngine_AssetBundleRequest
    {
        public static UnityEngine.Object GetResult(AssetBundleRequest self) => NAsyncOperation.Of(self)?.Asset;
        public static UnityEngine.Object[] get_allAssets(AssetBundleRequest self)
        {
            var op = NAsyncOperation.Of(self);
            if (op?.AllAssets != null) return op.AllAssets;
            var a = op?.Asset;
            return a == null ? Array.Empty<UnityEngine.Object>() : new[] { a };
        }
    }
}
