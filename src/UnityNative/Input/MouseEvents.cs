// 舊版滑鼠事件（OnMouseDown／Up／UpAsButton／Enter／Over／Exit／Drag）：Unity 每格在 PreUpdate 的 SendMouseEvents 階段呼叫
// UnityEngine.SendMouseEvents.DoSendMouseEvents（UnityEngine.InputLegacyModule，managed）。它自己逐台攝影機打射線、比上一格、SendMessage；
// 原生這邊只負責呼叫它，加上 CameraRaycastHelper.RaycastTry／RaycastTry2D（Physics/Physics2D.cs）。
// skipRTCameras 傳 1（畫進 RenderTexture 的攝影機不收滑鼠事件；Unity 原生端傳什麼未查證）。
using System;
using System.Reflection;

namespace UnityNative
{
    public static class MouseEvents
    {
        static Action<int> send;

        public static void Register()
        {
            var t = Type.GetType("UnityEngine.SendMouseEvents, UnityEngine.InputLegacyModule");
            var m = t?.GetMethod("DoSendMouseEvents", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) return;
            send = m.CreateDelegate<Action<int>>();
            Engine.PreUpdate.Add(() => send(1));
        }
    }
}
