// 匯入器的 JSON（AssetRipper 匯出的 YAML 轉過來的）→ managed 欄位。
// 欄位型別是已知的，所以 YAML 的含糊處照型別解：
//   - 一維陣列、List 的基本型別寫成十六進位字串（Unity 對小陣列的寫法，例如 "0e000000"）就照元素大小拆
//   - bool 是 0／1；Color32 是 {rgba: uint}（r 在最低位元組）；LayerMask 是 {m_Bits}
//   - 物件參照 {fileID}（同一個檔）或 {guid, fileID}（素材）；null 或 fileID 0 是 null
// JSON 沒有的欄位保留建構子給的值（跟 Unity 一樣）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    /// <summary>一個檔（場景、prefab、ScriptableObject）裡的物件：同檔參照用 fileID 找</summary>
    public sealed class LoadContext
    {
        public readonly Dictionary<string, NObject> ByFileId = new Dictionary<string, NObject>();
        public string Guid;
        public string Source;
        public readonly List<object> AfterDeserialize = new List<object>();

        public UnityEngine.Object Local(string fileId) => ByFileId.TryGetValue(fileId, out var n) ? n.Managed : null;
    }

    public static partial class Serializer
    {
        static readonly Dictionary<Type, Dictionary<string, FieldInfo>> byFormerName = new Dictionary<Type, Dictionary<string, FieldInfo>>();

        /// <summary>把 data 的欄位填進 target（target 已經跑過建構子）</summary>
        public static void Populate(object target, JsonObject data, LoadContext ctx)
        {
            var t = target.GetType();
            foreach (var f in SerialFields.Of(t))
            {
                if (!data.TryGetPropertyValue(f.Name, out var node))
                {
                    bool found = false;
                    foreach (var alt in Formers(f)) if (data.TryGetPropertyValue(alt, out node)) { found = true; break; }
                    if (!found) continue;
                }
                try
                {
                    if (node == null && IsSerializeReference(f)) { f.SetValue(target, null); continue; }
                    var v = Convert(node, f.FieldType, ctx, f.GetValue(target));
                    f.SetValue(target, v);
                    // 資產欄位指向場景物件（{guid: 場景, fileID}）：場景（再）載入時接上（SceneRefs）
                    if (node is JsonObject so && so["guid"]?.ToString() is string sg && sg != ctx?.Guid && SceneRefs.IsScene(sg)
                        && typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
                        SceneRefs.Track(target, f, sg, so["fileID"]?.ToString());
                }
                catch (Exception e)
                {
                    Assets.Warn("序列化：" + t.Name + "." + f.Name + "（" + f.FieldType.Name + "）讀不進來：" + e.GetType().Name + " " + e.Message + " " + Short(node?.ToJsonString() ?? "null"));
                }
            }
            AfterFields(target, ctx);
        }

        /// <summary>欄位填完：ISerializationCallbackReceiver 照 Unity 在整份讀完後叫 OnAfterDeserialize。
        /// 結構例外：這裡拿到的是裝箱的副本，回傳後才複製進欄位，要在那之前叫，不然改到的是丟掉的副本
        ///（Unity Localization 的 TableReference、TableEntryReference 靠它把 "GUID:…"、keyId 解析成參照）</summary>
        static void AfterFields(object target, LoadContext ctx)
        {
            if (target is not ISerializationCallbackReceiver cb) return;
            if (!target.GetType().IsValueType) { ctx.AfterDeserialize.Add(target); return; }
            try { cb.OnAfterDeserialize(); } catch (Exception e) { Engine.LogException(e); }
        }

        static readonly Dictionary<FieldInfo, string[]> formers = new Dictionary<FieldInfo, string[]>();

        /// <summary>[FormerlySerializedAs] 的舊名（可以有好幾個，Unity 逐一試）</summary>
        static string[] Formers(FieldInfo f)
        {
            if (formers.TryGetValue(f, out var r)) return r;
            var attrs = f.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>(false);
            var list = new List<string>();
            foreach (var a in attrs) if (!string.IsNullOrEmpty(a.oldName)) list.Add(a.oldName);
            formers[f] = r = list.ToArray();
            return r;
        }

        public static object Convert(JsonNode node, Type t, LoadContext ctx, object existing = null)
        {
            if (t == typeof(string))
            {
                if (node == null) return "";
                if (node is JsonValue sv) return sv.TryGetValue<string>(out var s) ? s : sv.ToJsonString();
                return node.ToJsonString();
            }
            if (t.IsEnum)
            {
                var ut = Enum.GetUnderlyingType(t);
                return Enum.ToObject(t, Convert(node, ut, ctx));
            }
            if (t.IsPrimitive) return Prim(node, t);
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return Ref(node, t, ctx);
            if (LazyRefs.Is(t))
            {
                var guid = node?["guid"]?.ToString();
                bool now = string.IsNullOrEmpty(guid) || (ctx?.Guid != null && guid == ctx.Guid) || SceneRefs.IsScene(guid);
                return LazyRefs.Make(t, guid, node?["fileID"]?.ToString(), now ? () => Ref(node, typeof(UnityEngine.Object), ctx) : null);
            }
            if (t.IsArray)
            {
                var et = t.GetElementType();
                var items = Items(node, et, ctx);
                var arr = Array.CreateInstance(et, items.Count);
                for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                return arr;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var et = t.GetGenericArguments()[0];
                var items = Items(node, et, ctx);
                var list = (IList)Activator.CreateInstance(t, items.Count);
                foreach (var it in items) list.Add(it);
                return list;
            }
            if (t == typeof(Vector2)) return new Vector2(F(node, "x"), F(node, "y"));
            if (t == typeof(Vector3)) return new Vector3(F(node, "x"), F(node, "y"), F(node, "z"));
            if (t == typeof(Vector4)) return new Vector4(F(node, "x"), F(node, "y"), F(node, "z"), F(node, "w"));
            if (t == typeof(Vector2Int)) return new Vector2Int((int)F(node, "x"), (int)F(node, "y"));
            if (t == typeof(Vector3Int)) return new Vector3Int((int)F(node, "x"), (int)F(node, "y"), (int)F(node, "z"));
            if (t == typeof(Quaternion)) return node == null ? Quaternion.identity : new Quaternion(F(node, "x"), F(node, "y"), F(node, "z"), F(node, "w"));
            if (t == typeof(Color)) return new Color(F(node, "r"), F(node, "g"), F(node, "b"), F(node, "a"));
            if (t == typeof(Color32)) return ToColor32(node);
            // PropertyName（ExposedReference.exposedName）：存檔裡是 id 本身
            if (t == typeof(PropertyName)) return new PropertyName { id = node is JsonValue pv ? (int)Prim(pv, typeof(int)) : node is JsonObject po ? (int)Prim(po["id"], typeof(int)) : 0 };
            if (t == typeof(Rect)) return new Rect(F(node, "x"), F(node, "y"), F(node, "width"), F(node, "height"));
            if (t == typeof(RectInt)) return new RectInt((int)F(node, "x"), (int)F(node, "y"), (int)F(node, "width"), (int)F(node, "height"));
            if (t == typeof(Bounds))
            {
                var c = (Vector3)Convert(node?["m_Center"], typeof(Vector3), ctx);
                var e = (Vector3)Convert(node?["m_Extent"], typeof(Vector3), ctx);
                return new Bounds(c, e * 2f);
            }
            if (t == typeof(LayerMask)) return (LayerMask)(int)(uint)D(node is JsonObject o ? o["m_Bits"] : node);
            if (t == typeof(Matrix4x4))
            {
                var m = new Matrix4x4();
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) m[r, c] = F(node, "e" + r + c);
                return m;
            }
            if (t == typeof(Hash128))
            {
                if (node is JsonObject ho && ho["Hash"] is JsonValue hv) return Hash128.Parse(hv.ToString());
                return default(Hash128);
            }
            if (t == typeof(RectOffset))
                return new RectOffset((int)F(node, "m_Left"), (int)F(node, "m_Right"), (int)F(node, "m_Top"), (int)F(node, "m_Bottom"));
            if (t == typeof(AnimationCurve)) return Curves.FromJson(node);
            if (t == typeof(Gradient)) return Curves.GradientFromJson(node);

            // [SerializeReference]：實際型別在 $type／$ns／$asm（匯入器的 supplement 從 build 讀出來寫的）
            if (node is JsonObject ro && ro["$type"] is JsonNode tn)
            {
                var rt = RefType(tn.ToString(), ro["$ns"]?.ToString(), ro["$asm"]?.ToString(), t);
                if (rt == null) return null;
                var inst = rt.IsValueType ? Activator.CreateInstance(rt) : New(rt);
                Populate(inst, ro, ctx);
                return inst;
            }
            // 介面、抽象類別只會是 SerializeReference 的欄位：沒有 $type 就是空參照
            if (t.IsInterface || t.IsAbstract) return null;

            // [Serializable] 的類別或結構：null 也照 Unity 建一個新的
            object obj = existing;
            if (obj == null || t.IsValueType) obj = t.IsValueType ? (existing ?? Activator.CreateInstance(t)) : New(t);
            if (obj == null) return null;
            if (node is JsonObject jo) Populate(obj, jo, ctx);
            return obj;
        }

        static readonly Dictionary<(string, string, string), Type> refTypes = new Dictionary<(string, string, string), Type>();

        /// <summary>SerializeReference 的型別：類別名、命名空間、組件（巢狀類別 Unity 寫成 外層/內層）。找不到或不是欄位的型別就記一筆、當成空參照</summary>
        static Type RefType(string cls, string ns, string asm, Type declared)
        {
            var key = (cls, ns ?? "", asm ?? "");
            if (!refTypes.TryGetValue(key, out var rt))
            {
                rt = Types.Find(asm, (string.IsNullOrEmpty(ns) ? "" : ns + ".") + cls.Replace('/', '+'));
                refTypes[key] = rt;
                if (rt == null) Assets.Warn("SerializeReference 找不到型別 " + asm + " " + ns + "." + cls);
            }
            if (rt != null && !declared.IsAssignableFrom(rt)) { Assets.Warn("SerializeReference 的型別 " + rt + " 不是 " + declared); return null; }
            return rt;
        }

        static readonly Dictionary<FieldInfo, bool> serializeRef = new Dictionary<FieldInfo, bool>();

        /// <summary>[SerializeReference] 欄位：JSON 是 null 時就是 null（一般 [Serializable] 欄位會建一個新的）</summary>
        static bool IsSerializeReference(FieldInfo f)
        {
            if (!serializeRef.TryGetValue(f, out var r)) serializeRef[f] = r = f.IsDefined(typeof(UnityEngine.SerializeReference), false);
            return r;
        }

        /// <summary>沒有無參數建構子的型別：記下來，下次直接 GetUninitializedObject（例外很貴，載入大的 ScriptableObject 時同一型別會建上千次）</summary>
        static readonly HashSet<Type> noCtor = new HashSet<Type>();

        static object New(Type t)
        {
            if (!noCtor.Contains(t))
            {
                try { return Activator.CreateInstance(t, true); }
                catch (MissingMethodException) { noCtor.Add(t); }
            }
            return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        }

        static List<object> Items(JsonNode node, Type et, LoadContext ctx)
        {
            var list = new List<object>();
            if (node == null) return list;
            if (node is JsonArray a)
            {
                foreach (var x in a) list.Add(Convert(x, et, ctx));
                return list;
            }
            // 十六進位：基本型別與 enum 的小陣列。全是數字的十六進位字串會被 YAML／JSON 讀成數字（"07000000" → 7000000），
            // 前導的 0 不見了：照元素大小補回到整數倍的長度
            if ((et.IsPrimitive || et.IsEnum) && node is JsonValue v)
            {
                var pt = et.IsEnum ? Enum.GetUnderlyingType(et) : et;
                int size = pt == typeof(bool) ? 1 : pt == typeof(char) ? 2 : System.Runtime.InteropServices.Marshal.SizeOf(pt);
                string hex = null;
                if (v.TryGetValue<string>(out var str)) hex = str.Replace("/", "f"); // 匯入的資料裡 -1（ffffffff）有時寫成 "ffffff/f"
                else
                {
                    double d = D(v);
                    if (d >= 0 && d == Math.Floor(d) && d < 9e15)
                    {
                        hex = ((long)d).ToString(CultureInfo.InvariantCulture);
                        int unit = size * 2;
                        int len = (hex.Length + unit - 1) / unit * unit;
                        hex = hex.PadLeft(len, '0');
                    }
                }
                if (hex != null && hex.Length % 2 == 0 && IsHex(hex))
                {
                    var bytes = new byte[hex.Length / 2];
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    for (int off = 0; off + size <= bytes.Length; off += size)
                    {
                        long raw = 0;
                        for (int b = 0; b < size; b++) raw |= (long)bytes[off + b] << (8 * b);
                        object val = FromRaw(raw, pt, size);
                        list.Add(et.IsEnum ? Enum.ToObject(et, val) : val);
                    }
                    return list;
                }
            }
            Engine.Log(LogType.Warning, "序列化：" + et.Name + " 的陣列欄位拿到 " + Short(node.ToJsonString()));
            return list;
        }

        static bool IsHex(string s)
        {
            foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        static string Short(string s) => s.Length > 80 ? s.Substring(0, 80) + "…" : s;

        static object FromRaw(long raw, Type pt, int size)
        {
            if (pt == typeof(bool)) return raw != 0;
            if (pt == typeof(byte)) return (byte)raw;
            if (pt == typeof(sbyte)) return (sbyte)raw;
            if (pt == typeof(short)) return (short)raw;
            if (pt == typeof(ushort)) return (ushort)raw;
            if (pt == typeof(char)) return (char)raw;
            if (pt == typeof(int)) return (int)raw;
            if (pt == typeof(uint)) return (uint)raw;
            if (pt == typeof(long)) return raw;
            if (pt == typeof(ulong)) return (ulong)raw;
            if (pt == typeof(float)) return BitConverter.Int32BitsToSingle((int)raw);
            if (pt == typeof(double)) return BitConverter.Int64BitsToDouble(raw);
            return 0;
        }

        public static Color32 ToColor32(JsonNode node)
        {
            uint rgba = 0xffffffff;
            if (node is JsonObject c && c["r"] != null && c["rgba"] == null)
                return new Color32((byte)D(c["r"]), (byte)D(c["g"]), (byte)D(c["b"]), (byte)D(c["a"]));
            if (node is JsonObject o && o["rgba"] != null) rgba = (uint)D(o["rgba"]);
            return new Color32((byte)(rgba & 0xff), (byte)((rgba >> 8) & 0xff), (byte)((rgba >> 16) & 0xff), (byte)(rgba >> 24));
        }

        static object Prim(JsonNode node, Type t)
        {
            if (t == typeof(bool))
            {
                if (node is JsonValue bv && bv.TryGetValue<bool>(out var b)) return b;
                return D(node) != 0;
            }
            if (t == typeof(char))
            {
                if (node is JsonValue cv && cv.TryGetValue<string>(out var s) && s.Length == 1) return s[0];
                return (char)(int)D(node);
            }
            double d = D(node);
            if (t == typeof(float)) return (float)d;
            if (t == typeof(double)) return d;
            if (t == typeof(int)) return d > int.MaxValue ? unchecked((int)(uint)d) : (int)d;
            if (t == typeof(uint)) return (uint)d;
            if (t == typeof(long)) return L(node);
            if (t == typeof(ulong)) return (ulong)L(node);
            if (t == typeof(short)) return (short)d;
            if (t == typeof(ushort)) return (ushort)d;
            if (t == typeof(byte)) return (byte)d;
            if (t == typeof(sbyte)) return (sbyte)d;
            return System.Convert.ChangeType(d, t);
        }

        public static double D(JsonNode node)
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<double>(out var d)) return d;
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
                if (v.TryGetValue<string>(out var s))
                {
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                    if (s == "Infinity") return double.PositiveInfinity;
                    if (s == "-Infinity") return double.NegativeInfinity;
                    if (s == "NaN") return double.NaN;
                }
            }
            return 0;
        }

        static long L(JsonNode node)
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<ulong>(out var ul)) return unchecked((long)ul);
                if (v.TryGetValue<string>(out var s) && long.TryParse(s, out l)) return l;
            }
            return (long)D(node);
        }

        public static float F(JsonNode node, string key) => node is JsonObject o && o[key] != null ? (float)D(o[key]) : 0f;

        // ───────── 物件參照 ─────────

        public static UnityEngine.Object Ref(JsonNode node, Type want, LoadContext ctx)
        {
            if (node is not JsonObject o) return null;
            if (o["instanceID"] != null) return Fit(Native.ById((int)D(o["instanceID"]))?.Managed, want);
            var fileId = o["fileID"]?.ToString();
            if (string.IsNullOrEmpty(fileId) || fileId == "0") return null;
            var guid = o["guid"]?.ToString();
            UnityEngine.Object r;
            if (string.IsNullOrEmpty(guid) || (ctx?.Guid != null && guid == ctx.Guid)) r = ctx?.Local(fileId);
            else if (SceneRefs.IsScene(guid)) r = SceneRefs.Resolve(guid, fileId);
            else r = Assets.Load(guid, fileId, want);
            return Fit(r, want);
        }

        /// <summary>參照到的物件跟欄位型別不合時照 Unity 的習慣轉：GameObject ↔ 元件</summary>
        public static UnityEngine.Object Fit(UnityEngine.Object r, Type want)
        {
            if ((object)r == null) return null;
            if (want.IsInstanceOfType(r)) return r;
            if (r is GameObject go && typeof(Component).IsAssignableFrom(want))
            {
                var n = Native.Of<NGameObject>(go);
                foreach (var c in n.Components) if (want.IsInstanceOfType(c.Managed)) return c.Managed;
                return null;
            }
            if (r is Component comp && want == typeof(GameObject)) return comp.gameObject;
            if (r is Texture2D && want == typeof(Sprite)) return null;
            return null;
        }
    }
}
