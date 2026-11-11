// 版面與動畫移植自 Sts2MobileLauncher e568614（https://github.com/ModinMobileSTS/Sts2MobileLauncher），MIT；授權全文見 THIRD_PARTY_NOTICES.md。
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;

namespace Mt2Mobile.Launcher.Ui;

// 遊戲頁的三種卡片與按壓效果。動畫的參數（週期、位移、模糊、縮放）照 Sts2MobileLauncher 的 GamePage，只換配色。

/// <summary>
/// 遊戲就緒時的主卡片：漸層緩慢左右流動（8 秒一輪），兩顆模糊光球各自漂移。
/// 以前每個 vsync 在軟體圖層重畫、每次重算 60 dp 的 BlurMaskFilter，啟動器開著時主執行緒吃滿一顆核心（docs/perf-battle.md「其他發現」）。
/// 現在光球的模糊只在大小變了時畫一次（四分之一解析度的點陣圖，畫的時候放大，模糊的東西放大看不出差別），漸層用同一個 shader 只移位置，
/// 走硬體加速；動得很慢，每秒重畫 30 次就夠；看不到（切到別頁、背景）就停。
/// </summary>
public sealed class AnimatedHeroCard : FrameLayout
{
    /// <summary>光球點陣圖的縮小倍數</summary>
    private const int OrbScale = 4;
    /// <summary>重畫間隔（毫秒）</summary>
    private const long FrameMs = 33;
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private readonly Paint _bitmapPaint = new(PaintFlags.FilterBitmap);
    private readonly Matrix _gradientMatrix = new();
    private readonly RectF _rect = new();
    private readonly Android.Graphics.Path _clip = new();
    private readonly int _radius;
    private LinearGradient? _gradient;
    private readonly Orb _top, _bottom;
    private long _startMs;
    private bool _running;
    private readonly Java.Lang.Runnable _tick;

    /// <summary>一顆光球：半徑、模糊半徑、顏色固定，預先畫好模糊後的圓（含外圍漸淡的範圍）</summary>
    private sealed class Orb(float radiusDp, float blurDp, Color color, long periodMs)
    {
        public readonly float RadiusDp = radiusDp, BlurDp = blurDp;
        public readonly Color Color = color;
        public readonly long PeriodMs = periodMs;
        public Bitmap? Bitmap;
        /// <summary>點陣圖涵蓋的半邊長，以半徑 1 倍時為準（裝置像素）</summary>
        public float Half;
        public readonly RectF Dst = new();

        public void Prepare(Context c)
        {
            if (Bitmap != null) return;
            float r = UiKit.Dp(c, RadiusDp), blur = UiKit.Dp(c, BlurDp);
            // BlurMaskFilter 的 sigma 約是半徑的 0.58 倍，3 個 sigma 之外看不到
            Half = r + 1.8f * blur;
            int size = Math.Max(8, (int)Math.Ceiling(2 * Half / OrbScale));
            Bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
            using var canvas = new Canvas(Bitmap);
            using var paint = new Paint(PaintFlags.AntiAlias) { Color = Color };
            paint.SetMaskFilter(new BlurMaskFilter(blur / OrbScale, BlurMaskFilter.Blur.Normal!));
            canvas.DrawCircle(size / 2f, size / 2f, r / OrbScale, paint);
        }

        public void Release()
        {
            Bitmap?.Recycle();
            Bitmap = null;
        }
    }

    public AnimatedHeroCard(Context context) : base(context)
    {
        SetWillNotDraw(false);
        _radius = UiKit.Dp(context, 24);
        // 右上是天界的光（偏藍白），左下是熔金
        _top = new Orb(80, 60, Color.Argb(46, 150, 200, 255), 12000L);
        _bottom = new Orb(60, 50, Color.Argb(38, 255, 190, 92), 16000L);
        _tick = new Java.Lang.Runnable(() =>
        {
            if (!_running)
                return;
            Invalidate();
            PostOnAnimationDelayed(_tick!, FrameMs);
        });
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        _startMs = SystemClock.UptimeMillis();
    }

    protected override void OnDetachedFromWindow()
    {
        Run(false);
        _top.Release();
        _bottom.Release();
        base.OnDetachedFromWindow();
    }

    /// <summary>這個 view 看得到（含 window 在前景）才動</summary>
    public override void OnVisibilityAggregated(bool isVisible)
    {
        base.OnVisibilityAggregated(isVisible);
        Run(isVisible);
    }

    private void Run(bool on)
    {
        if (on == _running)
            return;
        _running = on;
        RemoveCallbacks(_tick);
        if (on)
            PostOnAnimation(_tick);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        _rect.Set(0, 0, w, h);
        _clip.Reset();
        _clip.AddRoundRect(_rect, _radius, _radius, Android.Graphics.Path.Direction.Cw!);
        // 餘燼紅 → 紫 → 天界藍：MT2 主視覺的地獄對天界。流動只是把這個 shader 左右平移
        _gradient = new LinearGradient(0, 0, w * 1.35f, h,
            new int[] { Palette.PrimaryContainer, Palette.SecondaryContainer, Palette.Heaven },
            new float[] { 0f, 0.55f, 1f }, Shader.TileMode.Clamp!);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float width = Width, height = Height;
        if (width <= 0 || height <= 0 || _gradient == null)
            return;
        long elapsed = SystemClock.UptimeMillis() - _startMs;
        float cycle = elapsed % 8000L / 8000f;
        float flow = (float)(0.5 - 0.5 * Math.Cos(Math.PI * 2 * cycle));
        canvas.Save();
        canvas.ClipPath(_clip);

        float offset = (flow - 0.5f) * width * 1.4f;
        _gradientMatrix.SetTranslate(-offset, 0);
        _gradient.SetLocalMatrix(_gradientMatrix);
        _paint.SetShader(_gradient);
        canvas.DrawRoundRect(_rect, _radius, _radius, _paint);
        _paint.SetShader(null);

        DrawOrb(canvas, _top, width, height, elapsed, top: true);
        DrawOrb(canvas, _bottom, width, height, elapsed, top: false);
        canvas.Restore();
    }

    private void DrawOrb(Canvas canvas, Orb orb, float width, float height, long elapsed, bool top)
    {
        orb.Prepare(Context!);
        float cycle = elapsed % orb.PeriodMs / (float)orb.PeriodMs;
        float eased = (float)(0.5 - 0.5 * Math.Cos(Math.PI * 2 * cycle));
        if (!top)
            eased = 1f - eased;
        float dp(float v) => UiKit.Dp(Context!, v);
        float scale = 1f + 0.1f * eased;
        float cx = top ? width + dp(60) - dp(20) * eased : dp(40) - dp(20) * eased;
        float cy = top ? dp(40) + dp(20) * eased : height + dp(20) + dp(20) * eased;
        float half = orb.Half * scale;
        orb.Dst.Set(cx - half, cy - half, cx + half, cy + half);
        canvas.DrawBitmap(orb.Bitmap!, null, orb.Dst, _bitmapPaint);
    }
}

/// <summary>統計卡：右下角斜放的大圖示浮水印，按下時卡片縮小、浮水印轉正放大。</summary>
public sealed class StatusCardView : FrameLayout
{
    private static readonly IInterpolator Emphasized = new PathInterpolator(0.2f, 0f, 0f, 1f);
    private readonly GradientDrawable _background;
    private ImageView? _watermark;

    public StatusCardView(Context context) : base(context)
    {
        _background = UiKit.RoundedFill(context, Palette.SurfaceContainer, 16);
        Background = _background;
        ClipToOutline = true;
        Clickable = true;
        Focusable = true;
        Foreground = UiKit.Ripple(context, 16);
        Touch += (_, e) =>
        {
            if (e.Event!.ActionMasked == MotionEventActions.Down)
                Animate(true);
            else if (e.Event.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
                Animate(false);
            e.Handled = false;
        };
    }

    public void SetWatermark(ImageView watermark) => _watermark = watermark;

    private void Animate(bool pressed)
    {
        Animate()!.Cancel();
        Animate()!.ScaleX(pressed ? 0.95f : 1f).ScaleY(pressed ? 0.95f : 1f).SetDuration(200).SetInterpolator(Emphasized).Start();
        _background.SetColor(pressed ? Palette.SurfaceContainerHigh : Palette.SurfaceContainer);
        if (_watermark == null)
            return;
        _watermark.Animate()!.Cancel();
        _watermark.Animate()!.Rotation(pressed ? 0f : -15f).ScaleX(pressed ? 1.15f : 1f).ScaleY(pressed ? 1.15f : 1f)
            .Alpha(pressed ? 0.12f : 0.06f).SetDuration(400).SetInterpolator(Emphasized).Start();
    }
}

/// <summary>工具格：圖示加兩行以內的標籤。highlight 時外框是主色，並且一圈一圈往外擴散（提示「先做這個」）。</summary>
public sealed class ActionItemView : FrameLayout
{
    private readonly bool _highlight;
    private readonly GradientDrawable _background;
    private readonly Paint _pulse = new(PaintFlags.AntiAlias);
    private readonly RectF _pulseRect = new();
    private long _startMs;
    private bool _attached;
    private readonly Action _tick;

    public ActionItemView(Context context, bool highlight) : base(context)
    {
        _highlight = highlight;
        SetWillNotDraw(false);
        _background = UiKit.RoundedFill(context, Palette.Surface, 16);
        ApplyBackground(false);
        Background = _background;
        ClipToOutline = true;
        Clickable = true;
        Focusable = true;
        Foreground = UiKit.Ripple(context, 16);
        Touch += (_, e) =>
        {
            if (e.Event!.ActionMasked == MotionEventActions.Down)
                ApplyBackground(true);
            else if (e.Event.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
                ApplyBackground(false);
            e.Handled = false;
        };
        _tick = () =>
        {
            if (!_attached || !_highlight)
                return;
            Invalidate();
            PostOnAnimation(new Java.Lang.Runnable(_tick!));
        };
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        _attached = true;
        if (_highlight)
        {
            _startMs = SystemClock.UptimeMillis();
            PostOnAnimation(new Java.Lang.Runnable(_tick));
        }
    }

    protected override void OnDetachedFromWindow()
    {
        _attached = false;
        base.OnDetachedFromWindow();
    }

    private void ApplyBackground(bool pressed)
    {
        _background.SetColor(pressed ? Palette.SurfaceContainer : Palette.Surface);
        _background.SetStroke(UiKit.Dp(Context!, 1), _highlight ? Palette.Primary : Palette.HairLine);
    }

    protected override void DispatchDraw(Canvas canvas)
    {
        base.DispatchDraw(canvas);
        if (!_highlight)
            return;
        float progress = (SystemClock.UptimeMillis() - _startMs) % 2000L / 2000f;
        float stroke = UiKit.Dp(Context!, 1f + progress * 10f);
        int alpha = (int)(102f * (1f - Math.Min(progress / 0.7f, 1f)));
        if (alpha <= 0)
            return;
        _pulse.SetStyle(Paint.Style.Stroke);
        _pulse.StrokeWidth = stroke;
        _pulse.Color = Color.Argb(alpha, 255, 190, 92);
        float inset = stroke / 2f;
        _pulseRect.Set(inset, inset, Width - inset, Height - inset);
        float radius = UiKit.Dp(Context!, 16);
        canvas.DrawRoundRect(_pulseRect, radius, radius, _pulse);
    }
}

/// <summary>按下時縮小、放開時彈回（啟動按鈕用）。</summary>
public static class PressScale
{
    public static void Attach(View view, float pressedScale = 0.97f, long durationMs = 100)
    {
        view.Touch += (_, e) =>
        {
            if (e.Event!.ActionMasked == MotionEventActions.Down)
            {
                view.Animate()!.Cancel();
                view.Animate()!.ScaleX(pressedScale).ScaleY(pressedScale).SetDuration(durationMs).Start();
            }
            else if (e.Event.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
            {
                view.Animate()!.Cancel();
                view.Animate()!.ScaleX(1f).ScaleY(1f).SetDuration(durationMs).Start();
            }
            e.Handled = false;
        };
    }
}
