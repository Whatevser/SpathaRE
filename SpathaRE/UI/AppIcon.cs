using System.Drawing;
using System.Drawing.Drawing2D;

namespace SpathaRE.UI;

/// <summary>The app icon (Assets/SpathaRE.ico) and the live tray glyph.</summary>
public static class AppIcon
{
    private static Icon? _app;

    /// <summary>The mouse icon at tray size.</summary>
    public static Icon App
    {
        get
        {
            if (_app is not null) return _app;
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SpathaRE.ico"));
            using var stream = info.Stream;
            var size = System.Windows.Forms.SystemInformation.SmallIconSize;
            _app = new Icon(stream, size);
            return _app;
        }
    }

    /// <summary>
    /// Tray glyph: the mouse icon when nothing is connected, otherwise the active
    /// profile number inside a battery ring (ring omitted for wired mice).
    /// </summary>
    public static Icon Tray(int? profile, int battery)
    {
        if (profile is null) return (Icon)App.Clone();

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using (var bg = new SolidBrush(Color.FromArgb(235, 13, 20, 24)))
                g.FillEllipse(bg, 2, 2, 28, 28);

            Color accent = Color.FromArgb(33, 196, 232);
            if (battery >= 0)
            {
                Color arc = battery <= 15 ? Color.FromArgb(229, 72, 77)
                          : battery <= 35 ? Color.FromArgb(245, 165, 36)
                                          : accent;
                using var track = new Pen(Color.FromArgb(42, 61, 70), 3f);
                using var fill = new Pen(arc, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(track, 3, 3, 26, 26, 0, 360);
                g.DrawArc(fill, 3, 3, 26, 26, -90, Math.Max(4, (int)(360f * battery / 100f)));
            }
            else
            {
                using var ring = new Pen(accent, 2.5f);
                g.DrawEllipse(ring, 3, 3, 26, 26);
            }

            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(profile.Value.ToString(), font, brush, new RectangleF(0, 0, 32, 33), fmt);
        }

        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { NativeMethods.DestroyIcon(h); }
    }
}
