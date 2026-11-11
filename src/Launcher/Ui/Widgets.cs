// 版面與動畫移植自 Sts2MobileLauncher e568614（https://github.com/ModinMobileSTS/Sts2MobileLauncher），MIT；授權全文見 THIRD_PARTY_NOTICES.md。
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;

namespace Mt2Mobile.Launcher.Ui;

/// <summary>版本頁、設定頁、關於頁共用的元件，外觀照 Sts2MobileLauncher 的版本頁與設定頁。</summary>
public static class Widgets
{
    /// <summary>
    /// 分段切換（版本頁頂端、Steam 帳號頁的登入方式）：MaterialButtonToggleGroup 加外框按鈕，選中的那段填 surface container high。
    /// 照 Sts2MobileLauncher 的 GameVersionManagerPage.buildSegmentedTabs。
    /// </summary>
    public static View Segmented(Context c, string[] labels, int selected, Action<int> onSelect)
    {
        var group = new MaterialButtonToggleGroup(c) { SingleSelection = true, SelectionRequired = true };
        group.SetBackgroundColor(Color.Transparent);
        var ids = new int[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            var button = new MaterialButton(c, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = labels[i] };
            button.Id = ids[i] = View.GenerateViewId();
            button.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
            button.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            // 不用 SetSingleLine：單行會變成水平捲動，置中的捲動位置要掛上視窗才算，離屏快照會畫不出文字
            button.SetMaxLines(1);
            button.Checkable = true;
            button.SetMinHeight(UiKit.Dp(c, 44));
            button.SetPadding(UiKit.Dp(c, 8), 0, UiKit.Dp(c, 8), 0);
            button.SetTextColor(Palette.OnSurface);
            button.BackgroundTintList = new ColorStateList(
                new[] { new[] { Android.Resource.Attribute.StateChecked }, Array.Empty<int>() },
                new int[] { Palette.SurfaceContainerHigh, Color.Transparent });
            button.StrokeColor = ColorStateList.ValueOf(Palette.Outline);
            button.StrokeWidth = UiKit.Dp(c, 1);
            button.RippleColor = ColorStateList.ValueOf(Palette.Ripple);
            group.AddView(button, new LinearLayout.LayoutParams(0, UiKit.Dp(c, 46), 1f));
        }
        group.Check(ids[selected]);
        group.AddOnButtonCheckedListener(new CheckedListener((id, isChecked) =>
        {
            if (isChecked)
                onSelect(Array.IndexOf(ids, id));
        }));
        return group;
    }

    private sealed class CheckedListener(Action<int, bool> handler) : Java.Lang.Object, MaterialButtonToggleGroup.IOnButtonCheckedListener
    {
        public void OnButtonChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked) => handler(checkedId, isChecked);
    }

    /// <summary>膠囊按鈕：圖示加文字，surface container high 底（版本頁的「匯入｜Steam｜網盤」）。</summary>
    public static View PillButton(Context c, string glyph, string label, Action onClick)
    {
        var button = UiKit.Horizontal(c);
        button.SetGravity(GravityFlags.Center);
        button.SetPadding(UiKit.Dp(c, 8), 0, UiKit.Dp(c, 8), 0);
        button.Background = UiKit.RoundedFill(c, Palette.SurfaceContainerHigh, 28);
        button.Foreground = UiKit.Ripple(c, 28);
        button.Clickable = true;
        button.Focusable = true;
        button.Click += (_, _) => onClick();
        button.AddView(UiKit.Icon(c, glyph, Palette.OnSurface, 22));
        var text = UiKit.Text(c, label, 15, Palette.OnSurface, TypefaceStyle.Bold).SingleLine();
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = UiKit.Dp(c, 8) };
        button.AddView(text, lp);
        return button;
    }

    public static View PillButtonRow(Context c, params View[] buttons)
    {
        var row = UiKit.Horizontal(c);
        for (int i = 0; i < buttons.Length; i++)
        {
            var lp = new LinearLayout.LayoutParams(0, UiKit.Dp(c, 52), 1f);
            if (i > 0)
                lp.MarginStart = UiKit.Dp(c, 10);
            row.AddView(buttons[i], lp);
        }
        return row;
    }

    /// <summary>
    /// 清單列：左邊圓形底的圖示，標題、副標，右邊可放徽章，最右是箭頭（有 onClick 才畫）。
    /// enabled=false 時整列變淡、點了不動（設定頁「還沒做」的項目）。
    /// </summary>
    public static View ListItem(Context c, string glyph, string title, string? subtitle, Action? onClick, string? badge = null, bool enabled = true, bool circleIcon = true)
    {
        var row = UiKit.Horizontal(c);
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(UiKit.Dp(c, 4), UiKit.Dp(c, 12), UiKit.Dp(c, 4), UiKit.Dp(c, 12));
        if (onClick != null && enabled)
        {
            row.Clickable = true;
            row.Focusable = true;
            row.Foreground = UiKit.Ripple(c, 16);
            row.Click += (_, _) => onClick();
        }
        if (circleIcon)
        {
            var circle = new FrameLayout(c) { Background = UiKit.RoundedFill(c, Palette.SurfaceContainerHigh, 100) };
            circle.AddView(UiKit.Icon(c, glyph, Palette.Primary, 26), new FrameLayout.LayoutParams(UiKit.Dp(c, 26), UiKit.Dp(c, 26), GravityFlags.Center));
            row.AddView(circle, new LinearLayout.LayoutParams(UiKit.Dp(c, 52), UiKit.Dp(c, 52)));
        }
        else
        {
            row.AddView(UiKit.Icon(c, glyph, Palette.Primary, 24));
        }

        var texts = UiKit.Vertical(c);
        texts.AddView(UiKit.Text(c, title, 16, Palette.OnSurface, TypefaceStyle.Bold).SingleLine());
        if (!string.IsNullOrEmpty(subtitle))
        {
            var sub = UiKit.Text(c, subtitle, 13, Palette.OnSurfaceVariant);
            sub.SetMaxLines(3);
            texts.AddView(sub, UiKit.MatchWrap(c, 6));
        }
        row.AddView(texts, UiKit.Weighted(c, 1f, 16));

        if (badge != null)
        {
            var b = UiKit.Text(c, badge, 12, Palette.OnPrimary, TypefaceStyle.Bold);
            b.SetPadding(UiKit.Dp(c, 10), UiKit.Dp(c, 4), UiKit.Dp(c, 10), UiKit.Dp(c, 4));
            b.Background = UiKit.RoundedFill(c, Palette.Primary, 6);
            var bp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = UiKit.Dp(c, 8) };
            row.AddView(b, bp);
        }
        if (onClick != null && enabled)
        {
            var arrow = UiKit.Icon(c, "chevron_right", Palette.OnSurfaceVariant, 24);
            ((LinearLayout.LayoutParams)arrow.LayoutParameters!).MarginStart = UiKit.Dp(c, 8);
            row.AddView(arrow);
        }
        if (!enabled)
            row.Alpha = 0.5f;
        return row;
    }

    /// <summary>圓角卡片容器（設定頁的分組、關於頁的區塊）。</summary>
    public static LinearLayout Card(Context c)
    {
        var card = UiKit.Vertical(c);
        card.Background = UiKit.RoundedFill(c, Palette.SurfaceContainer, 20);
        card.SetPadding(UiKit.Dp(c, 12), UiKit.Dp(c, 4), UiKit.Dp(c, 12), UiKit.Dp(c, 4));
        return card;
    }

    public static TextView SectionLabel(Context c, string text)
    {
        var label = UiKit.Text(c, text, 14, Palette.Primary, TypefaceStyle.Bold);
        label.SetPadding(UiKit.Dp(c, 4), 0, 0, 0);
        return label;
    }

    /// <summary>空狀態：大圖示、標題、說明，置中。</summary>
    public static View EmptyState(Context c, string glyph, string title, string message)
    {
        var box = UiKit.Vertical(c);
        box.SetGravity(GravityFlags.CenterHorizontal);
        box.Background = UiKit.RoundedStroke(c, Palette.Surface, Palette.HairLine, 1, 24);
        box.SetPadding(UiKit.Dp(c, 24), UiKit.Dp(c, 32), UiKit.Dp(c, 24), UiKit.Dp(c, 32));
        var icon = UiKit.Icon(c, glyph, Palette.Primary, 48);
        icon.Alpha = 0.8f;
        box.AddView(icon);
        var t = UiKit.Text(c, title, 18, Palette.OnSurface, TypefaceStyle.Bold);
        t.Gravity = GravityFlags.Center;
        box.AddView(t, UiKit.MatchWrap(c, 16));
        var m = UiKit.Text(c, message, 14, Palette.OnSurfaceVariant);
        m.Gravity = GravityFlags.Center;
        m.SetLineSpacing(UiKit.Dp(c, 4), 1f);
        box.AddView(m, UiKit.MatchWrap(c, 10));
        return box;
    }
}
