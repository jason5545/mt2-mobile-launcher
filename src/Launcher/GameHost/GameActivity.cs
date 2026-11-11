// 遊戲（Phase 4 拆段 B）：從 unity-native/host-android 的 MainActivity 搬來，逐檔看過（Android API 手寫的宿主程式）。
// 跟原型不同的地方：
//   - 跑在自己的程序（:game）與自己的 task：引擎的狀態都是靜態的，每次開遊戲要新程序；Application.Quit 的 FinishAndRemoveTask + KillProcess
//     只收掉遊戲的 task 與程序，回到啟動器。
//   - 檔案從啟動器的安裝讀（docs/phase4.md「啟動遊戲」），不再從 APK 解 patch 過的 DLL、不讀 adb 推的外部 app 空間：
//       patch 過的遊戲 DLL 與 nonpublic-fields.txt：installs/<target>/patch/managed（手機上 patch 的）
//       轉接組件（Glue.*.dll）：runtime/<APK 的 lastUpdateTime>/runtime/glue（APK 帶的，開發機編好）
//       GameDir：installs/<target>/out；DataDir：originals/<target>/<名稱>_Data（原版，*.bundle 是真的檔，不用佔位檔）
//       存檔：files/saves/<名稱>；log：files/logs/game.log
//   - 拿掉 FMOD 的 Java 半邊（Diceomancer 用的，MT2 沒有）。
//   - 自動點擊（--es clicks）與另一份存檔（--es persistent）只在驗收用的建置（TEST_ENTRY）收。
#nullable disable
// 版本判斷用 Build.VERSION.SdkInt（同原型；原型的 csproj 用 NoWarn 關掉這三個）
#pragma warning disable CA1416, CA1422, CS0618
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Opengl;
using Android.OS;
using Android.Views;
using Mt2Mobile.Launcher.Install;
using Mt2Mobile.Launcher.SteamUi;
using Mt2Mobile.Steam;
using Activity = Android.App.Activity;
using EGLConfig = Javax.Microedition.Khronos.Egl.EGLConfig;
using Javax.Microedition.Khronos.Opengles;

namespace Mt2Mobile.Launcher.GameHost
{
    [Activity(Name = "org.mt2mobile.launcher.GameActivity", Process = ":game", Exported = false, TaskAffinity = "org.mt2mobile.launcher.game",
        Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen", LaunchMode = LaunchMode.SingleTask, ScreenOrientation = ScreenOrientation.SensorLandscape,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize |
                               ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.Density)]
    public class GameActivity : Activity
    {
        public const string Tag = "mt2-game";
        /// <summary>啟動器撤回過 Steam 存取、還沒補換代：磁碟上的身分與授權快取不算數（MainActivity.LaunchGameNow）</summary>
        public const string ExtraSteamRevoked = "steam-revoked";
        /// <summary>patch 過的遊戲 DLL 與轉接組件的資料夾，AssemblyLoadContext.Resolving 依序找</summary>
        internal static string[] AssemblyDirs;
        internal static string Managed;
        /// <summary>挑到的顯示模式的更新率（Hz）與螢幕圓角半徑（裝置像素），開機時交給 EngineConfig</summary>
        internal static volatile float RefreshRate = 60f;
        internal static volatile int CornerRadius;
        /// <summary>畫面上緣被挖孔擋住的高（裝置像素，DisplayCutout.SafeInsetTop），開機時交給 EngineConfig、之後跟著插入值更新</summary>
        internal static volatile int SafeInsetTop;
        internal static GameActivity Instance;
        internal static string LogPath;
        /// <summary>這份安裝保留原始壓縮資料的格式（install.json 的 textures.raw），開機時跟 GPU 支援的比</summary>
        internal static string[] InstallRawFormats = Array.Empty<string>();
        /// <summary>驗收的診斷開關（TEST_ENTRY）：tex-cpu（all、mips：壓縮貼圖強制用 CPU 解）、texhash（1：記每次 glCompressedTexImage2D 的雜湊）</summary>
        internal static string TexCpu, TexHash;
        GameView view;
        FileStream gameProcessLock;
        string installedTarget;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Window.AddFlags(WindowManagerFlags.KeepScreenOn | WindowManagerFlags.Fullscreen);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
            {
                var lp = Window.Attributes;
                lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                Window.Attributes = lp;
            }
            RequestHighestRefreshRate();
            Instance = this;

            var p = new Paths(FilesDir.AbsolutePath);
            Directory.CreateDirectory(Path.Combine(FilesDir.AbsolutePath, "cloud"));
            try
            {
                gameProcessLock = new FileStream(Path.Combine(FilesDir.AbsolutePath, "cloud", "game.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // 同一把鎖：遊戲已經在跑，或啟動器正在同步 Steam Cloud（同步全程持有，避免一邊同步一邊寫存檔）
                var busy = GetString(Resource.String.game_busy);
                Android.Widget.Toast.MakeText(this, busy, Android.Widget.ToastLength.Long)!.Show();
                SetContentView(new Android.Widget.TextView(this) { Text = busy });
                Finish();
                return;
            }
            Directory.CreateDirectory(p.Logs);
            LogPath = Path.Combine(p.Logs, "game.log");
            var problem = Locate(p, out var game, out var data);
            if (problem != null)
            {
                Android.Util.Log.Error(Tag, problem);
                var tv = new Android.Widget.TextView(this) { Text = problem };
                tv.SetTextColor(Android.Graphics.Color.White);
                SetContentView(tv);
                return;
            }
            // UnityEngine.*、遊戲的 DLL、轉接組件從資料夾載入：一定要在碰到任何 UnityEngine 型別之前掛上
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                foreach (var dir in AssemblyDirs)
                {
                    var f = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(f)) return LoadOptimizable(ctx, f);
                }
                return null;
            };

            ApplyStoredSteamSettings();

            string save = "persistent", clicks = null;
#if TEST_ENTRY
            // 自動點擊（驗收用）：am start --es clicks "格數:x,y;…"，x、y 是畫面比例（左上原點），例如 400:0.5,0.5 = 第 400 格點畫面中央
            // 另一份存檔（驗收用）：am start --es persistent 資料夾名，在 files/saves 另開一份，不碰平常那份
            var s = Intent?.GetStringExtra("persistent");
            if (!string.IsNullOrEmpty(s) && !s.Contains('/') && !s.Contains("..")) save = s;
            clicks = Intent?.GetStringExtra("clicks");
            TexCpu = Intent?.GetStringExtra("tex-cpu");
            TexHash = Intent?.GetStringExtra("texhash");
#endif
            view = new GameView(this, game, data, Path.Combine(FilesDir.AbsolutePath, "saves", save), clicks);
            SetContentView(view);
            HideBars(); // decor view 在 SetContentView 之後才有，InsetsController 要等它
            RegisterBackGesture();
        }

        /// <summary>
        /// Android 16 上 targetSdk 36 的 app 預設走預測返回：返回手勢不送 KEYCODE_BACK、也不叫 onBackPressed，DispatchKeyEvent 收不到，
        /// 系統直接 Finish 這個 Activity，task 留在最近使用裡（從那裡點回來會跳過啟動器的 patch 檢查與 Steam Cloud 同步）。
        /// 這裡接手返回手勢，跟 DispatchKeyEvent 的返回鍵一樣當 Escape（手勢放開才叫，按下、放開一起送）。
        /// 離開遊戲用遊戲內的結束（Application.Quit）或 Home；Activity 被關掉時程序在 OnDestroy 結束。
        /// Android 13～15 沒開 enableOnBackInvokedCallback，系統不用這個 callback，照舊送 KEYCODE_BACK（DispatchKeyEvent 當 Escape）
        /// </summary>
        void RegisterBackGesture()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return;
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(Android.Window.IOnBackInvokedDispatcher.PriorityDefault, new BackGesture(this));
        }

        sealed class BackGesture : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
        {
            readonly GameActivity owner;
            public BackGesture(GameActivity owner) => this.owner = owner;
            public void OnBackInvoked()
            {
                if (owner.view == null) { owner.FinishAndRemoveTask(); return; }
                owner.view.BackTap();
            }
        }

        protected override void OnDestroy()
        {
            gameProcessLock?.Dispose();
            gameProcessLock = null;
            GameView.CloseLog();
            base.OnDestroy();
            // 引擎的狀態都是靜態的，同一個程序不能再開機：Activity 一銷毀（返回手勢、系統回收、開不起來的 Finish）就結束程序，
            // 下次開遊戲一定是新程序。11/11 踩到：返回手勢只 Finish、程序還在，第二次開遊戲在舊程序裡重新開機，Camera 已銷毀、黑畫面
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
        }

        void ApplyStoredSteamSettings()
        {
            try
            {
                var glue = Assembly.Load(new AssemblyName("Glue.Facepunch"));
                var settings = glue.GetType("Glue.Facepunch.Settings", throwOnError: true)!;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                var steamIdField = settings.GetField("SteamId", flags)!;
                var nameField = settings.GetField("Name", flags)!;
                var ownedDlcField = settings.GetField("OwnedDlc", flags)!;
                var allDlcField = settings.GetField("AllDlc", flags)!;
                ulong defaultSteamId = (ulong)steamIdField.GetValue(null);
                var identity = OfficialSigning.Allowed(this) && Intent?.GetBooleanExtra(ExtraSteamRevoked, false) != true
                    && SteamAccountStore.HasCompleteGameAuthorization(this)
                    ? SteamAccountStore.LoadGameIdentity(this)
                    : null;
                var ownership = identity is null ? null : SteamAccountStore.LoadOwnership(this);

                allDlcField.SetValue(null, false);
                if (ownedDlcField.GetValue(null) is HashSet<uint> ownedDlc)
                    ownedDlc.Clear();

                if (identity is null || ownership is null || !ownership.HasOwnedDlcIds)
                {
                    GameView.SetPrivateIdentityForLogRedaction(defaultSteamId, null);
                    GameView.Log("Steam 身分快取未就緒，遊戲維持預設身分與關閉 DLC");
                    return;
                }

                steamIdField.SetValue(null, identity.SteamId);
                nameField.SetValue(null, identity.PersonaName);
                var targetDlc = ReadTargetDlc(installedTarget);
                var resolution = SteamDlcPolicy.Resolve(ownership.OwnedDlcIds, targetDlc);
                foreach (var appId in resolution.UnsupportedOwnedDlcIds)
                    GameView.Log($"DLC {appId} 這個版本還沒支援");
                var usableDlc = SteamDlcPolicy.ForGame(hasSteamAuthorization: true, ownership.OwnedDlcIds, targetDlc);

                if (ownedDlcField.GetValue(null) is HashSet<uint> target)
                    foreach (var appId in usableDlc)
                        target.Add(appId);
                else
                    ownedDlcField.SetValue(null, new HashSet<uint>(usableDlc));

                GameView.SetPrivateIdentityForLogRedaction(identity.SteamId, identity.PersonaName);
                var ids = string.Join(",", usableDlc.Order());
                GameView.Log($"Steam 身分已套用：defaultSteamId={identity.SteamId == defaultSteamId} ownedDlc=[{ids}] personaNameSet={!string.IsNullOrEmpty(identity.PersonaName)}");
            }
            catch (Exception e)
            {
                GameView.SetPrivateIdentityForLogRedaction(0, null);
                GameView.Log($"Steam 身分設定失敗：{e.GetType().Name}");
            }
        }

        IReadOnlyList<SteamDlcDepotTarget> ReadTargetDlc(string targetId) => TargetDlc.Read(this, targetId);

        /// <summary>
        /// 找安裝完成的那份：installs/*/install.json 是 complete 的（Intent 的 target 指定哪一份；沒指定就要剛好一份）。
        /// patch 過的 DLL 跟這個 APK 的引擎對不對得上，由啟動器在開遊戲之前比 patch 的指紋（拆段 C）；這裡只確認檔案在。
        /// 回傳 null 是找到了，不然是給使用者看的原因
        /// </summary>
        /// <summary>載入組件；組件層級的 [Debuggable] 帶 DisableOptimizations 的，記憶體裡清掉這個旗標再載（磁碟上的 patch 結果不動）。
        /// Unity 出貨的 UnityEngine.*Module.dll（這款是 70 個，含 CoreModule）都標了 Default｜DisableOptimizations｜EnableEditAndContinue：
        /// Unity 用的 Mono 不管這個屬性，CoreCLR 會照做，整個組件用 MinOpts 編、也不讓呼叫端內聯（Vector3、Matrix4x4 的運算子都變成一次呼叫）。
        /// 只留 IgnoreSymbolStoreSequencePoints，跟遊戲自己的組件（Assembly-CSharp）一樣。docs/perf-battle.md：戰鬥每格 cycles 少約 25%</summary>
        static Assembly LoadOptimizable(AssemblyLoadContext ctx, string file)
        {
            var bytes = File.ReadAllBytes(file);
            int at = DebuggableModesOffset(bytes);
            if (at < 0) return ctx.LoadFromAssemblyPath(file);
            int modes = BitConverter.ToInt32(bytes, at);
            const int Default = 0x1, EnableEditAndContinue = 0x4, DisableOptimizations = 0x100;
            if ((modes & DisableOptimizations) == 0) return ctx.LoadFromAssemblyPath(file);
            BitConverter.GetBytes(modes & ~(Default | EnableEditAndContinue | DisableOptimizations)).CopyTo(bytes, at);
            Interlocked.Increment(ref Optimized);
            return ctx.LoadFromStream(new MemoryStream(bytes));
        }

        /// <summary>載入時清掉 DisableOptimizations 的組件數（開機的 log 寫一行）</summary>
        internal static int Optimized;

        /// <summary>[assembly: Debuggable(DebuggingModes)] 的 DebuggingModes（int32）在檔案裡的位移；沒有這個屬性（或是 (bool, bool) 的舊建構子）回 -1</summary>
        static int DebuggableModesOffset(byte[] bytes)
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes, false));
            if (!pe.HasMetadata) return -1;
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            if (!md.IsAssembly) return -1;
            foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(h);
                if (ca.Constructor.Kind != System.Reflection.Metadata.HandleKind.MemberReference) continue;
                var mr = md.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)ca.Constructor);
                if (mr.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference) continue;
                if (md.GetString(md.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)mr.Parent).Name) != "DebuggableAttribute") continue;
                // blob：prolog 01 00、int32 的 DebuggingModes、具名參數個數 00 00，共 8 位元組
                var blob = md.GetBlobReader(ca.Value);
                if (blob.Length != 8) return -1;
                // blob 可能跟別的屬性共用（編譯器會合併相同內容）：有共用就不改
                int users = 0;
                foreach (var other in md.CustomAttributes) if (md.GetCustomAttribute(other).Value == ca.Value) users++;
                if (users != 1) return -1;
                int heap = System.Reflection.Metadata.Ecma335.MetadataReaderExtensions.GetHeapMetadataOffset(md, System.Reflection.Metadata.Ecma335.HeapIndex.Blob);
                int start = pe.PEHeaders.MetadataStartOffset + heap + System.Reflection.Metadata.Ecma335.MetadataTokens.GetHeapOffset(ca.Value) + 1;
                return bytes[start] == 1 && bytes[start + 1] == 0 ? start + 2 : -1;
            }
            return -1;
        }

        string Locate(Paths p, out string game, out string data)
        {
            game = data = null;
            var want = Intent?.GetStringExtra("target");
            var done = (Directory.Exists(p.Installs) ? Directory.GetDirectories(p.Installs) : Array.Empty<string>())
                .Select(d => new InstallDir(d))
                .Where(d => Util.ReadJson(d.Json)?["complete"]?.GetValue<bool>() == true)
                .Where(d => want == null || Path.GetFileName(d.Root) == want)
                .ToList();
            if (done.Count != 1) return GetString(done.Count == 0 ? Resource.String.game_error_not_installed : Resource.String.game_error_multiple_installs);
            var inst = done[0];
            var target = Path.GetFileName(inst.Root);
            var original = Util.ReadJson(Path.Combine(p.Original(target), "original.json"));
            if (original == null) return GetString(Resource.String.game_error_original_missing_format, target);
            installedTarget = target;
            InstallRawFormats = (Util.ReadJson(inst.Json)?["textures"]?["raw"] as System.Text.Json.Nodes.JsonArray)?.Select(n => n?.ToString()).Where(n => n != null).ToArray() ?? Array.Empty<string>();
            data = Path.Combine(p.Original(target), (string)original["dataDir"]);
            game = inst.Out;
            Managed = inst.Patch;
            var rt = Install.Runtime.Extract(this, p, s => Android.Util.Log.Info(Tag, s));
            AssemblyDirs = new[] { inst.Patch, rt.Glue };
            foreach (var need in new[] { Path.Combine(game, "scenes"), Path.Combine(data, "ScriptingAssemblies.json"), Path.Combine(Managed, "nonpublic-fields.txt") })
                if (!File.Exists(need) && !Directory.Exists(need)) return GetString(Resource.String.game_error_incomplete_format, need);
            return null;
        }

        /// <summary>同解析度裡更新率最高的顯示模式（120 Hz 的手機預設常跑 60；同 mt2-web 的 APK 殼）</summary>
        void RequestHighestRefreshRate()
        {
            var display = WindowManager?.DefaultDisplay;
            if (display == null) return;
            var cur = display.GetMode();
            var best = cur;
            foreach (var m in display.GetSupportedModes())
                if (m.PhysicalWidth == cur.PhysicalWidth && m.PhysicalHeight == cur.PhysicalHeight && m.RefreshRate > best.RefreshRate) best = m;
            var lp = Window.Attributes;
            lp.PreferredDisplayModeId = best.ModeId;
            lp.PreferredRefreshRate = best.RefreshRate;
            Window.Attributes = lp;
            RefreshRate = best.RefreshRate;
        }

        /// <summary>圓角半徑（四個角取最大）與挖孔的左右 safe inset 取大（同 mt2-web 的 APK 殼 updateSafeInsets）</summary>
        internal static void UpdateCorner(WindowInsets insets)
        {
            int side = 0;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                foreach (var pos in new[] { RoundedCornerPosition.TopLeft, RoundedCornerPosition.TopRight, RoundedCornerPosition.BottomRight, RoundedCornerPosition.BottomLeft })
                {
                    var rc = insets.GetRoundedCorner((int)pos);
                    if (rc != null) side = Math.Max(side, rc.Radius);
                }
            var cut = insets.DisplayCutout;
            if (cut != null) side = Math.Max(side, Math.Max(cut.SafeInsetLeft, cut.SafeInsetRight));
            CornerRadius = side;
            SafeInsetTop = cut?.SafeInsetTop ?? 0;
            if (UnityNative.Engine.Config != null)
            {
                UnityNative.Engine.Config.CornerRadius = side;
                UnityNative.Engine.Config.SafeInsetTop = SafeInsetTop;
            }
        }

        bool portraitAllowed;

        /// <summary>
        /// 遊戲的 glue 打開 EngineConfig.AllowPortrait 時方向改成跟著感應器轉，關掉時回 sensorLandscape（同原型）。MT2 沒有打開，一直是 sensorLandscape
        /// </summary>
        internal void SetPortraitAllowed(bool allow)
        {
            if (portraitAllowed == allow) return;
            portraitAllowed = allow;
            RunOnUiThread(() => RequestedOrientation = allow ? ScreenOrientation.Sensor : ScreenOrientation.SensorLandscape);
        }

        void HideBars()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                Window.SetDecorFitsSystemWindows(false);
                Window.InsetsController?.Hide(WindowInsets.Type.SystemBars());
                if (Window.InsetsController != null) Window.InsetsController.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
            else
            {
                Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation |
                                                                              SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen);
            }
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus) HideBars();
        }

        // 切到背景／回來：照 Unity 送 OnApplicationFocus、OnApplicationPause 給腳本。
        // 要在 GL 執行緒（遊戲的主執行緒）上跑：QueueEvent 排在 view.OnPause() 之前，GLSurfaceView 停下來前會先跑完排隊的事件
        protected override void OnPause()
        {
            base.OnPause();
            view?.QueueEvent(new Java.Lang.Runnable(() => Loop.ApplicationPause(true)));
            view?.OnPause();
            view?.Audio?.Pause();
        }

        protected override void OnResume()
        {
            base.OnResume();
            view?.OnResume();
            view?.QueueEvent(new Java.Lang.Runnable(() => Loop.ApplicationPause(false)));
            view?.Audio?.Resume();
        }

        /// <summary>Application.Quit：關掉遊戲的 task，程序也一起結束（引擎的狀態都是靜態的，不能在同一個程序裡再開機）</summary>
        internal void Quit() => RunOnUiThread(() => { FinishAndRemoveTask(); Android.OS.Process.KillProcess(Android.OS.Process.MyPid()); });

        // 返回鍵當 Escape（遊戲的選單、關對話框）
        public override bool DispatchKeyEvent(KeyEvent e)
        {
            if (view != null && e.KeyCode == Keycode.Back)
            {
                if (e.Action == KeyEventActions.Down && e.RepeatCount == 0) view.Key(true);
                else if (e.Action == KeyEventActions.Up) view.Key(false);
                return true;
            }
            return base.DispatchKeyEvent(e);
        }
    }

    /// <summary>畫面：GL ES 3 context、觸控（第一根手指當滑鼠左鍵）、遊戲迴圈</summary>
    public sealed class GameView : GLSurfaceView
    {
        readonly Loop loop;
        public AudioOut Audio => loop.Audio;

        internal static void SetPrivateIdentityForLogRedaction(ulong steamId, string personaName) =>
            Loop.SetPrivateIdentityForLogRedaction(steamId, personaName);

        internal static void CloseLog() => Loop.CloseLog();

        internal static void Log(string s) => Loop.Log(s);

        public GameView(Context ctx, string game, string data, string persistent, string clicks) : base(ctx)
        {
            SetEGLContextClientVersion(3);
            SetEGLConfigChooser(8, 8, 8, 0, 0, 0);
            PreserveEGLContextOnPause = true;
            loop = new Loop(game, data, persistent, clicks);
            SetRenderer(loop);
            RenderMode = Rendermode.Continuously;
        }

        public override bool OnTouchEvent(MotionEvent e)
        {
            int h = Height;
            // 遊戲有觸控翻譯層（TouchEvents.Enabled）：每根手指的事件原樣排進去，由它決定送什麼滑鼠動作
            if (Input.TouchLayer())
            {
                int idx = e.ActionIndex;
                switch (e.ActionMasked)
                {
                    case MotionEventActions.Down:
                    case MotionEventActions.PointerDown:
                        Input.Touch(0, e.GetPointerId(idx), e.GetX(idx), h - e.GetY(idx), e.ActionMasked == MotionEventActions.Down); break;
                    case MotionEventActions.Move:
                        for (int i = 0; i < e.PointerCount; i++) Input.Touch(1, e.GetPointerId(i), e.GetX(i), h - e.GetY(i), i == 0); break;
                    case MotionEventActions.Up:
                    case MotionEventActions.PointerUp:
                        Input.Touch(2, e.GetPointerId(idx), e.GetX(idx), h - e.GetY(idx), e.ActionMasked == MotionEventActions.Up); break;
                    case MotionEventActions.Cancel:
                        Input.Touch(3, e.GetPointerId(0), e.GetX(0), h - e.GetY(0), true); break;
                }
                return true;
            }
            float x = e.GetX(0), y = h - e.GetY(0);
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down: Input.Button(true, x, y); break;
                case MotionEventActions.Move: Input.Move(x, y); break;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel: Input.Button(false, x, y); break;
            }
            return true;
        }

        public void Key(bool down)
        {
            // 觸控翻譯層：返回鍵交給它（依狀態送取消、Shift、右鍵或 Esc）
            if (Input.TouchLayer()) { if (down) Input.Touch(4, 0, 0, 0, true); return; }
            Input.Escape(down);
        }

        /// <summary>
        /// 返回手勢（只有放開這一下）：觸控翻譯層照 Key(true)；不然按下 Escape，隔兩格再放開。
        /// InputSystemNative 每格送的是「按住」狀態，按下、放開在同一格排進去，那一格看到的是沒按，遊戲收不到 Escape
        /// </summary>
        public void BackTap()
        {
            if (Input.TouchLayer()) { Key(true); return; }
            loop.TapEscape();
        }

        public override WindowInsets OnApplyWindowInsets(WindowInsets insets)
        {
            GameActivity.UpdateCorner(insets);
            return base.OnApplyWindowInsets(insets);
        }
    }

    /// <summary>碰 UnityEngine 型別的地方集中在這裡（Resolving 掛好之後才會 JIT 到）</summary>
    static class Input
    {
        public static void Button(bool down, float x, float y) => UnityNative.InputState.PushButton(0, down, x, y);
        public static void Move(float x, float y) => UnityNative.InputState.PushMouse(x, y);
        public static void Escape(bool down) => UnityNative.InputState.PushKey(UnityEngine.KeyCode.Escape, down);
        public static bool TouchLayer() => UnityNative.TouchEvents.Enabled;
        public static void Touch(int kind, int id, float x, float y, bool primary) => UnityNative.TouchEvents.Push((UnityNative.TouchEvents.Kind)kind, id, x, y, primary);
    }

    sealed class Loop : Java.Lang.Object, GLSurfaceView.IRenderer
    {
        readonly string game, data, persistent;
        static ulong steamIdForLogRedaction;
        static string personaNameForLogRedaction;
        static readonly Regex SteamIdPattern = new Regex(@"\b7656119\d{10}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex PlayerIdPattern = new Regex(@"PlayerId:\s*[^,]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        readonly List<(int frame, float fx, float fy)> clicks = new List<(int, float, float)>();
        int frameNo;
        bool booted, failed, quitting;
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool QuitRequested() => UnityNative.Engine.Quitting;
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool PortraitAllowed() => UnityNative.Engine.Config?.AllowPortrait ?? false;
        int width, height;
        public AudioOut Audio;
        readonly Stopwatch clock = Stopwatch.StartNew();
        double last;
        readonly List<double> times = new List<double>();
        UnityNative.Gl.GlGpu gpu;
        readonly string ctlFile = Path.Combine(Android.App.Application.Context.FilesDir.AbsolutePath, "ctl");
        const int ProfileEvery = 10;
        string profileMode = "auto";
        int windowNo, glFrom;

        void RunCtl()
        {
            string[] lines;
            try { lines = File.ReadAllLines(ctlFile); File.Delete(ctlFile); } catch { return; }
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t == "dump") gpu.DumpNext = true;
                else if (t.StartsWith("find ")) UnityNative.DebugTools.Find(t[5..].Trim(), Log);
                // 量測（docs/perf-battle.md）：tex 之後 300 格的貼圖上傳、gc 最近幾次 GC、profile on|off|auto 分段計時（auto：每 10 窗量 1 窗）、
                // glcache on|off GL state 快取、gl 上次之後 state 呼叫送出與省掉的次數
                else if (t == "tex") { UnityNative.Gl.GpuResources.StartTrace(300); Log("[ctl] 貼圖上傳追蹤 300 格"); }
                else if (t == "gc") Log(UnityNative.PerfDiag.GcInfo());
                else if (t == "glcache on" || t == "glcache off") { UnityNative.Gl.GL.StateCache = t.EndsWith("on"); Log("[ctl] GL state 快取 " + t[8..]); }
                else if (t == "gl")
                {
                    Log($"  [gl] state 呼叫：送進驅動 {UnityNative.Gl.GL.StateIssued}、省掉 {UnityNative.Gl.GL.StateSkipped}（{UnityNative.Engine.Time.FrameCount - glFrom} 格）");
                    UnityNative.Gl.GL.StateIssued = UnityNative.Gl.GL.StateSkipped = 0; glFrom = UnityNative.Engine.Time.FrameCount;
                }
                else if (t == "profile on" || t == "profile off" || t == "profile auto")
                {
                    profileMode = t[8..];
                    UnityNative.Engine.Profile = profileMode == "on" || (profileMode == "auto" && windowNo % ProfileEvery == 0);
                    Log("[ctl] 分段計時 " + profileMode + "（這一窗的分段只算到切換之後）");
                }
                else if (t.Length > 0) Log("[ctl] 不認得：" + t);
            }
        }

        public Loop(string game, string data, string persistent, string clickSpec)
        {
            this.game = game; this.data = data; this.persistent = persistent;
            foreach (var c in (clickSpec ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int k = c.IndexOf(':');
                var xy = c[(k + 1)..].Split(',');
                clicks.Add((int.Parse(c[..k]), float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        // 新的 EGL context：GL 的 state 回到預設值，引擎的 state 快取跟著重設
        public void OnSurfaceCreated(IGL10 gl, EGLConfig config) => ResetGlState();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ResetGlState() => UnityNative.Gl.GL.ResetStateCache();

        public void OnSurfaceChanged(IGL10 gl, int w, int h)
        {
            width = w; height = h;
            if (booted)
            {
                UnityNative.Engine.Config.WindowWidth = w;
                UnityNative.Engine.Config.WindowHeight = h;
                return;
            }
            if (failed) return;
            // 第一個 surface 常是直的（轉向還沒套上）：等到橫的再開機，畫面大小才對
            if (h > w) { Log($"畫面 {w}x{h} 是直的，等轉成橫的"); return; }
            try { Boot(); booted = booted0 = true; }
            catch (Exception e) { failed = true; Log("開機失敗：" + e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Boot()
        {
            var sw = Stopwatch.StartNew();
            Log($"畫面 {width}x{height}、{GameActivity.RefreshRate:F0} Hz、圓角 {GameActivity.CornerRadius} px，遊戲資料 {game}");
            LoadGl();
            UnityNative.SerialFields.LoadEngineNonPublic(Path.Combine(GameActivity.Managed, "nonpublic-fields.txt"));
            var list = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "ScriptingAssemblies.json"))).RootElement.GetProperty("names");
            foreach (var n in list.EnumerateArray())
            {
                var name = Path.GetFileNameWithoutExtension(n.GetString());
                if (name == "mscorlib" || name == "netstandard" || name.StartsWith("System") || name.StartsWith("Mono.") || name == "Microsoft.Bcl.AsyncInterfaces") continue;
                try { Assembly.Load(new AssemblyName(name)); } catch (Exception e) { Log("[警告] 載不了 " + name + "：" + e.Message); }
            }
            Log($"載入 DLL {sw.ElapsedMilliseconds} ms");
            Log($"  載入時清掉 DisableOptimizations 的組件 {GameActivity.Optimized} 個");
            UnityNative.Icalls.UnityEngine_Object.MainThread = System.Environment.CurrentManagedThreadId;

            var info = File.ReadAllLines(Path.Combine(data, "app.info"));
            var cfg = new UnityNative.EngineConfig
            {
                GameDir = game,
                DataDir = data,
                PersistentDir = persistent,
                CompanyName = info.Length > 0 ? info[0] : "",
                ProductName = info.Length > 1 ? info[1] : "",
                ScreenWidth = width,
                ScreenHeight = height,
                WindowWidth = width,
                WindowHeight = height,
                // 手機版面（網頁版的 APK 預設開：寬版、介面縮放；遊戲的 glue 讀這個）
                MobileLayout = true,
                Dpi = Android.App.Application.Context.Resources.DisplayMetrics.Xdpi,
                RefreshRate = GameActivity.RefreshRate,
                CornerRadius = GameActivity.CornerRadius,
                SafeInsetTop = GameActivity.SafeInsetTop,
                LogSink = Log,
            };
            gpu = new UnityNative.Gl.GlGpu();
            UnityNative.FrameRenderer.Gpu = gpu;
            CompressedTextures();
            Audio = AudioOut.TryOpen();
            if (Audio != null) UnityNative.AudioEngine.Sink = Audio.Push;
            AndroidVideoDecoder.Install();
            PngAndroid.Install();
            UnityNative.Engine.Boot(cfg);
            Log($"開機 {sw.ElapsedMilliseconds} ms");
            UnityNative.Engine.Profile = true; // 第一窗（開機後）量
            last = clock.Elapsed.TotalSeconds;
        }

        /// <summary>
        /// 保留原始壓縮資料的貼圖（docs/perf-battle.md「B 段」）：GlGpu 開機時查了 GPU 的擴充，這裡給 CPU 解碼器（AssetRipper 的，安裝時用的同一套）、
        /// 記一行支援哪些格式；安裝時存成 GPU 格式、現在 GPU 卻不支援的（換了驅動之類），寫一行提示重新轉換（遊戲照樣用 CPU 解）
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void CompressedTextures()
        {
            UnityNative.Gl.GpuResources.CpuDecoder = (f, w, h, nm, d) => Mt2Mobile.TextureExport.RawTextureDecoder.DecodeLevel0(f, w, h, nm, d);
            UnityNative.Gl.GpuResources.ForceCpu = GameActivity.TexCpu;
            bool s3tc = UnityNative.Gl.GpuResources.S3tc, bptc = UnityNative.Gl.GpuResources.Bptc;
            Log($"  壓縮貼圖：GPU 支援 S3TC（DXT1、DXT5）{(s3tc ? "是" : "否")}、BPTC（BC6H、BC7）{(bptc ? "是" : "否")}；這份安裝存成原始資料的格式 {(GameActivity.InstallRawFormats.Length > 0 ? string.Join("、", GameActivity.InstallRawFormats) : "（沒有，都是 PNG）")}"
                + (GameActivity.TexCpu != null ? $"；診斷開關 tex-cpu={GameActivity.TexCpu}" : ""));
            var lost = GameActivity.InstallRawFormats.Where(f => f is "DXT1" or "DXT5" ? !s3tc : f is "BC6H" or "BC7" ? !bptc : false).ToArray();
            if (lost.Length > 0) Log($"[警告] 這份安裝把 {string.Join("、", lost)} 存成 GPU 的壓縮格式，這台 GPU 現在不支援：改用 CPU 解（載入比較慢），請回啟動器重新轉換");
            if (GameActivity.TexHash == "1") StartTexHash();
        }

        static StreamWriter texHash;

        /// <summary>診斷：每次 glCompressedTexImage2D 送出的資料記「來源 層 長度 sha256」（tab 分隔，files/logs/texhash.txt），tools/texture-check texhash 跟原版比</summary>
        static void StartTexHash()
        {
            texHash = new StreamWriter(Path.Combine(Path.GetDirectoryName(GameActivity.LogPath), "texhash.txt"), false) { AutoFlush = true };
            UnityNative.Gl.GpuResources.UploadTap = (src, level, d) =>
                texHash.WriteLine($"{src}\t{level}\t{d.Length}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(d)).ToLowerInvariant()}");
            Log("  [texhash] 記每次壓縮貼圖上傳的雜湊到 texhash.txt");
        }

        // GL ES 的函式：核心的從 libGLESv3.so 直接拿（Android 的 eglGetProcAddress 不保證回核心函式），拿不到才問 EGL
        static unsafe void LoadGl()
        {
            var gles = NativeLibrary.Load("libGLESv3.so");
            var egl = NativeLibrary.Load("libEGL.so");
            var eglGet = (delegate* unmanaged<byte*, IntPtr>)NativeLibrary.GetExport(egl, "eglGetProcAddress");
            UnityNative.Gl.GL.Load(n =>
            {
                if (NativeLibrary.TryGetExport(gles, n, out var p)) return p;
                var b = System.Text.Encoding.ASCII.GetBytes(n + "\0");
                fixed (byte* s = b) return eglGet(s);
            }, desktop: false);
            Log($"GL：{UnityNative.Gl.GL.Str(UnityNative.Gl.GL.VERSION)}，{UnityNative.Gl.GL.Str(UnityNative.Gl.GL.RENDERER)}");
        }

        readonly UnityNative.FramePacer pacer = new UnityNative.FramePacer();

        public void OnDrawFrame(IGL10 gl)
        {
            if (!booted) return;
            // 照 targetFrameRate／vSyncCount 限速：這次不跑的話重貼上一格（GLSurfaceView 每次都會換緩衝）
            if (frameNo > 0 && !pacer.ShouldRun(clock.Elapsed.TotalSeconds)) { gpu.Present(); return; }
            foreach (var c in clicks)
            {
                float x = c.fx * width, y = (1 - c.fy) * height;
                if (c.frame == frameNo) { Log($"自動點擊 第 {frameNo} 格 ({x:F0},{y:F0})"); Input.Button(true, x, y); }
                if (c.frame + 2 == frameNo) Input.Button(false, x, y);
            }
            frameNo++;
            // 除錯：adb 推 files/ctl（一行一個指令，同桌面版 --ctl 的 dump、find 名稱），結果寫進 log，檔案讀完就刪
            if (frameNo % 30 == 0 && File.Exists(ctlFile)) RunCtl();
            double now = clock.Elapsed.TotalSeconds;
            UnityNative.PerfDiag.FrameBegin();
            var t0 = Stopwatch.GetTimestamp();
            try { Frame(now - last); }
            catch (Exception e) { Log("[例外] 每格：" + e); }
            last = now;
            if (Volatile.Read(ref escapeUpIn) > 0 && Interlocked.Decrement(ref escapeUpIn) == 0) Input.Escape(false);
            if (QuitRequested() && !quitting) { quitting = true; Log("Application.Quit：關掉"); GameActivity.Instance?.Quit(); }
            GameActivity.Instance?.SetPortraitAllowed(PortraitAllowed());
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            times.Add(ms);
            // 慢格與 ctl 開的追蹤：縮排的行是量測細節，logcmp.py 不比
            if (UnityNative.PerfDiag.FrameEnd(UnityNative.Engine.Time.FrameCount, ms) is string slow) Log(slow);
            if (UnityNative.Gl.GpuResources.PollTrace() is string tex) Log(tex);
            if (times.Count == 300)
            {
                double sum = 0, max = 0;
                foreach (var t in times) { sum += t; if (t > max) max = t; }
                Log($"每格 {sum / times.Count:F2} ms、最大 {max:F1} ms（{UnityNative.Engine.Time.FrameCount} 格，例外 {UnityNative.Engine.ExceptionCount}，draw {gpu.LastDrawCalls}，" +
                    $"代用的 shader {gpu.StandIns.Count}）；累計 shader 編譯 {UnityNative.Gl.GL.Compiles}＋link {UnityNative.Gl.GL.Links} 次 {UnityNative.Gl.GL.CompileMs:F0} ms、" +
                    $"貼圖上傳 {UnityNative.Gl.GpuResources.TexturesUploaded} 張 {UnityNative.Gl.GpuResources.UploadMs} ms、{UnityNative.PerfDiag.WindowGc()}");
                if (UnityNative.Engine.Profile)
                {
                    Log("  分段（每格平均 ms）：" + string.Join("、", UnityNative.Engine.PhaseMs.OrderByDescending(kv => kv.Value).Take(14)
                        .Select(kv => kv.Key.Trim() + " " + (kv.Value / times.Count).ToString("F2"))));
                    Log("  配置（每格平均 KB）：" + string.Join("、", UnityNative.Engine.PhaseAlloc.OrderByDescending(kv => kv.Value).Take(14)
                        .Select(kv => kv.Key.Trim() + " " + (kv.Value / times.Count / 1024).ToString("F1"))));
                }
                if (UnityNative.PerfDiag.WindowExtra() is string extra) Log(extra);
                UnityNative.Engine.PhaseMs.Clear();
                UnityNative.Engine.PhaseAlloc.Clear();
                UnityNative.PerfDiag.WindowEnd();
                times.Clear();
                // 分段計時本身要花時間（戰鬥一格約 2.7% 的 cycles，docs/perf-battle.md A5）：預設每 10 窗量 1 窗，ctl 可改成一直開或關
                windowNo++;
                if (profileMode == "auto") UnityNative.Engine.Profile = windowNo % ProfileEvery == 0;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Frame(double dt) => UnityNative.Engine.Frame(dt);

        /// <summary>還要跑完幾格才放開返回手勢按下的 Escape（UI 執行緒寫、GL 執行緒讀）</summary>
        int escapeUpIn;

        /// <summary>
        /// 按下 Escape，跑完兩格再放開：按下可能排在某一格的 BeginFrame 之後，那格結束減成 1，
        /// 下一格的 BeginFrame 讀到按下（按住），結束時才排放開。還沒開機就不送，免得開機後卡著按住
        /// </summary>
        internal void TapEscape()
        {
            if (!booted) return;
            Input.Escape(true);
            Volatile.Write(ref escapeUpIn, 2);
        }

        static bool booted0;

        /// <summary>切到背景：OnApplicationFocus(false)、OnApplicationPause(true)；回來：OnApplicationPause(false)、OnApplicationFocus(true)</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ApplicationPause(bool paused)
        {
            if (!booted0) return;
            if (paused) { UnityNative.Lifecycle.SendApplicationFocus(false); UnityNative.Lifecycle.SendApplicationPause(true); }
            else { UnityNative.Lifecycle.SendApplicationPause(false); UnityNative.Lifecycle.SendApplicationFocus(true); }
            Log(paused ? "切到背景：OnApplicationPause(true)" : "回到前景：OnApplicationPause(false)");
        }

        // logcat 會被系統訊息洗掉，另外整份寫到 files/logs/game.log（每次開機重寫）
        static StreamWriter file;
        static readonly object fileGate = new object();

        internal static void SetPrivateIdentityForLogRedaction(ulong steamId, string personaName)
        {
            steamIdForLogRedaction = steamId;
            personaNameForLogRedaction = personaName;
        }

        internal static void CloseLog()
        {
            lock (fileGate)
            {
                file?.Dispose();
                file = null;
            }
        }

        internal static void Log(string s)
        {
            s = RedactPrivateIdentity(s);
            Android.Util.Log.Info(GameActivity.Tag, s);
            lock (fileGate)
            {
                if (file == null)
                {
                    try { file = new StreamWriter(GameActivity.LogPath, false) { AutoFlush = true }; }
                    catch { return; }
                }
                file.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + s);
            }
        }

        static string RedactPrivateIdentity(string value)
        {
            value = SteamIdPattern.Replace(value, "[SteamID]");
            value = PlayerIdPattern.Replace(value, "PlayerId: [redacted]");
            int playerName = value.IndexOf("PlayerName ", StringComparison.Ordinal);
            if (playerName >= 0)
                value = value[..playerName] + "PlayerName [redacted]";
            if (!string.IsNullOrEmpty(personaNameForLogRedaction))
                value = value.Replace(personaNameForLogRedaction, "[player]", StringComparison.Ordinal);
            if (steamIdForLogRedaction != 0)
                value = value.Replace(steamIdForLogRedaction.ToString(System.Globalization.CultureInfo.InvariantCulture), "[SteamID]", StringComparison.Ordinal);
            return value;
        }
    }
}
