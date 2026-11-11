// SpriteMask（UnityEngine.SpriteMaskModule，原生元件）：用 sprite 的形狀遮住 maskInteraction 不是 None 的 SpriteRenderer、粒子。
// 原版的材質是 Sprites-Mask（Sprites/Mask：ColorMask 0、clip(a − _Cutoff)），stencil 狀態由引擎決定，原始碼拿不到。
// 這裡的規則照網頁版（diceomancer-web src/runtime/render/spriteMaskRange.ts，10/10 的裁決）：
//   - 遮罩只用 stencil 的 0x80（bit 7），uGUI 的 Mask 用低位元（每層 +1），兩者不撞。
//   - 寫入：排在「同 sorting layer、order ≤ back」的所有 renderer 之後；Comp Always、Pass Replace、WriteMask 0x80，alpha < cutoff 的像素不寫。
//   - 清除：排在「order ≤ front」的所有 renderer 之後；Pass Zero。所以 order 落在 (back, front] 的 renderer 看得到遮罩。
//   - 被遮的：VisibleInsideMask 用 Equal、VisibleOutsideMask 用 NotEqual（ref 0x80、ReadMask 0x80、不寫 stencil）。
//   - 自訂範圍關閉：遮罩影響全部（Unity 手冊：預設影響場景裡所有 Mask Interaction 不是 None 的 sprite）。寫入排在最前、清除排在最後。
// 刻意不做：多個遮罩範圍重疊時共用同一個位元，先清除的會把重疊區清掉（同網頁版，寫在差異清單）。
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public sealed class NSpriteMask : NRenderer
    {
        public Sprite Sprite;
        public float AlphaCutoff = 0.2f;
        public int FrontLayerID, BackLayerID, FrontOrder = 1, BackOrder = -1;
        public bool CustomRange;
        public SpriteSortPoint SortPoint;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            Sprite = Serializer.Ref(d["m_Sprite"], typeof(Sprite), ctx) as Sprite;
            if (d["m_MaskAlphaCutoff"] != null) AlphaCutoff = (float)Serializer.D(d["m_MaskAlphaCutoff"]);
            FrontLayerID = (int)Serializer.D(d["m_FrontSortingLayerID"]);
            BackLayerID = (int)Serializer.D(d["m_BackSortingLayerID"]);
            if (d["m_FrontSortingOrder"] != null) FrontOrder = (int)Serializer.D(d["m_FrontSortingOrder"]);
            if (d["m_BackSortingOrder"] != null) BackOrder = (int)Serializer.D(d["m_BackSortingOrder"]);
            CustomRange = Serializer.D(d["m_IsCustomRangeActive"]) != 0;
            SortPoint = (SpriteSortPoint)(int)Serializer.D(d["m_SpriteSortPoint"]);
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NSpriteMask)src;
            Sprite = s.Sprite; AlphaCutoff = s.AlphaCutoff; FrontLayerID = s.FrontLayerID; BackLayerID = s.BackLayerID;
            FrontOrder = s.FrontOrder; BackOrder = s.BackOrder; CustomRange = s.CustomRange; SortPoint = s.SortPoint;
        }

        public override Bounds LocalBounds
        {
            get
            {
                if (Sprite == null) return default;
                Icalls.UnityEngine_Sprite.get_bounds_Injected(Sprite, out var b);
                return b;
            }
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_SpriteMask
    {
        static NSpriteMask M(SpriteMask m) => Native.Self<NSpriteMask>(m);
        public static int get_frontSortingLayerID(SpriteMask self) => M(self).FrontLayerID;
        public static void set_frontSortingLayerID(SpriteMask self, int value) => M(self).FrontLayerID = value;
        public static int get_frontSortingOrder(SpriteMask self) => M(self).FrontOrder;
        public static void set_frontSortingOrder(SpriteMask self, int value) => M(self).FrontOrder = value;
        public static int get_backSortingLayerID(SpriteMask self) => M(self).BackLayerID;
        public static void set_backSortingLayerID(SpriteMask self, int value) => M(self).BackLayerID = value;
        public static int get_backSortingOrder(SpriteMask self) => M(self).BackOrder;
        public static void set_backSortingOrder(SpriteMask self, int value) => M(self).BackOrder = value;
        public static float get_alphaCutoff(SpriteMask self) => M(self).AlphaCutoff;
        public static void set_alphaCutoff(SpriteMask self, float value) => M(self).AlphaCutoff = value;
        public static Sprite get_sprite(SpriteMask self) => M(self).Sprite;
        public static void set_sprite(SpriteMask self, Sprite value) => M(self).Sprite = value;
        public static bool get_isCustomRangeActive(SpriteMask self) => M(self).CustomRange;
        public static void set_isCustomRangeActive(SpriteMask self, bool value) => M(self).CustomRange = value;
        public static SpriteSortPoint get_spriteSortPoint(SpriteMask self) => M(self).SortPoint;
        public static void set_spriteSortPoint(SpriteMask self, SpriteSortPoint value) => M(self).SortPoint = value;
        public static void GetSpriteBounds_Injected(SpriteMask self, out Bounds ret) => ret = M(self).LocalBounds;
    }
}
