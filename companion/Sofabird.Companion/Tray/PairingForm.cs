using Sofabird.Companion.Localization;

namespace Sofabird.Companion.Tray;

/// <summary>
/// Pairing window on the TV screen: borderless, on top of all windows, a large 6-digit code and the address.
/// Closes when the code expires, on click/Esc or on successful pairing.
/// </summary>
public sealed class PairingForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _codeLabel;
    private readonly Label _addressLabel;
    private readonly Label _timerLabel;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly DateTimeOffset _expiresAt;

    public PairingForm(string code, string address, DateTimeOffset expiresAt, Screen target, string? requestingDevice = null)
    {
        _expiresAt = expiresAt;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(18, 18, 18);
        ForeColor = Color.White;
        Text = UiText.PairingTitle;
        KeyPreview = true;
        Cursor = Cursors.Hand;

        var width = Math.Min(900, target.WorkingArea.Width - 80);
        var height = Math.Min(460, target.WorkingArea.Height - 80);
        Size = new Size(width, height);
        Location = new Point(target.WorkingArea.X + (target.WorkingArea.Width - width) / 2,
            target.WorkingArea.Y + (target.WorkingArea.Height - height) / 2);

        var title = _titleLabel = new Label
        {
            Text = TitleFor(requestingDevice),
            Font = new Font("Segoe UI", 22, FontStyle.Regular),
            AutoEllipsis = true,
            Dock = DockStyle.Top,
            Height = 90,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _codeLabel = new Label
        {
            Text = $"{code[..3]} {code[3..]}",
            Font = new Font("Segoe UI", 96, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 190,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(0xF5, 0x8A, 0x1F),
        };
        _addressLabel = new Label
        {
            Text = UiText.PairingAddress(address),
            Font = new Font("Segoe UI", 18),
            Dock = DockStyle.Top,
            Height = 60,
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
        Controls.Add(title);

        foreach (Control c in Controls) c.Click += (_, _) => Close();
        Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => UpdateTimer();
        _timer.Start();
        UpdateTimer();
    }

    private static string TitleFor(string? device) =>
        string.IsNullOrWhiteSpace(device) ? UiText.PairingHeaderGeneric : UiText.PairingHeaderDevice(device);

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
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
