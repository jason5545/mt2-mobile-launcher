// Serializer 的 JsonElement 版：ScriptableObject、場景與 prefab 的腳本欄位直接從 JsonDocument 讀，不建 JsonNode 樹。
// JsonNode 每個值都是一個物件、每個 JsonObject 一份字典，MT2 開機的資料圖（四千多個檔）光這些就配置幾百 MB；手機上 Mono 跑這段最慢。
// 規則跟 JsonNode 版（Serializer.cs）逐條相同；不常見的型別（AnimationCurve、Bounds、十六進位陣列…）包成 JsonNode 交給原本那條，結果不會兩樣。
// 每個型別的欄位只算一次（欄位、UTF-8 名稱、FormerlySerializedAs、型別種類），讀值時不再一條條比型別。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public static partial class Serializer
    {
        /// <summary>Convert 的分支（照 JsonNode 版的判斷順序）</summary>
        enum Kind : byte { String, Enum, Prim, Ref, Array, List, Vector2, Vector3, Vector4, Quaternion, Color, Node, Composite, Lazy }

        sealed class TypeInfo
        {
            public Kind K;
            public Type Elem;          // 陣列、List 的元素型別
            public TypeInfo ElemInfo;
            public Type Under;         // enum 的底層型別
            public bool IsUnityObject;
        }

        sealed class FieldPlan
        {
            public FieldInfo F;
            public byte[] Name;
            public byte[][] Former;
            public TypeInfo T;
        }

        /// <summary>一個型別的欄位計畫，加上 JSON 屬性名（UTF-8）→ 欄位的雜湊表：屬性只掃一遍，不用每個欄位各掃一次</summary>
        sealed class TypePlan
        {
            public FieldPlan[] Fields;
            int[] slots;          // 開放定址：0 空的，否則 entry 編號 + 1
            byte[][] names;
            int[] field, rank;    // rank 0 是欄位名，1、2… 是第幾個 FormerlySerializedAs

            public TypePlan(FieldPlan[] fields)
            {
                Fields = fields;
                var ns = new List<byte[]>(); var fi = new List<int>(); var rk = new List<int>();
                for (int i = 0; i < fields.Length; i++)
                {
                    ns.Add(fields[i].Name); fi.Add(i); rk.Add(0);
                    for (int k = 0; k < fields[i].Former.Length; k++) { ns.Add(fields[i].Former[k]); fi.Add(i); rk.Add(k + 1); }
                }
                names = ns.ToArray(); field = fi.ToArray(); rank = rk.ToArray();
                int cap = 8; while (cap < names.Length * 2) cap <<= 1;
                slots = new int[cap];
                for (int e = 0; e < names.Length; e++)
                {
                    int h = Hash(names[e]) & (cap - 1);
                    while (slots[h] != 0) h = (h + 1) & (cap - 1);
                    slots[h] = e + 1;
                }
            }

            static int Hash(ReadOnlySpan<byte> s)
            {
                uint h = 2166136261;
                foreach (var b in s) h = (h ^ b) * 16777619;
                return (int)(h & 0x7fffffff);
            }

            /// <summary>名字對到的 entry（同一個名字可能是一個欄位的主名、另一個欄位的舊名：逐一回呼）</summary>
            public void Match(ReadOnlySpan<byte> name, Span<int> best, Span<JsonElement> found, JsonElement value)
            {
                int mask = slots.Length - 1, h = Hash(name) & mask;
                while (slots[h] != 0)
                {
                    int e = slots[h] - 1;
                    if (name.SequenceEqual(names[e]))
                    {
                        // 主名優先於舊名、舊名照屬性順序；同名的屬性後面的蓋前面的（JsonElement.TryGetProperty 從後面找）
                        int f = field[e], r = rank[e];
                        if (best[f] < 0 || r <= best[f]) { best[f] = r; found[f] = value; }
                    }
                    h = (h + 1) & mask;
                }
            }
        }

        static readonly Dictionary<Type, TypeInfo> typeInfos = new Dictionary<Type, TypeInfo>();
        static readonly Dictionary<Type, TypePlan> plans = new Dictionary<Type, TypePlan>();

        static TypeInfo Info(Type t)
        {
            if (typeInfos.TryGetValue(t, out var ti)) return ti;
            ti = new TypeInfo { IsUnityObject = typeof(UnityEngine.Object).IsAssignableFrom(t) };
            typeInfos[t] = ti;
            if (t == typeof(string)) ti.K = Kind.String;
            else if (t.IsEnum) { ti.K = Kind.Enum; ti.Under = Enum.GetUnderlyingType(t); }
            else if (t.IsPrimitive) ti.K = Kind.Prim;
            else if (ti.IsUnityObject) ti.K = Kind.Ref;
            else if (LazyRefs.Is(t)) ti.K = Kind.Lazy;
            else if (t.IsArray) { ti.K = Kind.Array; ti.Elem = t.GetElementType(); ti.ElemInfo = Info(ti.Elem); }
            else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) { ti.K = Kind.List; ti.Elem = t.GetGenericArguments()[0]; ti.ElemInfo = Info(ti.Elem); }
            else if (t == typeof(Vector2)) ti.K = Kind.Vector2;
            else if (t == typeof(Vector3)) ti.K = Kind.Vector3;
            else if (t == typeof(Vector4)) ti.K = Kind.Vector4;
            else if (t == typeof(Quaternion)) ti.K = Kind.Quaternion;
            else if (t == typeof(Color)) ti.K = Kind.Color;
            else if (t == typeof(Vector2Int) || t == typeof(Vector3Int) || t == typeof(Color32) || t == typeof(PropertyName) || t == typeof(Rect) || t == typeof(RectInt)
                     || t == typeof(Bounds) || t == typeof(LayerMask) || t == typeof(Matrix4x4) || t == typeof(Hash128) || t == typeof(RectOffset)
                     || t == typeof(AnimationCurve) || t == typeof(Gradient)) ti.K = Kind.Node;
            else ti.K = Kind.Composite;
            return ti;
        }

        static TypePlan Plan(Type t)
        {
            if (plans.TryGetValue(t, out var tp)) return tp;
            var fs = SerialFields.Of(t);
            var p = new FieldPlan[fs.Length];
            for (int i = 0; i < fs.Length; i++)
            {
                var former = Array.ConvertAll(Formers(fs[i]), n => Encoding.UTF8.GetBytes(n));
                p[i] = new FieldPlan { F = fs[i], Name = Encoding.UTF8.GetBytes(fs[i].Name), Former = former, T = Info(fs[i].FieldType) };
            }
            plans[t] = tp = new TypePlan(p);
            return tp;
        }

        /// <summary>Populate 的 JsonElement 版</summary>
        public static void Populate(object target, JsonElement data, LoadContext ctx)
        {
            var t = target.GetType();
            var tp = Plan(t);
            var fields = tp.Fields;
            if (fields.Length == 0) { AfterFields(target, ctx); return; }
            var found = System.Buffers.ArrayPool<JsonElement>.Shared.Rent(fields.Length);
            var best = System.Buffers.ArrayPool<int>.Shared.Rent(fields.Length);
            try
            {
            var bestS = best.AsSpan(0, fields.Length);
            bestS.Fill(-1);
            foreach (var prop in data.EnumerateObject())
            {
                var raw = System.Runtime.InteropServices.JsonMarshal.GetRawUtf8PropertyName(prop);
                // 有跳脫字元的名字（匯入的資料沒有）：轉回原字再比
                if (raw.IndexOf((byte)'\\') >= 0) tp.Match(Encoding.UTF8.GetBytes(prop.Name), bestS, found, prop.Value);
                else tp.Match(raw, bestS, found, prop.Value);
            }
            for (int i = 0; i < fields.Length; i++)
            {
                if (bestS[i] < 0) continue;
                var p = fields[i];
                var el = found[i];
                try
                {
                    if (el.ValueKind == JsonValueKind.Null && p.T.K == Kind.Composite && IsSerializeReference(p.F)) { p.F.SetValue(target, null); continue; }
                    var v = Convert(el, p.F.FieldType, p.T, ctx, p.T.K == Kind.Composite ? p.F.GetValue(target) : null);
                    p.F.SetValue(target, v);
                    // 資產欄位指向場景物件（{guid: 場景, fileID}）：場景（再）載入時接上（SceneRefs）
                    if (p.T.IsUnityObject && el.ValueKind == JsonValueKind.Object && el.TryGetProperty("guid", out var g) && Str(g) is string sg && sg != ctx?.Guid && SceneRefs.IsScene(sg))
                        SceneRefs.Track(target, p.F, sg, el.TryGetProperty("fileID", out var fid) ? Str(fid) : null);
                }
                catch (Exception e)
                {
                    Assets.Warn("序列化：" + t.Name + "." + p.F.Name + "（" + p.F.FieldType.Name + "）讀不進來：" + e.GetType().Name + " " + e.Message + "（" + e.StackTrace?.Split('\n')[0].Trim() + "）" + Short(el.ValueKind == JsonValueKind.Null ? "null" : el.GetRawText()));
                }
            }
            }
            finally
            {
                Array.Clear(found, 0, fields.Length);
                System.Buffers.ArrayPool<JsonElement>.Shared.Return(found);
                System.Buffers.ArrayPool<int>.Shared.Return(best);
            }
            AfterFields(target, ctx);
        }

        public static object Convert(JsonElement el, Type t, LoadContext ctx) => Convert(el, t, Info(t), ctx, null);

        static object Convert(JsonElement el, Type t, TypeInfo ti, LoadContext ctx, object existing)
        {
            // Null 是 JSON 的 null，Undefined 是欄位不存在（JsonNode 版兩個都是 null）
            bool isNull = el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined;
            switch (ti.K)
            {
                case Kind.String:
                    if (isNull) return "";
                    if (el.ValueKind == JsonValueKind.String) return el.GetString();
                    return ToNode(el).ToJsonString();
                case Kind.Enum: return Enum.ToObject(t, Convert(el, ti.Under, Info(ti.Under), ctx, null));
                case Kind.Prim: return Prim(el, t);
                case Kind.Ref: return Ref(el, t, ctx);
                case Kind.Lazy:
                {
                    var guid = Str(el, "guid");
                    bool now = string.IsNullOrEmpty(guid) || (ctx?.Guid != null && guid == ctx.Guid) || SceneRefs.IsScene(guid);
                    var copy = el;
                    return LazyRefs.Make(t, guid, Str(el, "fileID"), now ? () => Ref(copy, typeof(UnityEngine.Object), ctx) : null);
                }
                case Kind.Array:
                {
                    var items = Items(el, ti.Elem, ti.ElemInfo, ctx);
                    var arr = System.Array.CreateInstance(ti.Elem, items.Count);
                    for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                    return arr;
                }
                case Kind.List:
                {
                    var items = Items(el, ti.Elem, ti.ElemInfo, ctx);
                    var list = (IList)Activator.CreateInstance(t, items.Count);
                    foreach (var it in items) list.Add(it);
                    return list;
                }
                case Kind.Vector2: return new Vector2(F(el, "x"), F(el, "y"));
                case Kind.Vector3: return new Vector3(F(el, "x"), F(el, "y"), F(el, "z"));
                case Kind.Vector4: return new Vector4(F(el, "x"), F(el, "y"), F(el, "z"), F(el, "w"));
                case Kind.Quaternion: return isNull ? Quaternion.identity : new Quaternion(F(el, "x"), F(el, "y"), F(el, "z"), F(el, "w"));
                case Kind.Color: return new Color(F(el, "r"), F(el, "g"), F(el, "b"), F(el, "a"));
                case Kind.Node: return Convert(ToNode(el), t, ctx);
                default:
                {
                    // [SerializeReference]：實際型別在 $type／$ns／$asm（同 JsonNode 版）
                    if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("$type"u8, out var tn))
                    {
                        var rt = RefType(Str(tn), Str(el, "$ns"), Str(el, "$asm"), t);
                        if (rt == null) return null;
                        var inst = rt.IsValueType ? Activator.CreateInstance(rt) : New(rt);
                        Populate(inst, el, ctx);
                        return inst;
                    }
                    if (t.IsInterface || t.IsAbstract) return null;
                    // [Serializable] 的類別或結構：null 也照 Unity 建一個新的
                    object obj = existing;
                    if (obj == null || t.IsValueType) obj = t.IsValueType ? (existing ?? Activator.CreateInstance(t)) : New(t);
                    if (obj == null) return null;
                    if (el.ValueKind == JsonValueKind.Object) Populate(obj, el, ctx);
                    return obj;
                }
            }
        }

        static List<object> Items(JsonElement el, Type et, TypeInfo eti, LoadContext ctx)
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                var list = new List<object>(el.GetArrayLength());
                foreach (var x in el.EnumerateArray()) list.Add(Convert(x, et, eti, ctx, null));
                return list;
            }
            // null 回空的；十六進位字串與其他情況交給 JsonNode 版
            return Items(ToNode(el), et, ctx);
        }

        /// <summary>JsonElement 包成 JsonNode（不複製，用到才展開）；JSON 的 null 是 null</summary>
        static JsonNode ToNode(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.Object => JsonObject.Create(el),
            JsonValueKind.Array => JsonArray.Create(el),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => JsonValue.Create(el),
        };

        /// <summary>JsonNode 的 ToString：字串不帶引號，null 是 null，其他是原文</summary>
        public static string Str(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText(),
        };

        /// <summary>物件的欄位（JsonNode 的 o[key]?.ToString()）</summary>
        public static string Str(JsonElement o, string key) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) ? Str(v) : null;

        static object Prim(JsonElement el, Type t)
        {
            if (t == typeof(bool))
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
                return D(el) != 0;
            }
            if (t == typeof(char))
            {
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: 1 } s) return s[0];
                return (char)(int)D(el);
            }
            double d = D(el);
            if (t == typeof(float)) return (float)d;
            if (t == typeof(double)) return d;
            if (t == typeof(int)) return d > int.MaxValue ? unchecked((int)(uint)d) : (int)d;
            if (t == typeof(uint)) return (uint)d;
            if (t == typeof(long)) return L(el);
            if (t == typeof(ulong)) return (ulong)L(el);
            if (t == typeof(short)) return (short)d;
            if (t == typeof(ushort)) return (ushort)d;
            if (t == typeof(byte)) return (byte)d;
            if (t == typeof(sbyte)) return (sbyte)d;
            return System.Convert.ChangeType(d, t);
        }

        public static double D(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Number:
                    if (el.TryGetDouble(out var d)) return d;
                    if (el.TryGetInt64(out var l)) return l;
                    return 0;
                case JsonValueKind.True: return 1;
                case JsonValueKind.False: return 0;
                case JsonValueKind.String:
                {
                    var s = el.GetString();
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
                    if (s == "Infinity") return double.PositiveInfinity;
                    if (s == "-Infinity") return double.NegativeInfinity;
                    if (s == "NaN") return double.NaN;
                    return 0;
                }
                default: return 0;
            }
        }

        static long L(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number)
            {
                if (el.TryGetInt64(out var l)) return l;
                if (el.TryGetUInt64(out var ul)) return unchecked((long)ul);
            }
            else if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var sl)) return sl;
            return (long)D(el);
        }

        static float F(JsonElement el, string key) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? (float)D(v) : 0f;

        public static UnityEngine.Object Ref(JsonElement el, Type want, LoadContext ctx)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (el.TryGetProperty("instanceID", out var iid) && iid.ValueKind != JsonValueKind.Null) return Fit(Native.ById((int)D(iid))?.Managed, want);
            var fileId = Str(el, "fileID");
            if (string.IsNullOrEmpty(fileId) || fileId == "0") return null;
            var guid = Str(el, "guid");
            UnityEngine.Object r;
            if (string.IsNullOrEmpty(guid) || (ctx?.Guid != null && guid == ctx.Guid)) r = ctx?.Local(fileId);
            else if (SceneRefs.IsScene(guid)) r = SceneRefs.Resolve(guid, fileId);
            else r = Assets.Load(guid, fileId, want);
            return Fit(r, want);
        }
    }
}
