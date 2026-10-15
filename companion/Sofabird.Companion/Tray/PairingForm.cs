using Sofabird.Companion.Localization;

namespace Sofabird.Companion.Tray;

/// <summary>
/// Pairing window on the TV screen: borderless, on top of all windows, the app's name, a large 6-digit code and the
/// address. Closes when the code expires, on click/Esc or on successful pairing. Every row is measured at the window's
/// DPI (fonts are in points and grow with the display scale), so nothing is clipped at any scale or text length.
/// </summary>
public sealed class PairingForm : Form
{
    private const string CodeSample = "000 000";

    private readonly Screen _target;
    private readonly Label _brandLabel;
    private readonly Label _titleLabel;
    private readonly Label _codeLabel;
    private readonly Label _addressLabel;
    private readonly Label _timerLabel;
    private readonly Bitmap _mark;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly DateTimeOffset _expiresAt;

    public PairingForm(string code, string address, DateTimeOffset expiresAt, Screen target, string? requestingDevice = null)
    {
        _expiresAt = expiresAt;
        _target = target;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(18, 18, 18);
        ForeColor = Color.White;
        Text = UiText.PairingTitle;
        KeyPreview = true;
        Cursor = Cursors.Hand;

        _mark = TrayIconFactory.CreateBitmap(64);
        _brandLabel = new Label
        {
            Text = UiText.PairingBrand,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            Dock = DockStyle.Top,
            ForeColor = TrayIconFactory.Accent,
        };
        _brandLabel.Paint += PaintBrand;
        _titleLabel = new Label
        {
            Text = TitleFor(requestingDevice),
            Font = new Font("Segoe UI", 22, FontStyle.Regular),
            AutoEllipsis = true,
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _codeLabel = new Label
        {
            Text = $"{code[..3]} {code[3..]}",
            Font = new Font("Segoe UI", 96, FontStyle.Bold),
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TrayIconFactory.Accent,
        };
        _addressLabel = new Label
        {
            Text = UiText.PairingAddress(address),
            Font = new Font("Segoe UI", 18),
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _timerLabel = new Label
        {
            Font = new Font("Segoe UI", 14),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Silver,
        };
        Controls.Add(_timerLabel);
        Controls.Add(_addressLabel);
        Controls.Add(_codeLabel);
        Controls.Add(_titleLabel);
        Controls.Add(_brandLabel);

        foreach (Control c in Controls) c.Click += (_, _) => Close();
        Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Load += (_, _) => FitToContent();
        DpiChanged += (_, _) => FitToContent();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => UpdateTimer();
        _timer.Start();
        UpdateTimer();
    }

    private static string TitleFor(string? device) =>
        string.IsNullOrWhiteSpace(device) ? UiText.PairingHeaderGeneric : UiText.PairingHeaderDevice(device);

    /// <summary>Sizes the rows to their text at the current DPI and centres the window on the target screen.</summary>
    private void FitToContent()
    {
        var area = _target.WorkingArea;
        var pad = LogicalToDeviceUnits(24);
        var width = Math.Min(LogicalToDeviceUnits(1000), area.Width - 2 * pad);
        var inner = width - 2 * pad;
        using var g = CreateGraphics();
        int Measure(Label label, string text, bool wrap) => TextRenderer.MeasureText(g, text, label.Font,
            new Size(inner, int.MaxValue), wrap ? TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl : TextFormatFlags.SingleLine).Height;

        // The code shrinks rather than overflow a narrow screen.
        var codeSize = TextRenderer.MeasureText(g, CodeSample, _codeLabel.Font);
        if (codeSize.Width > inner)
        {
            var old = _codeLabel.Font;
            _codeLabel.Font = new Font(old.FontFamily, old.Size * inner / codeSize.Width, old.Style);
            old.Dispose();
            codeSize = TextRenderer.MeasureText(g, CodeSample, _codeLabel.Font);
        }

        _brandLabel.Height = Measure(_brandLabel, _brandLabel.Text, wrap: false) + pad;
        _titleLabel.Height = Measure(_titleLabel, _titleLabel.Text, wrap: false) + pad / 2;
        _codeLabel.Height = codeSize.Height + pad / 2;
        _addressLabel.Height = Math.Max(Measure(_addressLabel, _addressLabel.Text, wrap: true),
            Measure(_addressLabel, UiText.PairingDone(new string('W', 24)), wrap: true)) + pad / 2;
        var timer = Math.Max(Measure(_timerLabel, UiText.PairingTimer("4:59"), wrap: true),
            Measure(_timerLabel, UiText.PairingClosing, wrap: true)) + pad;
        var height = Math.Min(_brandLabel.Height + _titleLabel.Height + _codeLabel.Height + _addressLabel.Height + timer + pad,
            area.Height - 2 * pad);
        Padding = new Padding(pad, pad / 2, pad, pad / 2);
        Size = new Size(width, height);
        Location = new Point(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2);
    }

    /// <summary>The app mark and name, centred together, so the TV says which program asks.</summary>
    private void PaintBrand(object? sender, PaintEventArgs e)
    {
        var label = _brandLabel;
        e.Graphics.Clear(label.BackColor);
        var text = TextRenderer.MeasureText(e.Graphics, label.Text, label.Font);
        var mark = text.Height;
        var gap = mark / 3;
        var x = (label.ClientSize.Width - mark - gap - text.Width) / 2;
        var y = (label.ClientSize.Height - mark) / 2;
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(_mark, new Rectangle(x, y, mark, mark));
        TextRenderer.DrawText(e.Graphics, label.Text, label.Font, new Point(x + mark + gap, (label.ClientSize.Height - text.Height) / 2),
            label.ForeColor);
    }

    /// <summary>A repeated request (possibly from another phone) — update the header, the code stays the same.</summary>
    public void SetRequester(string? device)
    {
        if (!string.IsNullOrWhiteSpace(device)) _titleLabel.Text = TitleFor(device);
    }

    /// <summary>Show on top of all windows (the window is already open or has just been created).</summary>
    public void BringToFrontTopMost()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!Visible) Show();
        TopMost = false;
        TopMost = true;
        BringToFront();
        Activate();
    }

    private void UpdateTimer()
    {
        var left = _expiresAt - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            Close();
            return;
        }
        _timerLabel.Text = UiText.PairingTimer(left.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture));
    }

    public void ShowPaired(string? deviceName)
    {
        _codeLabel.Text = "✓";
        _codeLabel.ForeColor = Color.LightGreen;
        _addressLabel.Text = UiText.PairingDone(deviceName ?? "?");
        _timerLabel.Text = UiText.PairingClosing;
        _timer.Stop();
        var close = new System.Windows.Forms.Timer { Interval = 3000 };
        close.Tick += (_, _) => { close.Stop(); close.Dispose(); Close(); };
        close.Start();
    }

    protected override bool ShowWithoutActivation => false;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _mark.Dispose();
        }
        base.Dispose(disposing);
    }
}
