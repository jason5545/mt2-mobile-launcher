// MT2 在手機上的處理（只在 Android 生效），規則同 mt2-web 的 src/main.ts（Jason 10/18 定、10/19 搬到原生版）：
//   - 解析度照手機版（參考 MT1 的 Android 版）：畫面是手機的原生解析度、填滿螢幕，設定裡沒有解析度與全螢幕模式。
//     MT2 原版只有 Steam／Standalone 兩種平台服務，HasFixedResolution 都是 false；GraphicsSettingsManager 本來就有「平台解析度固定」的分支，Android 上回 true 走那條。
//   - 設定選單（SettingsDialog）與第一次的設定畫面（FirstTimeSettingsScreen）：Initialize 跑完把解析度、全螢幕模式兩列關掉，設定選單再照它結尾的做法重編隔行底色。
//   - 螢幕圓角：HUD 右上角的設定齒輪與頂端資訊列的右緣往內挪，讓齒輪整個落在圓角裡（圓角半徑由宿主給，EngineConfig.CornerRadius）。
//   - 目標幀率的清單：原版只在可以改解析度時建，固定解析度的分支只放螢幕更新率一個；照 RefreshResolutions 的規則補上比它小的 targetFrameratesSpecial。
//   - 上下滑動：原版只有滾輪跟搖桿，沒有滑動手勢。滑一下送一次滾輪，戰鬥換一層、地圖換一段（走原版的 HandleScrollWheel）；
//     按在卡片、手牌區、ScrollRect、Scrollbar、Slider 上（按下處接拖曳的物件或它的上層有這些元件）照原本的拖曳。
// 戰場的上下箭頭照原版留著（10/19 Jason：MT1 的 Android 版也有）。
using System;
using System.Linq;
using System.Reflection;
using ShinyShoe;
using UnityEngine;

namespace Glue.MT2
{
    public static class Phone
    {
        const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        // 桌面上驗證用：UN_PHONE=1 當成手機，UN_PHONE_CORNER=像素 當成圓角半徑
        static bool On => OperatingSystem.IsAndroid() || Environment.GetEnvironmentVariable("UN_PHONE") == "1";
        static int Corner => int.TryParse(Environment.GetEnvironmentVariable("UN_PHONE_CORNER"), out var c) ? c : UnityNative.Engine.Config?.CornerRadius ?? 0;
        static T F<T>(object o, Type t, string name) => (T)t.GetField(name, I)?.GetValue(o);

        // redirect：SteamPlatformServices.HasFixedResolution
        public static bool HasFixedResolution(SteamPlatformServices self) => On;

        // after：SettingsDialog.Initialize、FirstTimeSettingsScreen.Initialize（多載都掛，重複跑沒關係）
        public static void Initialize(SettingsDialog self) { if (On) HideResolutionRows(self, typeof(SettingsDialog), true); }
        public static void Initialize(FirstTimeSettingsScreen self) { if (On) HideResolutionRows(self, typeof(FirstTimeSettingsScreen), false); }

        static void HideResolutionRows(MonoBehaviour self, Type t, bool reindex)
        {
            foreach (var k in new[] { "resolutionDropdown", "fullScreenDropdown" })
            {
                var dd = F<Component>(self, t, k);
                if (dd == null) continue;
                var row = dd.GetComponentsInParent<SettingsEntry>(true).FirstOrDefault()?.gameObject ?? dd.gameObject;
                row.SetActive(false);
            }
            if (!reindex) return;
            var tabs = F<System.Collections.IEnumerable>(self, t, "tabs");
            if (tabs == null) return;
            foreach (SettingsTab tab in tabs)
            {
                if (tab == null || tab.SectionRoot == null) continue;
                var entries = tab.SectionRoot.GetComponentsInChildren<SettingsEntry>();
                for (int i = 0; i < entries.Length; i++) entries[i].SetIndex(i);
            }
        }

        // after：Hud.Start
        public static void Start(Hud self)
        {
            if (!On) return;
            int h = Screen.height;
            float d = h > 0 ? GearInset(Corner, h) / (h / 1080f) : 0;
            if (d <= 0) return;
            var gear = F<Component>(self, typeof(Hud), "settingsButton");
            if (gear != null && gear.transform is RectTransform g) g.anchoredPosition -= new Vector2(d, 0);
            var bar = F<GameObject>(self, typeof(Hud), "gameplayHudRoot");
            if (bar != null && bar.transform is RectTransform b) b.offsetMax -= new Vector2(d, 0);
        }

        /// <summary>
        /// 齒輪往內挪多少（螢幕像素）才整個落在圓角裡（同 main.ts 的 gearInset）。齒輪錨在右上角、中心離角 (40, 34)、畫出來的圓（含外圈陰影）半徑約 24，
        /// 單位是 HUD 畫布的單位（比 16:9 寬時照高度縮放，參考高 1080）。圓心 (d + cx, cy) 到圓角圓心 (r, r) 的距離加上 b 不超過 r
        /// </summary>
        static float GearInset(float r, int screenH)
        {
            float k = screenH / 1080f, cx = 40 * k, cy = 34 * k, b = 24 * k;
            if (r <= 0 || cy >= r) return 0;
            float room = (r - b) * (r - b) - (r - cy) * (r - cy);
            if (room <= 0) return r;
            return Math.Max(0, MathF.Ceiling(r - MathF.Sqrt(room) - cx));
        }

        // after：CoreInputDriverMouse.OnLateUpdate（每格跑一次）：認滑動，認出方向就送一次滾輪（下一格的 OnGUI 收到）
        static bool tracking;
        static Vector2 swipeStart;
        static readonly string[] SwipeYield = { "GameUIDraggableHelper", "CardUI", "HandUI", "ScrollRect", "Scrollbar", "Slider" };
        /// <summary>一次滑動送的滾輪量（格）：原版換段時把累計歸零，給大一點也只換一段（同 main.ts 的 SWIPE_STEP 100 ÷ 一格 3）</summary>
        const float SwipeNotches = 34;

        public static void OnLateUpdate(CoreInputDriverMouse self)
        {
            if (!On) return;
            Vector2 p = Input.mousePosition;
            if (Input.GetMouseButtonDown(0)) { tracking = !YieldsToDrag(p); swipeStart = p; return; }
            if (!Input.GetMouseButton(0)) { tracking = false; return; }
            if (!tracking) return;
            var d = p - swipeStart;
            // 起動距離：畫面高度的 3%，而且以垂直為主。手指往下＝內容跟著往下＝看上面＝滾輪往上
            if (Mathf.Abs(d.y) < Screen.height * 0.03f || Mathf.Abs(d.y) < Mathf.Abs(d.x) * 1.5f) return;
            UnityNative.InputState.PushScroll(0, d.y < 0 ? SwipeNotches : -SwipeNotches);
            tracking = false;
        }

        /// <summary>按下處接拖曳的物件（uGUI 的 pointerDrag：最上面那個命中往上找 IDragHandler），它或上層有 SwipeYield 的元件就讓給原本的拖曳</summary>
        static bool YieldsToDrag(Vector2 pos)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) return false;
            var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = pos };
            var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(ped, hits);
            if (hits.Count == 0) return false;
            var drag = UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IDragHandler>(hits[0].gameObject);
            for (var t = drag != null ? drag.transform : null; t != null; t = t.parent)
                foreach (var c in t.GetComponents<Component>())
                    if (c != null && Array.IndexOf(SwipeYield, c.GetType().Name) >= 0) return true;
            return false;
        }

        // after：GraphicsSettingsManager.Start（Init 是 Start 直接叫的）
        public static void Start(GraphicsSettingsManager self)
        {
            if (!On) return;
            var add = typeof(GraphicsSettingsManager).GetMethod("AddTargetFramerate", I);
            var special = typeof(GraphicsSettingsManager).GetField("targetFrameratesSpecial", S)?.GetValue(null) as int[];
            if (add == null) return;
            int hz = (int)Math.Round(Screen.currentResolution.refreshRateRatio.value);
            add.Invoke(self, new object[] { hz });
            foreach (var fps in special ?? Array.Empty<int>()) if (fps < hz) add.Invoke(self, new object[] { fps });
        }
    }
}
