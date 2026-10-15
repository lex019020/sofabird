using System.Drawing.Drawing2D;

namespace Sofabird.Companion.Tray;

/// <summary>
/// The app icon drawn in code (no binary resources): the phone app's launcher mark, a bar and a "play" triangle, on a
/// rounded square; orange when phones can connect, grey otherwise. Never VLC's cone (see ADR-0006).
/// </summary>
public static class TrayIconFactory
{
    public static readonly Color Accent = Color.FromArgb(0xF5, 0x8A, 0x1F);

    public static Icon Create(bool active)
    {
        using var bmp = CreateBitmap(32, active);
        var handle = bmp.GetHicon();
        using var tmp = Icon.FromHandle(handle);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    /// <summary>The mark at any size (the pairing window draws it next to the app name).</summary>
    public static Bitmap CreateBitmap(int size, bool active = true)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        var s = size / 32f;
        using (var body = new SolidBrush(active ? Accent : Color.FromArgb(0x80, 0x80, 0x80)))
        using (var square = RoundedSquare(size, 7 * s))
            g.FillPath(body, square);
        using var white = new SolidBrush(Color.White);
        g.FillRectangle(white, 6 * s, 8.8f * s, 3.2f * s, 14.4f * s);
        g.FillPolygon(white, new PointF[] { new(11.6f * s, 6.4f * s), new(11.6f * s, 25.6f * s), new(26 * s, 16 * s) });
        return bmp;
    }

    private static GraphicsPath RoundedSquare(int size, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(size - d - 1, 0, d, d, 270, 90);
        path.AddArc(size - d - 1, size - d - 1, d, d, 0, 90);
        path.AddArc(0, size - d - 1, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
