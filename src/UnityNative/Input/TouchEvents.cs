// 原始觸控事件的佇列：遊戲的 glue 打開（Enabled）時，宿主不再把第一根手指直接當滑鼠，改把觸控事件（與 Android 的返回鍵）排進來，
// 由 glue 的觸控翻譯層在遊戲執行緒上取出、合成原版的滑鼠與按鍵動作（Diceomancer：native/glue/Glue.Dice/Touch.cs，網頁版 touchInput.ts 的移植）。
// 座標同滑鼠：視窗像素、左下原點。
using System.Collections.Generic;

namespace UnityNative
{
    public static class TouchEvents
    {
        public enum Kind { Down, Move, Up, Cancel, Back }
        public struct Event { public Kind Kind; public int Id; public float X, Y; public bool Primary; }

        public static volatile bool Enabled;
        static readonly List<Event> pending = new List<Event>();

        public static void Push(Kind kind, int id, float x, float y, bool primary = true)
        {
            lock (pending) pending.Add(new Event { Kind = kind, Id = id, X = x, Y = y, Primary = primary });
        }

        public static List<Event> Drain()
        {
            lock (pending)
            {
                var r = new List<Event>(pending);
                pending.Clear();
                return r;
            }
        }
    }
}
