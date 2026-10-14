using System.Drawing.Drawing2D;

namespace Sofabird.Companion.Tray;

/// <summary>The tray icon is drawn in code (no binary resources): an orange cone + a "play" triangle.</summary>
public static class TrayIconFactory
{
    public static Icon Create(bool active)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var body = active ? Color.FromArgb(0xF5, 0x8A, 0x1F) : Color.FromArgb(0x80, 0x80, 0x80);
            using var brush = new SolidBrush(body);
            // Cone
            var cone = new PointF[] { new(10, 4), new(22, 4), new(27, 28), new(5, 28) };
            g.FillPolygon(brush, cone);
            using var stripe = new SolidBrush(Color.White);
            g.FillPolygon(stripe, new PointF[] { new(11.5f, 11), new(20.5f, 11), new(21.5f, 15), new(10.5f, 15) });
            // Play triangle
            using var dark = new SolidBrush(Color.FromArgb(0x30, 0x30, 0x30));
            g.FillPolygon(dark, new PointF[] { new(13, 17), new(13, 26), new(21, 21.5f) });
        }
        var handle = bmp.GetHicon();
        using var tmp = Icon.FromHandle(handle);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
