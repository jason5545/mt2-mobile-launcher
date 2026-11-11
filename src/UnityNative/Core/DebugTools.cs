// 除錯：桌面版 --ctl 與 Android 的 ctl 檔共用的查詢（find：名稱相同的 GameObject 的啟用狀態、RectTransform、元件）
using System;
using System.Linq;
using UnityEngine;

namespace UnityNative
{
    public static class DebugTools
    {
        /// <summary>ctl 的 eval：「組件|型別.成員.成員…」從靜態成員開始一路用反射讀（欄位、屬性、無參數方法都行，非公開的也讀），
        /// 印出結果；可列舉的印前 30 項。例：eval Unity.InputSystem|UnityEngine.InputSystem.InputSystem.devices
        /// 也可以從場景物件的元件開始：「@物件名:元件型別名.成員…」（同名物件取第一個活著的，物件名 * 是任何物件；成員的索引用 [n]，例：@setting:GameObjectLocalizer.m_TrackedObjects[0].Target）</summary>
        public static void Eval(string expr, Action<string> log)
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;
            try
            {
                object cur = null; Type ct; string[] parts; int k;
                string rest = expr;
                if (expr.StartsWith("@"))
                {
                    // @物件名:元件型別.成員…
                    int colon = expr.IndexOf(':');
                    var goName = expr[1..colon];
                    var restC = expr[(colon + 1)..];
                    parts = restC.Split('.');
                    // 物件名是 * 的話，找任何物件上第一個這個型別的元件
                    // 名字裡有 / 的照完整路徑比（同名物件很多時用）
                    static string PathOf(NGameObject g) { var s = g.Name; for (var t = g.Transform?.Parent; t != null; t = t.Parent) s = t.Go?.Name + "/" + s; return s; }
                    var gos = Native.All.ToList().OfType<NGameObject>().Where(x => x.Alive && (goName == "*" || (goName.Contains('/') ? PathOf(x).EndsWith(goName) : x.Name == goName)));
                    cur = gos.SelectMany(x => x.Components).Select(c => c.Managed).FirstOrDefault(m => m != null && m.GetType().Name == parts[0]);
                    if (cur == null) { log("[eval] " + goName + " 找不到元件 " + parts[0]); return; }
                    ct = cur.GetType(); k = 0;
                }
                else
                {
                    int bar = expr.IndexOf('|');
                    string asm = bar > 0 ? expr[..bar] : null;
                    rest = bar > 0 ? expr[(bar + 1)..] : expr;
                    // 型別名是最長的、找得到的前綴
                    parts = rest.Split('.');
                    Type t = null; k = parts.Length;
                    for (; k > 0 && t == null; k--) t = Types.Find(asm, string.Join(".", parts[..k]));
                    if (t == null) { log("[eval] 找不到型別：" + rest); return; }
                    ct = t;
                }
                for (int i = k + 1; i < parts.Length; i++)
                {
                    var m = parts[i];
                    int idx = -1;
                    if (m.EndsWith("]") && m.IndexOf('[') is int lb && lb > 0) { idx = int.Parse(m[(lb + 1)..^1]); m = m[..lb]; }
                    object next = null; bool found = false;
                    // 從自己往基底找（子類別的同名成員優先，例如 Mouse.current 蓋過 Pointer.current）
                    for (var bt = ct; bt != null && !found; bt = bt.BaseType)
                    {
                        const System.Reflection.BindingFlags D = F | System.Reflection.BindingFlags.DeclaredOnly;
                        if (bt.GetField(m, D) is System.Reflection.FieldInfo f) { next = f.GetValue(f.IsStatic ? null : cur); found = true; }
                        else if (bt.GetProperty(m, D) is System.Reflection.PropertyInfo pi) { next = pi.GetValue(pi.GetMethod.IsStatic ? null : cur); found = true; }
                        else if (bt.GetMethod(m, D, null, Type.EmptyTypes, null) is System.Reflection.MethodInfo mi) { next = mi.Invoke(mi.IsStatic ? null : cur, null); found = true; }
                    }
                    if (!found) { log("[eval] " + ct.FullName + " 沒有成員 " + m); return; }
                    if (idx >= 0 && next is System.Collections.IEnumerable seq) next = seq.Cast<object>().ElementAtOrDefault(idx);
                    cur = next;
                    if (cur == null) { log("[eval] " + string.Join(".", parts[..(i + 1)]) + " = null"); return; }
                    ct = cur.GetType();
                }
                if (cur is System.Collections.IEnumerable en && cur is not string)
                {
                    int n = 0;
                    foreach (var x in en) { if (n < 30) log("[eval]   [" + n + "] " + x); n++; }
                    log("[eval] " + rest + "：" + ct.Name + "，共 " + n + " 項");
                }
                else log("[eval] " + rest + " = " + cur + "（" + ct.Name + "）");
            }
            catch (Exception e) { log("[eval] " + (e.InnerException ?? e).GetType().Name + "：" + (e.InnerException ?? e).Message); }
        }

        /// <summary>ctl「where 名稱或路徑」：物件中心在視窗上的座標（左上原點，ctl 的 click 直接用）。UI 用它所在 Canvas 的攝影機（Overlay 直接是螢幕座標）</summary>
        public static void Where(string name, Action<string> log)
        {
            static string PathOf(NGameObject g) { var s = g.Name; for (var t = g.Transform?.Parent; t != null; t = t.Parent) s = t.Go?.Name + "/" + s; return s; }
            int n = 0;
            foreach (var g in Native.All.ToList().OfType<NGameObject>())
            {
                if (!g.Alive || g.Scene == null || !(name.Contains('/') ? PathOf(g).EndsWith(name) : g.Name == name)) continue;
                if (g.Managed is not GameObject go || !go.activeInHierarchy) continue;
                Vector3 world; Camera cam = Camera.main;
                if (go.transform is RectTransform rt)
                {
                    var corners = new Vector3[4];
                    rt.GetWorldCorners(corners);
                    world = (corners[0] + corners[2]) * 0.5f;
                    var canvas = go.GetComponentInParent<Canvas>()?.rootCanvas;
                    if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay) cam = null;
                    else if (canvas != null && canvas.worldCamera != null) cam = canvas.worldCamera;
                }
                else world = go.GetComponent<Collider2D>() is Collider2D col ? col.bounds.center : go.transform.position;
                var sp = cam != null ? cam.WorldToScreenPoint(world) : world;
                float wx = sp.x + ScreenFit.OffsetX, wy = (Engine.Config.WindowHeight > 0 ? Engine.Config.WindowHeight : Engine.Config.ScreenHeight) - (sp.y + ScreenFit.OffsetY);
                log($"[where] {PathOf(g)} → {wx:F0} {wy:F0}");
                if (++n >= 8) break;
            }
            if (n == 0) log("[where] 找不到啟用中的 " + name);
        }

        /// <summary>ctl「pick x y」：這一點每台攝影機的射線打到哪些 2D、3D 碰撞器（舊版滑鼠事件 SendMouseEvents 的命中，由近到遠）</summary>
        public static void Pick(float x, float y, Action<string> log)
        {
            static string PathOf(GameObject go)
            {
                var path = go != null ? go.name : "null";
                for (var t = go != null ? go.transform.parent : null; t != null; t = t.parent) path = t.name + "/" + path;
                return path;
            }
            foreach (var cam in Camera.allCameras)
            {
                if (!cam.pixelRect.Contains(new Vector2(x, y)) || cam.eventMask == 0) continue;
                var ray = cam.ScreenPointToRay(new Vector3(x, y, 0));
                float z = ray.direction.z;
                float dist = Mathf.Approximately(0, z) ? float.PositiveInfinity : Mathf.Abs((cam.farClipPlane - cam.nearClipPlane) / z);
                int mask = cam.cullingMask & cam.eventMask;
                var h2 = Physics2DQuery.RayIntersection(ray, dist, mask);
                var h3 = Physics3D.Cast(ray, dist, mask, QueryTriggerInteraction.UseGlobal);
                log($"[pick] ({x},{y}) 攝影機 {PathOf(cam.gameObject)} depth={cam.depth} clear={cam.clearFlags} 2D {h2.Count} 個、3D {h3.Count} 個");
                foreach (var (c, d, pt) in h2) log($"    2D {PathOf(c.Go?.Managed as GameObject)} {c.GetType().Name} 距離 {d:F3} z={c.Tr.Position.z:F3} layer={c.Go.Layer}");
                foreach (var r in h3) log($"    3D {PathOf((Native.ById(r.m_Collider) as NComponent)?.Go?.Managed as GameObject)} 距離 {r.m_Distance:F3}");
            }
        }

        /// <summary>ctl 的 hide：名稱相同的 GameObject 全部 SetActive(false)（找畫面上某個東西是誰畫的）</summary>
        public static void Hide(string name, Action<string> log)
        {
            int n = 0;
            foreach (var o in Native.All.ToList())
                if (o is NGameObject g && g.Alive && g.Name == name && g.Managed is UnityEngine.GameObject go) { go.SetActive(false); n++; }
            log("[hide] " + name + "：" + n + " 個");
        }

        /// <summary>ctl 的 find：名稱相同的 GameObject 印出路徑、啟用狀態、RectTransform 與每個元件（Image 的 sprite、文字元件的內容用反射讀）</summary>
        public static void Find(string name, Action<string> log)
        {
            foreach (var o in Native.All.ToList())
            {
                if (o is not NGameObject g || !g.Alive || g.Name != name) continue;
                var path = g.Name;
                for (var p = g.Parent; p != null; p = p.Parent) path = p.Name + "/" + path;
                var tr = g.Transform;
                log($"[find] {path} self={g.ActiveSelf} hier={g.ActiveInHierarchy} rect={tr?.Rect} scale={tr?.LocalScale} 世界位置={tr?.Position} 本地位置={tr?.LocalPosition} anchor={tr?.AnchorMin}-{tr?.AnchorMax} anchored={tr?.AnchoredPosition} size={tr?.SizeDelta} pivot={tr?.Pivot}" + ((object)tr?.DrivenBy != null ? $" drivenBy={tr.DrivenBy.GetType().Name}" : "")
                    + $" 子物件 {tr?.Children.Count ?? 0}：{string.Join("、", (tr?.Children ?? new System.Collections.Generic.List<NTransform>()).Take(12).Select(k => k.Go?.Name))}");
                foreach (var c in g.Components)
                {
                    var m = c.Managed;
                    var t = m?.GetType();
                    string extra = "";
                    if (t?.GetProperty("sprite")?.GetValue(m) is UnityEngine.Object sp) extra += " sprite=" + sp.name;
                    else if (t?.GetProperty("sprite") != null) extra += " sprite=null";
                    if (t?.GetProperty("text")?.GetValue(m) is string tx) extra += $" text=\"{tx}\"";
                    if (t?.GetProperty("color")?.GetValue(m) is UnityEngine.Color col) extra += " color=" + col;
                    if (c is NCanvasRenderer cr)
                        extra += $" 頂點={cr.Geo?.VertexCount ?? -1} cull={cr.Cull} cullTransparent={cr.CullTransparentMesh} color={cr.Color} 繼承alpha={cr.InheritedAlpha()} tex={(cr.Texture != null ? cr.Texture.name : "null")} 索引={cr.Geo?.Indices.Length ?? -1} 材質數={cr.MaterialCount}/{cr.Materials.Count} 深度={cr.AbsoluteDepth}"
                            + (cr.Geo != null && cr.Geo.VertexCount > 0 ? $" 第一點=({cr.Geo.Pos[0]:F1},{cr.Geo.Pos[1]:F1},{cr.Geo.Pos[2]:F1}) 第三點=({cr.Geo.Pos[6]:F1},{cr.Geo.Pos[7]:F1})" : "");
                    log($"    {t?.Name} enabled={(c is NBehaviour b ? b.Enabled : true)}{extra}");
                    // CanvasRenderer 的材質：名稱、shader、材質自己設的屬性（沒列的用 shader 預設）
                    if (c is NCanvasRenderer cr2)
                        foreach (var mm in cr2.Materials)
                        {
                            var nm = Native.Of<NMaterial>(mm);
                            if (nm == null) { log("      材質 null"); continue; }
                            var p = nm.Props;
                            log($"      材質 {nm.Name} shader={nm.Shader?.Name} queue={nm.RenderQueue} keywords=[{string.Join(",", nm.Keywords)}] "
                                + string.Join(" ", System.Linq.Enumerable.Select(p.Floats, kv => PropertyIds.Name(kv.Key) + "=" + kv.Value))
                                + " " + string.Join(" ", System.Linq.Enumerable.Select(p.Ints, kv => PropertyIds.Name(kv.Key) + "=" + kv.Value))
                                + " " + string.Join(" ", System.Linq.Enumerable.Select(p.Vectors, kv => PropertyIds.Name(kv.Key) + "=" + kv.Value))
                                + " " + string.Join(" ", System.Linq.Enumerable.Select(p.Textures, kv => PropertyIds.Name(kv.Key) + "=" + (kv.Value.Texture != null ? kv.Value.Texture.name : "null"))));
                        }
                }
            }
        }

    }
}
