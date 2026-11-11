// 版面與動畫移植自 Sts2MobileLauncher e568614（https://github.com/ModinMobileSTS/Sts2MobileLauncher），MIT；授權全文見 THIRD_PARTY_NOTICES.md。
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace Mt2Mobile.Launcher.Ui;

/// <summary>
/// 共用的版面工具，對應 Sts2MobileLauncher 的 ExtraSettingsUi 與各頁重複的小函式（dp、text、icon、圓角底、ripple）。
/// 圖示是 Resources/drawable/ic_ms_&lt;名稱&gt;_24.xml（Material Symbols Rounded 轉的 vector，Apache-2.0），用名稱查。
/// </summary>
public static class UiKit
{
    public static int Dp(Context context, float value) =>
        (int)Math.Round(TypedValue.ApplyDimension(ComplexUnitType.Dip, value, context.Resources!.DisplayMetrics));

    public static bool IsWideLayout(Context context)
    {
        var c = context.Resources!.Configuration!;
        return c.ScreenWidthDp >= 720 && c.ScreenWidthDp > c.ScreenHeightDp;
    }

    public static LinearLayout Vertical(Context context) => new(context) { Orientation = Android.Widget.Orientation.Vertical };
    public static LinearLayout Horizontal(Context context) => new(context) { Orientation = Android.Widget.Orientation.Horizontal };

    public static TextView Text(Context context, string value, float sp, Color color, TypefaceStyle style = TypefaceStyle.Normal)
    {
        var view = new TextView(context) { Text = value };
        view.SetTextSize(ComplexUnitType.Sp, sp);
        view.SetTextColor(color);
        view.SetTypeface(Typeface.Default, style);
        view.SetIncludeFontPadding(false);
        return view;
    }

    public static TextView SingleLine(this TextView view)
    {
        view.SetSingleLine(true);
        view.Ellipsize = TextUtils.TruncateAt.End;
        return view;
    }

    public static Drawable? Symbol(Context context, string glyph, ColorStateList tint, int sizeDp)
    {
        int id = context.Resources!.GetIdentifier($"ic_ms_{glyph}_24", "drawable", context.PackageName);
        if (id == 0)
            return null;
        var drawable = context.GetDrawable(id)!.Mutate();
        drawable.SetTintList(tint);
        // vector 照 bounds 的大小畫；回報的 intrinsic size 改成要的大小，放大成浮水印時才不會糊（同 STS2 的 SizedDrawable）
        return new SizedDrawable(drawable, Dp(context, sizeDp));
    }

    private sealed class SizedDrawable(Drawable inner, int size) : DrawableWrapper(inner)
    {
        public override int IntrinsicWidth => size;
        public override int IntrinsicHeight => size;
    }

    public static Drawable? Symbol(Context context, string glyph, Color tint, int sizeDp) =>
        Symbol(context, glyph, ColorStateList.ValueOf(tint), sizeDp);

    public static ImageView Icon(Context context, string glyph, Color tint, int sizeDp)
    {
        var view = new ImageView(context);
        view.SetImageDrawable(Symbol(context, glyph, tint, sizeDp));
        view.SetScaleType(ImageView.ScaleType.FitCenter);
        int size = Dp(context, sizeDp);
        view.LayoutParameters = new LinearLayout.LayoutParams(size, size);
        return view;
    }

    public static GradientDrawable RoundedFill(Context context, Color color, float radiusDp)
    {
        var drawable = new GradientDrawable();
        drawable.SetShape(ShapeType.Rectangle);
        drawable.SetColor(color);
        drawable.SetCornerRadius(Dp(context, radiusDp));
        return drawable;
    }

    public static GradientDrawable RoundedStroke(Context context, Color fill, Color stroke, float strokeDp, float radiusDp)
    {
        var drawable = RoundedFill(context, fill, radiusDp);
        drawable.SetStroke(Dp(context, strokeDp), stroke);
        return drawable;
    }

    public static RippleDrawable Ripple(Context context, float radiusDp) =>
        new(ColorStateList.ValueOf(Palette.Ripple), null, RoundedFill(context, Color.White, radiusDp));

    public static LinearLayout.LayoutParams MatchWrap(Context context, int topMarginDp = 0) =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(context, topMarginDp) };

    public static LinearLayout.LayoutParams Weighted(Context context, float weight, int startMarginDp = 0)
    {
        var p = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, weight);
        p.MarginStart = Dp(context, startMarginDp);
        return p;
    }

    /// <summary>頁面的捲動區：內容限制最大寬度（手機 640dp、寬版 1180dp）並置中，左右留白，同 STS2 的 addResponsiveScrollContent。</summary>
    public static ScrollView ResponsiveScroll(Context context, View content)
    {
        var scroll = new ScrollView(context) { FillViewport = false };
        scroll.SetClipToPadding(false);
        scroll.SetBackgroundColor(Palette.Background);
        var container = new MaxWidthFrameLayout(context, Dp(context, IsWideLayout(context) ? 1180 : 640));
        int pad = Dp(context, IsWideLayout(context) ? 28 : 20);
        container.SetPadding(pad, 0, pad, 0);
        container.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.CenterHorizontal));
        scroll.AddView(container, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return scroll;
    }

    /// <summary>
    /// 頁首：大標題，右邊可以放一個元件（遊戲頁的 Steam chip、模組頁的開關）。上緣讓出狀態列。
    /// </summary>
    public static LinearLayout TopAppBar(Context context, string title, View? trailing = null)
    {
        var bar = Horizontal(context);
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(Dp(context, 20), Dp(context, 24), Dp(context, 20), Dp(context, 16));
        ApplySystemBarPadding(bar, top: true);
        var titleView = Text(context, title, 28, Palette.OnSurface, TypefaceStyle.Bold).SingleLine();
        titleView.LetterSpacing = 0.018f;
        bar.AddView(titleView, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        if (trailing != null)
            bar.AddView(trailing);
        return bar;
    }

    /// <summary>
    /// targetSdk 35 起一律 edge-to-edge：狀態列與導覽列畫在 app 上面，要自己讓出來。
    /// 在原本的 padding 上加系統列的 inset（只加指定的邊）。
    /// </summary>
    public static void ApplySystemBarPadding(View view, bool top = false, bool bottom = false, bool left = false, bool right = false)
    {
        int l = view.PaddingLeft, t = view.PaddingTop, r = view.PaddingRight, b = view.PaddingBottom;
        view.SetOnApplyWindowInsetsListener(new InsetsListener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            v.SetPadding(l + (left ? bars.Left : 0), t + (top ? bars.Top : 0), r + (right ? bars.Right : 0), b + (bottom ? bars.Bottom : 0));
            return insets;
        }));
        // 系統只在 inset 改變時往下發；之後才加進畫面的 view（切分頁換上的新頁）要自己要一次，不然頁首會被狀態列蓋住
        if (view.IsAttachedToWindow)
            view.RequestApplyInsets();
        else
            view.ViewAttachedToWindow += (_, _) => view.RequestApplyInsets();
    }

    private sealed class InsetsListener(Func<View, WindowInsets, WindowInsets> handler) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets) => handler(v, insets);
    }

    public static void Toast(Context context, string message) =>
        Android.Widget.Toast.MakeText(context, message, ToastLength.Short)!.Show();
}

/// <summary>寬度上限的 FrameLayout（大螢幕上內容不要拉得太寬）。</summary>
public sealed class MaxWidthFrameLayout(Context context, int maxWidth) : FrameLayout(context)
{
    private int _baseLeft = -1, _baseRight = -1;

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        // 第一次量的時候記下呼叫端設的左右留白，之後每次照可用寬度重算，螢幕轉向變窄時能縮回來
        if (_baseLeft < 0)
        {
            _baseLeft = PaddingLeft;
            _baseRight = PaddingRight;
        }
        int width = MeasureSpec.GetSize(widthMeasureSpec);
        int extra = Math.Max(0, (width - maxWidth - _baseLeft - _baseRight) / 2);
        SetPadding(_baseLeft + extra, PaddingTop, _baseRight + extra, PaddingBottom);
        base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
    }
}
