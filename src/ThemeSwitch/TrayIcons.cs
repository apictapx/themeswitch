using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ThemeSwitch;

/// <summary>Sun and moon tray icons, drawn at runtime at the tray's real size so they stay crisp at any DPI.</summary>
static class TrayIcons
{
    public static Icon Sun() => Render(DrawSun);
    public static Icon Moon() => Render(DrawMoon);

    static void DrawSun(Graphics g, float s)
    {
        var amber = Color.FromArgb(255, 245, 165, 36);
        using var pen = new Pen(amber, s * 0.09f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            float c = s / 2, r1 = s * 0.36f, r2 = s * 0.47f;
            g.DrawLine(pen, c + (float)Math.Cos(a) * r1, c + (float)Math.Sin(a) * r1,
                            c + (float)Math.Cos(a) * r2, c + (float)Math.Sin(a) * r2);
        }
        using var fill = new SolidBrush(amber);
        g.FillEllipse(fill, s * 0.25f, s * 0.25f, s * 0.5f, s * 0.5f);
    }

    static void DrawMoon(Graphics g, float s)
    {
        using var disc = new GraphicsPath();
        disc.AddEllipse(s * 0.08f, s * 0.08f, s * 0.84f, s * 0.84f);
        using var bite = new GraphicsPath();
        bite.AddEllipse(s * 0.34f, s * -0.02f, s * 0.72f, s * 0.72f);
        using var crescent = new Region(disc);
        crescent.Exclude(bite);
        // Pale fill for dark taskbars, thin slate outline so it still reads on a light one.
        using var fill = new SolidBrush(Color.FromArgb(255, 232, 236, 248));
        g.FillRegion(fill, crescent);
        using var outline = new Pen(Color.FromArgb(200, 64, 72, 88), s * 0.04f);
        g.SetClip(crescent, CombineMode.Replace);
        g.DrawPath(outline, disc);
        g.ResetClip();
    }

    static Icon Render(Action<Graphics, float> draw)
    {
        int size = SystemInformation.SmallIconSize.Width;
        const int Supersample = 8;
        using var big = new Bitmap(size * Supersample, size * Supersample);
        using (var g = Graphics.FromImage(big))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            draw(g, big.Width);
        }
        using var small = new Bitmap(size, size);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(big, 0, 0, size, size);
        }
        IntPtr handle = small.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
}
