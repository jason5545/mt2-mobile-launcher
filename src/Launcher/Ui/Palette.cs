using Android.Graphics;

namespace Mt2Mobile.Launcher.Ui;

/// <summary>
/// 配色。角色（background、surface、primary…）照 Sts2MobileLauncher 的 Material 3 殼一一對應，
/// 色相換成 MT2 主視覺：logo 的熔金（primary）、地獄側的餘燼紅（primary container）、中間的紫（secondary container）、天界側的藍（漸層終點）。
/// 背景與 surface 帶一點紫調。Resources/values/themes.xml 用同一組值。
/// </summary>
public static class Palette
{
    public static readonly Color Background = Color.Rgb(20, 16, 24);
    public static readonly Color Surface = Color.Rgb(30, 25, 35);
    public static readonly Color SurfaceContainer = Color.Rgb(41, 34, 47);
    public static readonly Color SurfaceContainerHigh = Color.Rgb(52, 44, 59);
    public static readonly Color Primary = Color.Rgb(255, 190, 92);
    public static readonly Color OnPrimary = Color.Rgb(69, 43, 0);
    public static readonly Color PrimaryContainer = Color.Rgb(110, 38, 16);
    public static readonly Color OnPrimaryContainer = Color.Rgb(255, 221, 178);
    public static readonly Color SecondaryContainer = Color.Rgb(70, 44, 84);
    public static readonly Color OnSecondaryContainer = Color.Rgb(240, 219, 255);
    public static readonly Color Heaven = Color.Rgb(24, 72, 122);
    public static readonly Color OnSurface = Color.Rgb(236, 225, 233);
    public static readonly Color OnSurfaceVariant = Color.Rgb(205, 194, 205);
    public static readonly Color Outline = Color.Rgb(138, 127, 140);
    public static readonly Color Error = Color.Rgb(255, 180, 171);
    public static readonly Color Success = Color.Rgb(160, 220, 170);
    public static readonly Color Ripple = Color.Argb(76, 255, 190, 92);
    public static readonly Color PrimaryTint = Color.Argb(31, 255, 190, 92);
    public static readonly Color HairLine = Color.Argb(13, 255, 255, 255);
    public static readonly Color Divider = Color.Argb(26, 255, 255, 255);
}
