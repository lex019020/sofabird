using VlcRemote.Companion.Localization;
using VlcRemote.Companion.Net;
using VlcRemote.Core.Setup;

namespace VlcRemote.Companion.Setup;

/// <summary>
/// "Setup and permissions": every permission or system change the companion makes, with what it is and why it is
/// needed. Shown at the first start (and once more when <see cref="SetupPolicy.CurrentVersion"/> grows), and from the
/// tray at any time. Layout is built from auto-sized table/flow panels so that text wraps instead of being cut, at
/// any DPI and in any language.
/// </summary>
public sealed class SetupForm : Form
{
    private const int ContentWidth = 600; // logical pixels (96 DPI), scaled by AutoScaleMode.Dpi

    private readonly ISetupBackend _backend;
    private readonly Item _firewall;
    private readonly Item _autostart;
    private readonly Item _vlc;
    private readonly Item _keepAwake;
    private readonly Item _bluetooth;
    private readonly Label _networkHint;
    private readonly LinkLabel _networkLink;
    private readonly Button _apply;
    private readonly Button _close;
    private readonly Panel _body;
    private readonly TableLayoutPanel _items;
    private readonly TableLayoutPanel _header;
    private readonly TableLayoutPanel _footer;
    private readonly Icon _icon;
    private SetupSnapshot? _snapshot;
    private SetupResult? _result;
    private bool _choicesInitialized;
    private bool _busy;
    private bool _exiting;
    private bool _fitted;

    public SetupForm(ISetupBackend backend, bool firstRun)
    {
        _backend = backend;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = UiText.SetupTitle;
        Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 9.75F);
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        _icon = Tray.TrayIconFactory.Create(active: true);
        Icon = _icon;

        // Header: title + what this window is about.
        _header = Table(1);
        _header.Dock = DockStyle.Top;
        _header.Padding = new Padding(24, 20, 24, 12);
        _header.Controls.Add(TextLabel(UiText.SetupHeader, new Font(Font.FontFamily, 15F, FontStyle.Regular), SystemColors.WindowText, new Padding(0, 0, 0, 8)));
        _header.Controls.Add(TextLabel(UiText.SetupIntro, Font, SecondaryText, Padding.Empty));

        // Items.
        _items = Table(1);
        _items.Dock = DockStyle.Top;
        _items.Padding = new Padding(24, 0, 24, 8);

        _firewall = AddItem(UiText.SetupFirewallTitle, UiText.SetupFirewallText, toggle: true);
        _networkHint = TextLabel("", Font, WarningColor, new Padding(0, 4, 0, 0));
        _networkHint.Visible = false;
        _firewall.Content.Controls.Add(_networkHint);
        _networkLink = Link(UiText.SetupOpenNetworkSettings, () => _backend.OpenNetworkSettings());
        _networkLink.Visible = false;
        _firewall.Content.Controls.Add(_networkLink);

        _autostart = AddItem(UiText.SetupAutostartTitle, UiText.SetupAutostartText, toggle: true);
        _vlc = AddItem(UiText.SetupVlcTitle, UiText.SetupVlcText, toggle: true);
        _keepAwake = AddItem(UiText.SetupKeepAwakeTitle, UiText.SetupKeepAwakeText, toggle: true);
        _bluetooth = AddItem(UiText.SetupBluetoothTitle, UiText.SetupBluetoothText, toggle: false, last: true);
        _bluetooth.Content.Controls.Add(Link(UiText.SetupOpenBluetoothSettings, () => _backend.OpenBluetoothSettings()));

        _body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = SystemColors.Window };
        _body.Controls.Add(_items);

        // Footer: hint where to find the window again + buttons.
        _footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(24, 12, 24, 12),
            BackColor = SystemColors.Control,
        };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var note = TextLabel(UiText.SetupFooterNote, Font, SystemColors.ControlText, new Padding(0, 0, 16, 0));
        note.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _footer.Controls.Add(note, 0, 0);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
        };
        _apply = DialogButton(UiText.SetupApply);
        _apply.Click += async (_, _) => await ApplyAsync();
        _close = DialogButton(firstRun ? UiText.SetupLater : UiText.SetupClose);
        _close.Click += (_, _) => Close();
        buttons.Controls.Add(_apply);
        buttons.Controls.Add(_close);
        _footer.Controls.Add(buttons, 1, 0);
        AcceptButton = _apply;
        CancelButton = _close;

        // Dock order: the last added is docked first.
        Controls.Add(_body);
        Controls.Add(_footer);
        Controls.Add(_header);
        ClientSize = new Size(ContentWidth + 48, 560);
        ResumeLayout(false);
        PerformLayout();

        SetEnabled(false);
        foreach (var item in AllItems) item.Status.Text = UiText.SetupChecking;
    }

    private IEnumerable<Item> AllItems => [_firewall, _autostart, _vlc, _keepAwake, _bluetooth];

    private static bool HighContrast => SystemInformation.HighContrast;
    private static Color SecondaryText => HighContrast ? SystemColors.WindowText : Color.FromArgb(0x5A, 0x5A, 0x5A);
    private static Color OkColor => HighContrast ? SystemColors.WindowText : Color.FromArgb(0x0F, 0x7B, 0x0F);
    private static Color WarningColor => HighContrast ? SystemColors.WindowText : Color.FromArgb(0x9D, 0x5D, 0x00);
    private static Color ErrorColor => HighContrast ? SystemColors.WindowText : Color.FromArgb(0xC4, 0x2B, 0x1C);

    private enum Tone { Neutral, Ok, Warning, Error }

    private sealed record Item(CheckBox? Toggle, Label Title, TableLayoutPanel Content, Label Status);

    private static TableLayoutPanel Table(int columns)
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        for (var i = 0; i < columns; i++)
            t.ColumnStyles.Add(i == columns - 1 ? new ColumnStyle(SizeType.Percent, 100F) : new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    /// <summary>A wrapping label: auto-sized, stretched to the column width, so long text wraps instead of being cut.</summary>
    private static Label TextLabel(string text, Font font, Color color, Padding margin) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = margin,
        UseMnemonic = false,
    };

    private static LinkLabel Link(string text, Action onClick)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 0),
            UseMnemonic = false,
        };
        link.LinkClicked += (_, _) => onClick();
        return link;
    }

    private static Button DialogButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(100, 30),
        Padding = new Padding(10, 2, 10, 2),
        Margin = new Padding(8, 0, 0, 0),
        UseVisualStyleBackColor = true,
    };

    private Item AddItem(string title, string description, bool toggle, bool last = false)
    {
        var card = Table(2);
        card.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        card.Padding = new Padding(0, 12, 0, 12);

        CheckBox? cb = null;
        if (toggle)
        {
            cb = new CheckBox
            {
                AutoSize = true,
                Margin = new Padding(0, 3, 10, 0),
                AccessibleName = title,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
            };
            card.Controls.Add(cb, 0, 0);
        }
        else
        {
            // Keeps the text aligned with the items that have a check box.
            int glyph;
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                glyph = CheckBoxRenderer.GetGlyphSize(g, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal).Width;
            card.Controls.Add(new Panel { Size = new Size(glyph + 2, 1), Margin = new Padding(0, 3, 10, 0) }, 0, 0);
        }

        var content = Table(1);
        content.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        var titleLabel = TextLabel(title, new Font(Font.FontFamily, 10.5F, FontStyle.Bold), SystemColors.WindowText, new Padding(0, 0, 0, 4));
        if (cb is not null)
        {
            titleLabel.Cursor = Cursors.Hand;
            titleLabel.Click += (_, _) => { if (cb.Enabled) cb.Checked = !cb.Checked; };
        }
        content.Controls.Add(titleLabel);
        content.Controls.Add(TextLabel(description, Font, SecondaryText, new Padding(0, 0, 0, 6)));
        var status = TextLabel("", Font, SystemColors.WindowText, Padding.Empty);
        content.Controls.Add(status);
        card.Controls.Add(content, 1, 0);

        _items.Controls.Add(card);
        if (!last)
            _items.Controls.Add(new Panel
            {
                Height = 1,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = Padding.Empty,
                BackColor = HighContrast ? SystemColors.WindowText : Color.FromArgb(0xE5, 0xE5, 0xE5),
            });
        return new Item(cb, titleLabel, content, status);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitHeight();
        await RefreshStateAsync();
    }

    /// <summary>
    /// Height to fit the content, within the screen; the item list scrolls if the screen is too small. The first fit
    /// centres the window; later ones (new status text, another DPI) only grow it and keep it on the screen.
    /// </summary>
    private void FitHeight()
    {
        MinimumSize = Size.Empty;
        var itemsHeight = _items.GetPreferredSize(new Size(_body.ClientSize.Width, 0)).Height;
        var wanted = _header.Height + itemsHeight + _footer.Height;
        var area = Screen.FromControl(this).WorkingArea;
        var maxClient = area.Height - (Height - ClientSize.Height) - LogicalToDeviceUnits(40);
        var client = Math.Min(wanted, maxClient);
        if (_fitted) client = Math.Max(client, Math.Min(ClientSize.Height, maxClient));
        if (client != ClientSize.Height) ClientSize = new Size(ClientSize.Width, client);
        if (!_fitted) Top = Math.Max(area.Top, area.Top + (area.Height - Height) / 2);
        else if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
        _fitted = true;
        MinimumSize = new Size(Width, Math.Min(Height, LogicalToDeviceUnits(360)));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        MinimumSize = Size.Empty; // the old minimum is in the old DPI's pixels
        base.OnDpiChanged(e);
        BeginInvoke(FitHeight);
    }

    /// <summary>Re-reads the state (from the tray, the window may be opened again later).</summary>
    public async Task RefreshStateAsync()
    {
        if (_busy) return;
        try
        {
            _snapshot = await Task.Run(_backend.Inspect);
        }
        catch (Exception ex)
        {
            _snapshot = null;
            SetStatus(_firewall, UiText.SetupFirewallUnknown(ex.Message), Tone.Warning);
        }
        if (IsDisposed) return;
        if (_snapshot is not null)
        {
            InitChoices(_snapshot);
            Render(_snapshot);
        }
        SetEnabled(true);
        FitHeight();
    }

    /// <summary>Check boxes start from the defaults (first run) or from what the user chose last time.</summary>
    private void InitChoices(SetupSnapshot s)
    {
        if (_choicesInitialized) return;
        _choicesInitialized = true;
        _firewall.Toggle!.Checked = !s.Completed || s.ManageFirewall;
        _autostart.Toggle!.Checked = !s.Completed || s.Autostart;
        _vlc.Toggle!.Checked = s.Vlc != VlcSetupState.NotInstalled && (!s.Completed || s.ConfigureVlc);
        _keepAwake.Toggle!.Checked = s.KeepAwake;
    }

    private void Render(SetupSnapshot s)
    {
        RenderFirewall(s);
        if (_result?.Autostart is { Outcome: StepOutcome.Failed } af)
            SetStatus(_autostart, UiText.SetupAutostartFailed(af.Detail ?? ""), Tone.Error);
        else
            SetStatus(_autostart, s.Autostart ? UiText.SetupOn : UiText.SetupOff, s.Autostart ? Tone.Ok : Tone.Neutral);

        _vlc.Toggle!.Enabled = s.Vlc != VlcSetupState.NotInstalled;
        if (_result?.Vlc is { Outcome: StepOutcome.Failed })
            SetStatus(_vlc, UiText.SetupVlcFailed, Tone.Error);
        else
            switch (s.Vlc)
            {
                case VlcSetupState.NotInstalled: SetStatus(_vlc, UiText.SetupVlcNotInstalled, Tone.Warning); break;
                case VlcSetupState.Configured: SetStatus(_vlc, UiText.SetupVlcConfigured, Tone.Ok); break;
                case VlcSetupState.VlcRunning: SetStatus(_vlc, UiText.SetupVlcRunning, Tone.Neutral); break;
                default:
                    SetStatus(_vlc, s.Completed && !s.ConfigureVlc ? UiText.SetupVlcOff : UiText.SetupVlcNotConfigured, Tone.Neutral);
                    break;
            }

        SetStatus(_keepAwake, s.KeepAwake ? UiText.SetupOn : UiText.SetupOff, s.KeepAwake ? Tone.Ok : Tone.Neutral);

        switch (s.Bluetooth)
        {
            case BluetoothServerStatus.Advertising: SetStatus(_bluetooth, UiText.SetupBluetoothOn, Tone.Ok); break;
            case BluetoothServerStatus.Starting: SetStatus(_bluetooth, UiText.SetupBluetoothStarting, Tone.Neutral); break;
            case BluetoothServerStatus.RadioOff: SetStatus(_bluetooth, UiText.SetupBluetoothRadioOff, Tone.Neutral); break;
            case BluetoothServerStatus.Disabled: SetStatus(_bluetooth, UiText.SetupBluetoothDisabled, Tone.Neutral); break;
            default: SetStatus(_bluetooth, UiText.SetupBluetoothNoAdapter, Tone.Neutral); break;
        }
    }

    private void RenderFirewall(SetupSnapshot s)
    {
        var fr = _result?.Firewall;
        if (fr is { Outcome: StepOutcome.Cancelled })
            SetStatus(_firewall, UiText.SetupFirewallCancelled, Tone.Warning);
        else if (fr is { Outcome: StepOutcome.Failed })
            SetStatus(_firewall, UiText.SetupFirewallFailed(fr.Detail ?? ""), Tone.Error);
        else if (s.Firewall is null)
            SetStatus(_firewall, UiText.SetupFirewallUnknown(s.FirewallError ?? ""), Tone.Warning);
        else if (!s.Firewall.PrivateProfileEnabled)
            SetStatus(_firewall, UiText.SetupFirewallOff, Tone.Ok);
        else if (s.Firewall.AnyBlocked)
            SetStatus(_firewall, UiText.SetupFirewallBlocked(Names(s.Firewall.Programs.Where(p => p.Blocked))), Tone.Warning);
        else if (s.Firewall.AnyMissing)
            SetStatus(_firewall, s.Completed && !s.ManageFirewall ? UiText.SetupFirewallNotManaged : UiText.SetupFirewallMissing, Tone.Neutral);
        else
            SetStatus(_firewall, UiText.SetupFirewallOk, Tone.Ok);

        var publicNet = s.PublicNetworks.FirstOrDefault();
        _networkHint.Visible = _networkLink.Visible = publicNet is not null;
        if (publicNet is not null) _networkHint.Text = UiText.SetupPublicNetwork(publicNet.Name);
    }

    private static string Names(IEnumerable<FirewallProgramStatus> programs)
    {
        var names = programs.Select(p => p.Program.Key == FirewallPlanner.VlcKey ? UiText.SetupProgramVlc : UiText.SetupProgramCompanion).ToList();
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => UiText.SetupAnd(string.Join(", ", names.Take(names.Count - 1)), names[^1]),
        };
    }

    private static void SetStatus(Item item, string text, Tone tone)
    {
        item.Status.Text = text;
        item.Status.ForeColor = tone switch
        {
            Tone.Ok => OkColor,
            Tone.Warning => WarningColor,
            Tone.Error => ErrorColor,
            _ => SystemColors.WindowText,
        };
    }

    private void SetEnabled(bool enabled)
    {
        foreach (var item in AllItems)
            if (item.Toggle is not null)
                item.Toggle.Enabled = enabled && (item != _vlc || _snapshot?.Vlc != VlcSetupState.NotInstalled);
        _apply.Enabled = enabled;
        _close.Enabled = enabled;
    }

    private async Task ApplyAsync()
    {
        if (_busy || _snapshot is null) return;
        _busy = true;
        SetEnabled(false);
        _result = null;
        var choices = new SetupChoices(_firewall.Toggle!.Checked, _autostart.Toggle!.Checked, _vlc.Toggle!.Checked, _keepAwake.Toggle!.Checked);
        if (choices.Firewall && _snapshot.Firewall is not { Ok: true })
            SetStatus(_firewall, UiText.SetupFirewallWaiting, Tone.Neutral);
        try
        {
            _result = await _backend.ApplyAsync(choices, CancellationToken.None);
        }
        finally
        {
            _busy = false;
        }
        if (IsDisposed) return;
        _close.Text = UiText.SetupClose;
        await RefreshStateAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The UAC prompt / helper is still running: keep the window until its result is shown.
        if (_busy && !_exiting && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    /// <summary>Closes the window even while applying (the companion is exiting).</summary>
    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    /// <summary>Shows the window in front (it may open at startup behind other windows).</summary>
    public void BringToFrontActivated()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!Visible) Show();
        TopMost = true;
        TopMost = false;
        BringToFront();
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Dispose();
        base.Dispose(disposing);
    }
}
