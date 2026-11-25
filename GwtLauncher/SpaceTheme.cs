using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GwtLauncher;

internal static class SpaceTheme
{
    // SpaceRust-inspired deep obsidian + crimson/rust-red palette
    public static readonly Color BgWindow = Color.FromArgb(11, 13, 18);
    public static readonly Color BgHeader = Color.FromArgb(200, 12, 14, 20);
    public static readonly Color BgGlassDark = Color.FromArgb(215, 16, 19, 27);
    public static readonly Color BgCard = Color.FromArgb(20, 24, 33);
    public static readonly Color BgCardHover = Color.FromArgb(29, 34, 46);
    public static readonly Color BgInput = Color.FromArgb(14, 17, 24);
    public static readonly Color BgConsole = Color.FromArgb(9, 11, 16);

    public static readonly Color BorderSubtle = Color.FromArgb(38, 44, 58);
    public static readonly Color BorderGlass = Color.FromArgb(65, 255, 255, 255);
    public static readonly Color BorderBright = Color.FromArgb(58, 67, 86);

    // Primary SpaceRust Crimson / Rust-Red accent
    public static readonly Color AccentRed = Color.FromArgb(217, 54, 38);
    public static readonly Color AccentRedBright = Color.FromArgb(239, 68, 52);
    public static readonly Color AccentRedDark = Color.FromArgb(178, 38, 26);

    // Secondary semantic accents
    public static readonly Color AccentAmber = Color.FromArgb(245, 158, 11);
    public static readonly Color AccentCyan = Color.FromArgb(56, 189, 248);
    public static readonly Color AccentGreen = Color.FromArgb(34, 197, 94);

    public static readonly Color TextPrimary = Color.FromArgb(244, 246, 250);
    public static readonly Color TextSecondary = Color.FromArgb(164, 172, 186);
    public static readonly Color TextMuted = Color.FromArgb(106, 115, 132);

    public static GraphicsPath CreateRoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        if (rect.Width <= 0 || rect.Height <= 0)
            return path;

        int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        var arc = new Rectangle(rect.Location, new Size(d, d));
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - d;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - d;
        path.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void EnableWindowRoundedCorners(IntPtr handle)
    {
        try
        {
            const int DwmwaWindowCornerPreference = 33;
            int preference = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // Ignore on older Windows builds.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
