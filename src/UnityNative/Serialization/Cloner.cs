// Object.Instantiate：照 Unity 的語意複製「序列化的狀態」——
//   GameObject：整棵子樹的 GameObject 和元件都複製，欄位裡指到子樹內部的參照改成指到複製品，指到外面的照舊；
//   沒序列化的欄位不複製（跟 Unity 一樣，複製品的這些欄位是建構子給的值）。
//   其他物件（ScriptableObject、材質、網格…）複製單一個。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace UnityNative
{
    public static class Cloner
    {
        public sealed class Map
        {
            public readonly Dictionary<UnityEngine.Object, UnityEngine.Object> Objects = new Dictionary<UnityEngine.Object, UnityEngine.Object>(ReferenceEqualityComparer.Instance);
        }

        public static GameObject CloneGameObject(NGameObject src, NTransform parent, bool worldStays, Vector3? pos, Quaternion? rot, NScene scene = null)
        {
            var map = new Map();
            var srcAll = Hierarchy.Collect(src);
            var pairs = new List<(NComponent s, NComponent d)>();

            // 1. 先建 GameObject 與元件（腳本跑建構子）
            var goMap = new Dictionary<NGameObject, NGameObject>();
            foreach (var sg in srcAll)
            {
                var dg = Native.Bind(new NGameObject { Name = sg.Name + (sg == src ? "(Clone)" : ""), Layer = sg.Layer, Tag = sg.Tag, ActiveSelf = sg.ActiveSelf, IsStatic = sg.IsStatic },
                    Native.NewShell(typeof(GameObject)), false);
                goMap[sg] = dg;
                map.Objects[sg.Managed] = dg.Managed;
                foreach (var sc in sg.Components)
                {
                    if (!sc.Alive) continue;
                    NComponent dc;
                    if (sc is NTransform st)
                    {
                        var dt = Native.Bind(new NTransform { Go = dg, Name = dg.Name }, Native.NewShell(sc.Managed.GetType()), false);
                        dt.CopyFrom(st);
                        dg.Transform = dt;
                        dg.Components.Add(dt);
                        dc = dt;
                    }
                    else if (sc is NMonoBehaviour smb)
                    {
                        var dmb = Loader.NewBehaviour(dg, sc.Managed.GetType(), false);
                        dmb.Enabled = smb.Enabled;
                        dc = dmb;
                    }
                    else
                    {
                        dc = (NComponent)Activator.CreateInstance(sc.GetType());
                        dc.Go = dg;
                        dc.Name = dg.Name;
                        Native.Bind(dc, Native.NewShell(sc.Managed.GetType()), false);
                        dg.Components.Add(dc);
                    }
                    map.Objects[sc.Managed] = dc.Managed;
                    pairs.Add((sc, dc));
                }
            }

            // 2. 階層
            foreach (var sg in srcAll)
            {
                var dg = goMap[sg];
                foreach (var ch in sg.Transform.Children)
                {
                    if (ch.Go == null || !goMap.TryGetValue(ch.Go, out var dch)) continue;
                    dch.Transform.Parent = dg.Transform;
                    dg.Transform.Children.Add(dch.Transform);
                }
            }
            var root = goMap[src];

            // 3. 欄位（腳本的 managed 欄位、原生元件的資料）
            var after = new List<object>();
            foreach (var (s, d) in pairs)
            {
                if (s is NTransform) continue;
                if (s is NMonoBehaviour)
                {
                    if (s.Managed is ISerializationCallbackReceiver sr) { try { sr.OnBeforeSerialize(); } catch (Exception e) { Engine.LogException(e); } }
                    CopyFields(s.Managed, d.Managed, map);
                    if (d.Managed is ISerializationCallbackReceiver) after.Add(d.Managed);
                }
                else
                {
                    try { d.CopyFrom(s); } catch (Exception e) { Engine.LogException(e, d); }
                }
            }
            foreach (var (s, d) in pairs) if (!(s is NMonoBehaviour)) RemapNative(d, map);
            foreach (var o in after) { try { ((ISerializationCallbackReceiver)o).OnAfterDeserialize(); } catch (Exception e) { Engine.LogException(e); } }

            // 4. 放進場景、父物件
            var tr = root.Transform;
            var targetScene = parent?.Go?.Scene ?? scene ?? Scenes.Active;
            foreach (var g in goMap.Values) g.Scene = targetScene;
            if (parent != null)
            {
                // 跟 Unity 一樣：先算出原本的世界座標，掛到父物件底下（worldStays）或保留本地值
                Vector3 wp = src.Transform.Position; Quaternion wr = src.Transform.Rotation;
                tr.Parent = parent;
                parent.Children.Add(tr);
                if (pos.HasValue) { tr.Position = pos.Value; tr.Rotation = rot ?? Quaternion.identity; }
                else if (worldStays) { tr.Position = wp; tr.Rotation = wr; }
            }
            else
            {
                targetScene?.Roots.Add(root);
                if (pos.HasValue) { tr.LocalPosition = pos.Value; tr.LocalRotation = rot ?? Quaternion.identity; }
                else if (src.Transform.Parent != null) { tr.LocalPosition = src.Transform.Position; tr.LocalRotation = src.Transform.Rotation; }
            }
            tr.MarkDirty();
            tr.SyncRect();

            // 5. 啟用
            var activeList = new List<NGameObject>();
            void Mark(NGameObject g, bool parentActive)
            {
                g.ActiveInHierarchy = parentActive && g.ActiveSelf;
                if (g.ActiveInHierarchy) activeList.Add(g);
                foreach (var c in g.Transform.Children) if (c.Go != null) Mark(c.Go, g.ActiveInHierarchy);
            }
            Mark(root, parent == null || parent.Go.ActiveInHierarchy);
            if (parent != null) Lifecycle.SendToScripts(parent.Go, Messages.OnTransformChildrenChanged);
            // 根 Canvas 的 RectTransform 不在 Instantiate 當下驅動（NCanvas.Cloning）
            NCanvas.Cloning++;
            try { Lifecycle.Activate(activeList); }
            finally { NCanvas.Cloning--; }
            return root.GO;
        }

        /// <summary>原生元件的 Data 是共用的 JSON；參照（材質、sprite）已經在 CopyFrom 處理。這裡留給要改指向的元件</summary>
        static void RemapNative(NComponent d, Map map)
        {
            if (d is IRemap r) r.Remap(o => (object)o != null && map.Objects.TryGetValue(o, out var n) ? n : o);
        }

        public static void CopyFields(object src, object dst, Map map, int depth = 0)
        {
            foreach (var f in SerialFields.Of(src.GetType()))
            {
                var v = f.GetValue(src);
                f.SetValue(dst, CloneValue(v, f.FieldType, map, depth));
            }
        }

        static object CloneValue(object v, Type t, Map map, int depth)
        {
            if (v == null) return null;
            if (t.IsPrimitive || t.IsEnum || t == typeof(string)) return v;
            if (v is UnityEngine.Object uo) return map.Objects.TryGetValue(uo, out var m) ? m : uo;
            if (depth > 10) return v;
            if (v is Array arr)
            {
                var et = t.IsArray ? t.GetElementType() : arr.GetType().GetElementType();
                var na = Array.CreateInstance(et, arr.Length);
                for (int i = 0; i < arr.Length; i++) na.SetValue(CloneValue(arr.GetValue(i), et, map, depth + 1), i);
                return na;
            }
            if (v is IList list && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var et = t.GetGenericArguments()[0];
                var nl = (IList)Activator.CreateInstance(t, list.Count);
                foreach (var x in list) nl.Add(CloneValue(x, et, map, depth + 1));
                return nl;
            }
            if (v is RectOffset ro) return new RectOffset(ro.left, ro.right, ro.top, ro.bottom);
            if (v is AnimationCurve curve) return new AnimationCurve(curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
            if (v is Gradient g) { var ng = new Gradient(); ng.SetKeys(g.colorKeys, g.alphaKeys); ng.mode = g.mode; return ng; }
            var vt = v.GetType();
            if (vt.IsValueType)
            {
                if (SerialFields.Of(vt).Length == 0) return v;
                var box = RuntimeHelpers.GetUninitializedObject(vt);
                CopyRaw(v, box);
                CopyFields(v, box, map, depth + 1);
                return box;
            }
            if (!SerialFields.IsSerializable(vt, false)) return v;
            // 巢狀的 [Serializable] 物件也照 Unity 的 Instantiate（序列化再反序列化）：來源先 OnBeforeSerialize、複本讀完欄位 OnAfterDeserialize。
            // 例：RotaryHeart 的 SerializableDictionary 讀檔後把 _keys／_values 清掉、只留 _dict（不序列化），只複製欄位的話複本是空字典
            //（MT2 手牌的 MasterySpriteSelector 查不到名牌、外框的 sprite，整張卡少了名牌與外框，10/19）
            if (v is ISerializationCallbackReceiver sr) { try { sr.OnBeforeSerialize(); } catch (Exception e) { Engine.LogException(e); } }
            object o;
            try { o = Activator.CreateInstance(vt, true); }
            catch { o = RuntimeHelpers.GetUninitializedObject(vt); }
            CopyFields(v, o, map, depth + 1);
            if (o is ISerializationCallbackReceiver dr) { try { dr.OnAfterDeserialize(); } catch (Exception e) { Engine.LogException(e); } }
            return o;
        }

        static void CopyRaw(object src, object dst)
        {
            foreach (var f in src.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) f.SetValue(dst, f.GetValue(src));
        }

        // ───────── 單一物件 ─────────

        public static UnityEngine.Object CloneSingle(UnityEngine.Object src)
        {
            var n = Native.Of(src);
            switch (n)
            {
                case NGameObject go: return CloneGameObject(go, null, false, null, null);
                case NComponent c: return ((GameObject)CloneGameObject(c.Go, null, false, null, null)).GetComponent(src.GetType());
                case NScriptableObject so:
                    {
                        var dst = (ScriptableObject)RuntimeHelpers.GetUninitializedObject(src.GetType());
                        var dn = Native.Bind(new NScriptableObject { Name = so.Name + "(Clone)", Info = so.Info }, dst, false);
                        Loader.RunCtor(dst);
                        if (src is ISerializationCallbackReceiver sr) { try { sr.OnBeforeSerialize(); } catch (Exception e) { Engine.LogException(e); } }
                        CopyFields(src, dst, new Map());
                        if (dst is ISerializationCallbackReceiver r) r.OnAfterDeserialize();
                        Lifecycle.AwakeScriptableObject(dn);
                        return dst;
                    }
                case ICloneableAsset ca: return ca.CloneAsset();
            }
            throw new NotSupportedException("UnityNative：Instantiate 還不支援 " + src.GetType().Name);
        }
    }

    /// <summary>原生元件裡有物件參照的（Instantiate 後要改指向複製品）</summary>
    public interface IRemap { void Remap(Func<UnityEngine.Object, UnityEngine.Object> f); }
    /// <summary>可以 Instantiate 的素材（材質、網格、貼圖）</summary>
    public interface ICloneableAsset { UnityEngine.Object CloneAsset(); }
}
