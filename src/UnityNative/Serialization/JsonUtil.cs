// JsonUtility：跟場景序列化同一套欄位規則（SerialFields），格式照 Unity 的 JsonUtility：
//   bool 是 true／false、enum 是整數、UnityEngine.Object 是 {"instanceID": n}、Color32 是 {r,g,b,a}。
using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public static class JsonUtil
    {
        public static object FromJson(string json, object overwrite, Type type)
        {
            if (string.IsNullOrEmpty(json)) return overwrite;
            var node = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            if (node is not JsonObject o) throw new ArgumentException("JSON parse error: Invalid value.");
            var target = overwrite;
            if (target == null)
            {
                if (typeof(UnityEngine.Object).IsAssignableFrom(type)) throw new ArgumentException("Cannot deserialize JSON to new instances of type '" + type.Name + ".'");
                target = type.IsValueType ? Activator.CreateInstance(type) : Activator.CreateInstance(type, true);
            }
            var ctx = new LoadContext();
            Serializer.Populate(target, o, ctx);
            Loader.AfterDeserialize(ctx);
            return target;
        }

        public static string ToJson(object obj, bool pretty)
        {
            if (obj == null) return "";
            if (obj is ISerializationCallbackReceiver r) r.OnBeforeSerialize();
            var sb = new StringBuilder();
            WriteObject(sb, obj, pretty, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, bool pretty, int depth)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', depth * 4);
        }

        static void WriteObject(StringBuilder sb, object obj, bool pretty, int depth)
        {
            sb.Append('{');
            bool first = true;
            foreach (var f in SerialFields.Of(obj.GetType()))
            {
                if (!first) sb.Append(',');
                first = false;
                Indent(sb, pretty, depth + 1);
                WriteString(sb, f.Name);
                sb.Append(pretty ? ": " : ":");
                WriteValue(sb, f.GetValue(obj), f.FieldType, pretty, depth + 1);
            }
            if (!first) Indent(sb, pretty, depth);
            sb.Append('}');
        }

        static void WriteValue(StringBuilder sb, object v, Type t, bool pretty, int depth)
        {
            if (t == typeof(string)) { WriteString(sb, (string)v ?? ""); return; }
            if (t.IsEnum) { sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
            if (t == typeof(bool)) { sb.Append((bool)v ? "true" : "false"); return; }
            if (t == typeof(float)) { sb.Append(Num((float)v)); return; }
            if (t == typeof(double)) { sb.Append(((double)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (t == typeof(char)) { sb.Append((int)(char)v); return; }
            if (t.IsPrimitive) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            if (typeof(UnityEngine.Object).IsAssignableFrom(t))
            {
                var n = Native.Of(v as UnityEngine.Object);
                sb.Append("{\"instanceID\":").Append(n?.InstanceId ?? 0).Append('}');
                return;
            }
            if (v is IList list && (t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>))))
            {
                var et = t.IsArray ? t.GetElementType() : t.GetGenericArguments()[0];
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Indent(sb, pretty, depth + 1);
                    WriteValue(sb, list[i], et, pretty, depth + 1);
                }
                if (list.Count > 0) Indent(sb, pretty, depth);
                sb.Append(']');
                return;
            }
            if (v == null && (t.IsArray || t.IsGenericType)) { sb.Append("[]"); return; }
            switch (v)
            {
                case Vector2 a: sb.Append("{\"x\":").Append(Num(a.x)).Append(",\"y\":").Append(Num(a.y)).Append('}'); return;
                case Vector3 a: sb.Append("{\"x\":").Append(Num(a.x)).Append(",\"y\":").Append(Num(a.y)).Append(",\"z\":").Append(Num(a.z)).Append('}'); return;
                case Vector4 a: sb.Append("{\"x\":").Append(Num(a.x)).Append(",\"y\":").Append(Num(a.y)).Append(",\"z\":").Append(Num(a.z)).Append(",\"w\":").Append(Num(a.w)).Append('}'); return;
                case Quaternion a: sb.Append("{\"x\":").Append(Num(a.x)).Append(",\"y\":").Append(Num(a.y)).Append(",\"z\":").Append(Num(a.z)).Append(",\"w\":").Append(Num(a.w)).Append('}'); return;
                case Color a: sb.Append("{\"r\":").Append(Num(a.r)).Append(",\"g\":").Append(Num(a.g)).Append(",\"b\":").Append(Num(a.b)).Append(",\"a\":").Append(Num(a.a)).Append('}'); return;
                case Color32 a: sb.Append("{\"r\":").Append(a.r).Append(",\"g\":").Append(a.g).Append(",\"b\":").Append(a.b).Append(",\"a\":").Append(a.a).Append('}'); return;
                case Rect a: sb.Append("{\"serializedVersion\":\"2\",\"x\":").Append(Num(a.x)).Append(",\"y\":").Append(Num(a.y)).Append(",\"width\":").Append(Num(a.width)).Append(",\"height\":").Append(Num(a.height)).Append('}'); return;
                case LayerMask a: sb.Append("{\"serializedVersion\":\"2\",\"m_Bits\":").Append((uint)a.value).Append('}'); return;
            }
            if (v == null)
            {
                // Unity 不寫 null 的可序列化類別：給一個空的
                try { v = t.IsValueType ? Activator.CreateInstance(t) : Activator.CreateInstance(t, true); } catch { sb.Append("{}"); return; }
            }
            WriteObject(sb, v, pretty, depth);
        }

        static string Num(float f)
        {
            if (float.IsNaN(f)) return "NaN";
            if (float.IsInfinity(f)) return f > 0 ? "Infinity" : "-Infinity";
            return f.ToString("R", CultureInfo.InvariantCulture);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_JsonUtility
    {
        public static string ToJsonInternal(object obj, bool prettyPrint) => JsonUtil.ToJson(obj, prettyPrint);
        public static object FromJsonInternal(string json, object objectToOverwrite, Type type) => JsonUtil.FromJson(json, objectToOverwrite, type);
    }
}
