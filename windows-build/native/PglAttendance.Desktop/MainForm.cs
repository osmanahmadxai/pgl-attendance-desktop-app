using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PglAttendance.Core;
using PglAttendance.Core.Models;

namespace PglAttendance.Desktop;

public sealed class MainForm : Form
{
    // =========================================================================
    // Design tokens — GitHub Primer-inspired palette
    // =========================================================================
    private static readonly Color Bg          = Color.FromArgb(248, 249, 251);
    private static readonly Color Surface     = Color.White;
    private static readonly Color SurfaceAlt  = Color.FromArgb(247, 248, 250);
    private static readonly Color SurfaceHover= Color.FromArgb(241, 243, 245);
    private static readonly Color BorderHair  = Color.FromArgb(231, 233, 237);
    private static readonly Color BorderSoft  = Color.FromArgb(216, 220, 226);

    private static readonly Color TextPrimary = Color.FromArgb( 31,  35,  40);
    private static readonly Color TextMuted   = Color.FromArgb(101, 109, 118);
    private static readonly Color TextSubtle  = Color.FromArgb(139, 148, 158);

    private static readonly Color Accent      = Color.FromArgb(  9, 105, 218);
    private static readonly Color AccentHover = Color.FromArgb(  8,  87, 184);
    private static readonly Color AccentTint  = Color.FromArgb(240, 247, 254);

    private static readonly Color SuccessFg   = Color.FromArgb( 26, 127,  55);
    private static readonly Color SuccessBg   = Color.FromArgb(218, 251, 225);
    private static readonly Color WarnFg      = Color.FromArgb(154, 103,   0);
    private static readonly Color WarnBg      = Color.FromArgb(255, 248, 197);
    private static readonly Color DangerFg    = Color.FromArgb(207,  34,  46);
    private static readonly Color DangerBg    = Color.FromArgb(255, 235, 233);

    private static readonly Font FontTitle      = new("Segoe UI Semibold", 14.5F);
    private static readonly Font FontH2         = new("Segoe UI Semibold", 10F);
    private static readonly Font FontBody       = new("Segoe UI", 9F);
    private static readonly Font FontBodyBold   = new("Segoe UI Semibold", 9F);
    private static readonly Font FontStatNum    = new("Segoe UI Semibold", 26F);
    private static readonly Font FontLabel      = new("Segoe UI", 8.75F);
    private static readonly Font FontPill       = new("Segoe UI Semibold", 8.25F);
    private static readonly Font FontMono       = new("Consolas", 8.25F);
    private static readonly Font FontGridHeader = new("Segoe UI Semibold", 8.75F);

    // =========================================================================
    // State
    // =========================================================================
    private readonly ServiceClient _svc = new();
    private CancellationTokenSource _sseCts = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 4000 };

    private int _page = 1;
    private const int Limit = 15;
    private int _totalPages = 0;
    private string _filterValue = "all";
    private string _searchValue = "";
    private List<ParsedAttendanceVm> _rows = new();

    // =========================================================================
    // Controls
    // =========================================================================
    // Top bar
    private readonly Label _appTitle    = new();
    private readonly Button _btnSettings = new();

    // Status chip
    private readonly Panel _statusChip  = new();
    private readonly Panel _statusDot   = new();
    private readonly Label _statusText  = new();

    // Stats
    private readonly Label _statTotal    = new();
    private readonly Label _statSynced   = new();
    private readonly Label _statUnsynced = new();

    // Toolbar (filter tabs + search + sync)
    private readonly TabButton _tabAll      = new("All records");
    private readonly TabButton _tabSynced   = new("Synced");
    private readonly TabButton _tabUnsynced = new("Pending");
    private readonly SearchBox _search       = new();
    private readonly Button _btnSyncAll      = new();

    // Body
    private readonly DataGridView _grid = new();
    private readonly Panel _activityCard = new();
    private readonly Panel _activityList = new();
    private readonly Label _activityEmpty = new();
    private readonly List<ActivityItem> _activity = new();
    private const int MaxActivity = 200;

    // Footer
    private readonly Label _pageLabel = new();
    private readonly Button _btnPrev = new();
    private readonly Button _btnNext = new();

    // Tray
    private readonly NotifyIcon _tray = new();
    private bool _reallyExit = false;

    // =========================================================================
    public MainForm()
    {
        Text = "PGL Attendance";
        Width = 1340;
        Height = 820;
        MinimumSize = new Size(1120, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = FontBody;
        ForeColor = TextPrimary;
        BackColor = Bg;
        Icon = LoadAppIcon();
        DoubleBuffered = true;

        BuildLayout();
        SetFilter("all", initial: true);
        InitTray();

        _poll.Tick += async (_, _) => await RefreshSoftAsync();
        Load += async (_, _) =>
        {
            await BootstrapAsync();
            _poll.Start();
            _ = Task.Run(() => StartSseLoopAsync(_sseCts.Token));
        };

        // Closing the X button: minimize to tray instead of exiting.
        FormClosing += (_, e) =>
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
            }
        };
        FormClosed += (_, _) =>
        {
            _poll.Stop();
            try { _sseCts.Cancel(); } catch { /* ignore */ }
            _tray.Visible = false;
            _tray.Dispose();
        };
    }

    private void InitTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "PGL Attendance — service running";
        _tray.Visible = true;

        // Single click → toggle visibility. Double-click also opens.
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new TrayMenuColors()),
        };
        var miOpen = new ToolStripMenuItem("Open Dashboard");
        miOpen.Font = new System.Drawing.Font(miOpen.Font, FontStyle.Bold);
        miOpen.Click += (_, _) => ShowFromTray();
        var miSettings = new ToolStripMenuItem("Settings…");
        miSettings.Click += async (_, _) => { ShowFromTray(); await OpenSettingsAsync(); };
        var sep = new ToolStripSeparator();
        var miExit = new ToolStripMenuItem("Exit (service keeps running)");
        miExit.Click += (_, _) => { _reallyExit = true; Close(); };

        menu.Items.Add(miOpen);
        menu.Items.Add(miSettings);
        menu.Items.Add(sep);
        menu.Items.Add(miExit);
        _tray.ContextMenuStrip = menu;
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        // First time only — let the user know where it went
        if (_tray.Tag is null)
        {
            _tray.Tag = "shown";
            try
            {
                _tray.BalloonTipTitle = "PGL Attendance";
                _tray.BalloonTipText = "Still running in the background. Click the tray icon to reopen.";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(2500);
            }
            catch { /* ignore */ }
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private sealed class TrayMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(240, 247, 254);
        public override Color MenuItemBorder => Color.FromArgb(231, 233, 237);
        public override Color MenuBorder => Color.FromArgb(216, 220, 226);
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Color.White;
        public override Color ImageMarginGradientMiddle => Color.White;
        public override Color ImageMarginGradientEnd => Color.White;
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { /* ignore */ }
        return SystemIcons.Application;
    }

    // =========================================================================
    // Layout
    // =========================================================================
    private void BuildLayout()
    {
        // --- Top bar -------------------------------------------------------
        var top = new Panel
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Surface,
            Padding = new Padding(32, 0, 32, 0),
        };
        top.Paint += (_, e) => DrawBottomHairline(e.Graphics, top);

        _appTitle.Text = "PGL Attendance";
        _appTitle.Font = FontTitle;
        _appTitle.ForeColor = TextPrimary;
        _appTitle.AutoSize = true;
        _appTitle.Location = new Point(32, 20);

        StyleGhostButton(_btnSettings, "⚙  Settings", 120);
        _btnSettings.Click += async (_, _) => await OpenSettingsAsync();
        top.Resize += (_, _) =>
        {
            _btnSettings.Location = new Point(top.Width - _btnSettings.Width - 32, 14);
        };

        top.Controls.Add(_appTitle);
        top.Controls.Add(_btnSettings);

        // --- Hero zone (status chip + stats) ------------------------------
        var hero = new Panel { Dock = DockStyle.Top, Height = 154, BackColor = Bg, Padding = new Padding(32, 22, 32, 14) };

        BuildStatusChip();
        _statusChip.Location = new Point(32, 22);
        hero.Controls.Add(_statusChip);

        var statsRow = new TableLayoutPanel
        {
            Location = new Point(32, 62),
            Height = 86,
            ColumnCount = 3,
            BackColor = Bg,
        };
        statsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
        statsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        statsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        statsRow.Controls.Add(BuildStat(_statTotal,    "Total records", TextPrimary), 0, 0);
        statsRow.Controls.Add(BuildStat(_statSynced,   "Synced",        SuccessFg),   1, 0);
        statsRow.Controls.Add(BuildStat(_statUnsynced, "Pending",       WarnFg),      2, 0);
        hero.Resize += (_, _) => statsRow.Width = hero.Width - 64;
        hero.Controls.Add(statsRow);

        // --- Toolbar (filter tabs + search + actions) ---------------------
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Bg, Padding = new Padding(32, 8, 32, 8) };

        // tabs
        _tabAll.Location      = new Point(32, 14);
        _tabSynced.Location   = new Point(_tabAll.Right + 4, 14);
        _tabUnsynced.Location = new Point(_tabSynced.Right + 4, 14);
        _tabAll.Click      += (_, _) => SetFilter("all");
        _tabSynced.Click   += (_, _) => SetFilter("synced");
        _tabUnsynced.Click += (_, _) => SetFilter("unsynced");
        toolbar.Controls.Add(_tabAll);
        toolbar.Controls.Add(_tabSynced);
        toolbar.Controls.Add(_tabUnsynced);

        // search
        _search.Location = new Point(_tabUnsynced.Right + 16, 12);
        _search.Width = 320;
        _search.Placeholder = "Search by user, status, verify type…";
        _search.TextChanged += (_, _) => { _searchValue = _search.Text.Trim(); RenderGrid(); };
        toolbar.Controls.Add(_search);

        // sync all
        StylePrimaryButton(_btnSyncAll, "Sync all unsynced", 170);
        _btnSyncAll.Click += async (_, _) => await OnSyncAllAsync();
        toolbar.Resize += (_, _) =>
        {
            _btnSyncAll.Location = new Point(toolbar.Width - _btnSyncAll.Width - 32, 12);
        };
        toolbar.Controls.Add(_btnSyncAll);

        // --- Split: Grid (left) + Activity (right) ------------------------
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 16,
            BackColor = Bg,
            FixedPanel = FixedPanel.Panel2,
            Panel1 = { BackColor = Bg, Padding = new Padding(32, 4, 8, 12) },
            Panel2 = { BackColor = Bg, Padding = new Padding(8, 4, 32, 12) },
        };
        split.Panel1MinSize = 360;
        split.Panel2MinSize = 280;

        // Robustly clamp SplitterDistance into the valid range — the WinForms
        // setter throws ArgumentOutOfRangeException if you go even 1 px outside
        // [Panel1MinSize, Width - Panel2MinSize - SplitterWidth].
        void ApplySplit()
        {
            try
            {
                if (split.IsDisposed || !split.IsHandleCreated) return;
                int width = split.Width;
                if (width <= 0) return;
                int min = split.Panel1MinSize;
                int max = width - split.Panel2MinSize - split.SplitterWidth;
                if (max < min) return;          // container is too narrow → leave default
                int desired = (int)(width * 0.62);
                int clamped = Math.Max(min, Math.Min(max, desired));
                if (split.SplitterDistance != clamped) split.SplitterDistance = clamped;
            }
            catch { /* swallow — never let layout math crash the UI */ }
        }
        split.HandleCreated += (_, _) => ApplySplit();
        split.SizeChanged   += (_, _) => ApplySplit();

        BuildGridCard(split.Panel1);
        BuildActivityCard(split.Panel2);

        // --- Footer (pagination) ------------------------------------------
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Surface };
        footer.Paint += (_, e) => DrawTopHairline(e.Graphics, footer);

        _pageLabel.Text = "—";
        _pageLabel.Font = FontLabel;
        _pageLabel.ForeColor = TextMuted;
        _pageLabel.AutoSize = true;
        _pageLabel.Location = new Point(32, 20);

        StyleGhostButton(_btnPrev, "‹  Previous", 110);
        StyleGhostButton(_btnNext, "Next  ›", 100);
        _btnPrev.Click += async (_, _) => { if (_page > 1) { _page--; await RefreshSoftAsync(); } };
        _btnNext.Click += async (_, _) => { if (_page < _totalPages) { _page++; await RefreshSoftAsync(); } };
        footer.Resize += (_, _) =>
        {
            _btnNext.Location = new Point(footer.Width - _btnNext.Width - 32, 11);
            _btnPrev.Location = new Point(_btnNext.Left - _btnPrev.Width - 8, 11);
        };
        footer.Controls.Add(_pageLabel);
        footer.Controls.Add(_btnPrev);
        footer.Controls.Add(_btnNext);

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(toolbar);
        Controls.Add(hero);
        Controls.Add(top);
    }

    // =========================================================================
    // Status chip
    // =========================================================================
    private void BuildStatusChip()
    {
        _statusChip.Height = 28;
        _statusChip.Width = 480;
        _statusChip.BackColor = AccentTint;
        _statusChip.Padding = new Padding(0);

        _statusChip.Paint += (s, e) =>
        {
            var p = (Panel)s!;
            using var path = RoundedRect(new Rectangle(0, 0, p.Width - 1, p.Height - 1), 14);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(p.BackColor);
            e.Graphics.FillPath(fill, path);
            using var pen = new Pen(Color.FromArgb(208, 226, 250));
            e.Graphics.DrawPath(pen, path);
        };

        _statusDot.Location = new Point(12, 10);
        _statusDot.Width = 8;
        _statusDot.Height = 8;
        _statusDot.BackColor = TextSubtle;
        _statusDot.Paint += (s, e) =>
        {
            var p = (Panel)s!;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(p.BackColor);
            e.Graphics.FillEllipse(b, 0, 0, p.Width - 1, p.Height - 1);
        };

        _statusText.Text = "Connecting…";
        _statusText.Font = FontBodyBold;
        _statusText.ForeColor = Accent;
        _statusText.AutoSize = true;
        _statusText.Location = new Point(26, 7);

        _statusChip.Controls.Add(_statusDot);
        _statusChip.Controls.Add(_statusText);
    }

    // =========================================================================
    // Stat tile (no border, just typography)
    // =========================================================================
    private static Panel BuildStat(Label number, string label, Color numberColor)
    {
        var p = new Panel { BackColor = Bg, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 16, 0) };
        number.Text = "0";
        number.Font = FontStatNum;
        number.ForeColor = numberColor;
        number.AutoSize = true;
        number.Location = new Point(0, 0);
        var lbl = new Label
        {
            Text = label,
            Font = FontLabel,
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(0, 50),
        };
        p.Controls.Add(number);
        p.Controls.Add(lbl);
        return p;
    }

    // =========================================================================
    // Grid card
    // =========================================================================
    private void BuildGridCard(Control host)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        card.Paint += (s, e) => DrawCardBorder(e.Graphics, (Panel)s!);

        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Surface;
        _grid.BorderStyle = BorderStyle.None;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.ColumnHeadersHeight = 38;
        _grid.EnableHeadersVisualStyles = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.GridColor = BorderHair;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.RowTemplate.Height = 40;
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            Padding = new Padding(14, 0, 14, 0),
            SelectionBackColor = AccentTint,
            SelectionForeColor = TextPrimary,
            Font = FontBody,
            ForeColor = TextPrimary,
            BackColor = Surface,
        };
        _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            Padding = new Padding(14, 0, 14, 0),
            SelectionBackColor = AccentTint,
            SelectionForeColor = TextPrimary,
            Font = FontBody,
            ForeColor = TextPrimary,
            BackColor = SurfaceAlt,
        };
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = TextMuted,
            Font = FontGridHeader,
            Padding = new Padding(14, 0, 14, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            SelectionBackColor = Surface,
            SelectionForeColor = TextMuted,
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID",          FillWeight = 6 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "User",        FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time",        FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status",      FillWeight = 8 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Verify",      FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sync",        FillWeight = 11 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Received",    FillWeight = 17 });

        // Custom pill rendering for the Sync column
        _grid.CellPainting += GridCellPainting;

        card.Padding = new Padding(1);
        card.Controls.Add(_grid);
        host.Controls.Add(card);
    }

    private void GridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 5 || e.Value == null) return;

        var text = e.Value.ToString() ?? "";
        var isSynced = text == "Synced";
        var fg = isSynced ? SuccessFg : WarnFg;
        var bg = isSynced ? SuccessBg : WarnBg;

        e.PaintBackground(e.CellBounds, true);

        var g = e.Graphics!;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var sz = TextRenderer.MeasureText(text, FontPill);
        var pillW = sz.Width + 18;
        var pillH = 20;
        var pillX = e.CellBounds.Left + 14;
        var pillY = e.CellBounds.Top + (e.CellBounds.Height - pillH) / 2;
        var pillRect = new Rectangle(pillX, pillY, pillW, pillH);

        using (var path = RoundedRect(pillRect, pillH / 2))
        using (var brush = new SolidBrush(bg))
        {
            g.FillPath(brush, path);
        }
        TextRenderer.DrawText(g, text, FontPill, pillRect, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        e.Handled = true;
    }

    // =========================================================================
    // Activity card
    // =========================================================================
    private void BuildActivityCard(Control host)
    {
        _activityCard.Dock = DockStyle.Fill;
        _activityCard.BackColor = Surface;
        _activityCard.Paint += (s, e) => DrawCardBorder(e.Graphics, (Panel)s!);

        var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Surface, Padding = new Padding(18, 14, 18, 0) };
        header.Paint += (_, e) => DrawBottomHairline(e.Graphics, header);
        var headerLabel = new Label
        {
            Text = "Activity",
            Font = FontH2,
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(18, 14),
        };
        var subLabel = new Label
        {
            Text = "Live events as devices punch in",
            Font = FontLabel,
            ForeColor = TextSubtle,
            AutoSize = true,
            Location = new Point(headerLabel.Right + 12, 16),
        };
        header.Controls.Add(headerLabel);
        header.Controls.Add(subLabel);

        _activityList.Dock = DockStyle.Fill;
        _activityList.BackColor = Surface;
        _activityList.AutoScroll = true;
        _activityList.Padding = new Padding(0, 4, 0, 8);

        _activityEmpty.Text = "Waiting for events…";
        _activityEmpty.Font = FontBody;
        _activityEmpty.ForeColor = TextSubtle;
        _activityEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _activityEmpty.Dock = DockStyle.Fill;
        _activityList.Controls.Add(_activityEmpty);

        _activityCard.Controls.Add(_activityList);
        _activityCard.Controls.Add(header);
        host.Controls.Add(_activityCard);
    }

    // =========================================================================
    // Buttons
    // =========================================================================
    private static void StyleGhostButton(Button b, string text, int width)
    {
        b.Text = text;
        b.Width = width;
        b.Height = 36;
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Surface;
        b.ForeColor = TextPrimary;
        b.Font = FontBody;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.BorderColor = BorderSoft;
        b.FlatAppearance.MouseOverBackColor = SurfaceHover;
        b.UseVisualStyleBackColor = false;
    }

    private static void StylePrimaryButton(Button b, string text, int width)
    {
        b.Text = text;
        b.Width = width;
        b.Height = 36;
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.Font = FontBodyBold;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.UseVisualStyleBackColor = false;
    }

    // =========================================================================
    // Drawing helpers
    // =========================================================================
    private static void DrawCardBorder(Graphics g, Panel p)
    {
        using var pen = new Pen(BorderHair);
        g.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
    }

    private static void DrawBottomHairline(Graphics g, Panel p)
    {
        using var pen = new Pen(BorderHair);
        g.DrawLine(pen, 0, p.Height - 1, p.Width, p.Height - 1);
    }

    private static void DrawTopHairline(Graphics g, Panel p)
    {
        using var pen = new Pen(BorderHair);
        g.DrawLine(pen, 0, 0, p.Width, 0);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // =========================================================================
    // Filter tabs
    // =========================================================================
    private void SetFilter(string value, bool initial = false)
    {
        _filterValue = value;
        _tabAll.IsActive      = value == "all";
        _tabSynced.IsActive   = value == "synced";
        _tabUnsynced.IsActive = value == "unsynced";
        if (!initial)
        {
            _page = 1;
            _ = RefreshSoftAsync();
        }
    }

    // =========================================================================
    // Data + render
    // =========================================================================
    private async Task BootstrapAsync()
    {
        var health = await _svc.GetHealthAsync();
        if (health != null) _svc.SetPort(health.Port);
        await RefreshSoftAsync();
    }

    private async Task RefreshSoftAsync()
    {
        try
        {
            var health = await _svc.GetHealthAsync();
            if (health == null)
            {
                _statusDot.BackColor = DangerFg;
                _statusText.ForeColor = DangerFg;
                _statusText.Text = "Service offline — will retry";
                _statusChip.BackColor = DangerBg;
                _statusChip.Invalidate();
                _statTotal.Text = _statSynced.Text = _statUnsynced.Text = "—";
                return;
            }
            _statusDot.BackColor = SuccessFg;
            _statusText.ForeColor = SuccessFg;
            _statusText.Text = $"Listening on port {health.Port}   ·   HRMIS  {ShortenUrl(health.HrmisUrl)}";
            _statusChip.BackColor = SuccessBg;
            _statusChip.Invalidate();

            var stats = await _svc.GetStatsAsync();
            _statTotal.Text    = (stats?.Total ?? 0).ToString("N0");
            _statSynced.Text   = (stats?.Synced ?? 0).ToString("N0");
            _statUnsynced.Text = (stats?.Unsynced ?? 0).ToString("N0");

            var page = await _svc.GetAttendanceAsync(_page, Limit, _filterValue);
            _rows = page.Data;
            _totalPages = page.TotalPages;
            RenderGrid();
            _pageLabel.Text = $"Page {page.PageNumber} of {Math.Max(1, page.TotalPages)}   ·   {page.Total:N0} records";
            _btnPrev.Enabled = _page > 1;
            _btnNext.Enabled = _page < _totalPages;
        }
        catch { /* swallow — keep polling */ }
    }

    private static string ShortenUrl(string u)
    {
        try
        {
            var uri = new Uri(u);
            return uri.Host;
        }
        catch { return u; }
    }

    private void RenderGrid()
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        var filtered = _rows.Where(r => string.IsNullOrEmpty(_searchValue)
                                     || (r.UserId?.Contains(_searchValue, StringComparison.OrdinalIgnoreCase) ?? false)
                                     || (r.Status?.Contains(_searchValue, StringComparison.OrdinalIgnoreCase) ?? false)
                                     || (r.VerifyType?.Contains(_searchValue, StringComparison.OrdinalIgnoreCase) ?? false));
        foreach (var r in filtered)
        {
            _grid.Rows.Add(
                r.Id,
                r.UserId,
                FormatDt(r.DateTime),
                r.Status,
                r.VerifyType,
                r.IsSynced ? "Synced" : "Pending",
                r.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        }
        _grid.ResumeLayout();
    }

    private static string FormatDt(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (DateTime.TryParse(s, out var dt)) return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return s;
    }

    // =========================================================================
    // Activity log + SSE
    // =========================================================================
    private async Task StartSseLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _svc.SubscribeEventsAsync(OnSseEvent, ct); }
            catch (OperationCanceledException) { return; }
            catch { /* network blip — reconnect */ }
            try { await Task.Delay(2000, ct); } catch { return; }
        }
    }

    private void OnSseEvent(string evt, string jsonData)
    {
        if (IsDisposed) return;
        try
        {
            if (evt == "newRecord")
            {
                using var doc = JsonDocument.Parse(jsonData);
                var r = doc.RootElement;
                var u = r.TryGetProperty("userId",   out var ue) ? ue.GetString() ?? "?" : "?";
                var s = r.TryGetProperty("status",   out var se) ? se.GetString() ?? "?" : "?";
                BeginInvoke((Action)(() => AddActivity(ActivityKind.Received,
                    $"Punch from user {u}",
                    "status " + s)));
                BeginInvoke((Action)(async () => await RefreshSoftAsync()));
            }
            else if (evt == "syncUpdate")
            {
                using var doc = JsonDocument.Parse(jsonData);
                var r = doc.RootElement;
                var id = r.TryGetProperty("id", out var i) && i.TryGetInt64(out var il) ? il : 0L;
                var ok = r.TryGetProperty("isSynced", out var b) && b.GetBoolean();
                BeginInvoke((Action)(() => AddActivity(ok ? ActivityKind.Synced : ActivityKind.Failed,
                    ok ? $"Synced record #{id}" : $"Failed to sync record #{id}",
                    ok ? "HRMIS accepted" : "will retry")));
                BeginInvoke((Action)(async () => await RefreshSoftAsync()));
            }
        }
        catch { /* malformed payload — ignore */ }
    }

    private enum ActivityKind { Received, Synced, Failed, Info }

    private sealed class ActivityItem
    {
        public DateTime Ts;
        public ActivityKind Kind;
        public string Title = "";
        public string Detail = "";
    }

    private void AddActivity(ActivityKind kind, string title, string detail)
    {
        if (_activity.Count == 0 && _activityList.Controls.Contains(_activityEmpty))
            _activityList.Controls.Remove(_activityEmpty);

        var item = new ActivityItem { Ts = DateTime.Now, Kind = kind, Title = title, Detail = detail };
        _activity.Insert(0, item);
        if (_activity.Count > MaxActivity) _activity.RemoveAt(_activity.Count - 1);

        var row = BuildActivityRow(item);
        _activityList.Controls.Add(row);
        _activityList.Controls.SetChildIndex(row, 0);

        // re-layout: stack from top
        int y = 0;
        for (int i = 0; i < _activityList.Controls.Count; i++)
        {
            var c = _activityList.Controls[i];
            c.Top = y;
            c.Width = _activityList.ClientSize.Width;
            y += c.Height;
        }

        while (_activityList.Controls.Count > MaxActivity)
            _activityList.Controls.RemoveAt(_activityList.Controls.Count - 1);
    }

    private Control BuildActivityRow(ActivityItem item)
    {
        var row = new Panel
        {
            Height = 54,
            Width = _activityList.ClientSize.Width,
            BackColor = Surface,
            Padding = new Padding(0),
            Cursor = Cursors.Default,
        };
        row.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderHair);
            e.Graphics.DrawLine(pen, 16, row.Height - 1, row.Width - 16, row.Height - 1);
        };
        row.MouseEnter += (_, _) => row.BackColor = SurfaceHover;
        row.MouseLeave += (_, _) => row.BackColor = Surface;

        var (dotColor, kindLabel) = item.Kind switch
        {
            ActivityKind.Received => (Accent,    "received"),
            ActivityKind.Synced   => (SuccessFg, "synced"),
            ActivityKind.Failed   => (DangerFg,  "failed"),
            _                     => (TextSubtle,"info"),
        };

        var dot = new Panel
        {
            Location = new Point(20, 22),
            Width = 8,
            Height = 8,
            BackColor = dotColor,
        };
        dot.Paint += (s, e) =>
        {
            var p = (Panel)s!;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(p.BackColor);
            e.Graphics.FillEllipse(b, 0, 0, p.Width - 1, p.Height - 1);
        };
        dot.MouseEnter += (_, _) => row.BackColor = SurfaceHover;

        var titleLbl = new Label
        {
            Text = item.Title,
            Font = FontBody,
            ForeColor = TextPrimary,
            Location = new Point(40, 10),
            AutoSize = false,
            Height = 18,
            Width = row.Width - 140,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            AutoEllipsis = true,
        };
        var detailLbl = new Label
        {
            Text = item.Detail.Length == 0 ? kindLabel : $"{kindLabel}  ·  {item.Detail}",
            Font = FontLabel,
            ForeColor = TextMuted,
            Location = new Point(40, 28),
            AutoSize = false,
            Height = 16,
            Width = row.Width - 140,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            AutoEllipsis = true,
        };
        var timeLbl = new Label
        {
            Text = item.Ts.ToString("HH:mm:ss"),
            Font = FontMono,
            ForeColor = TextSubtle,
            Width = 80,
            Height = 16,
            Location = new Point(row.Width - 100, 19),
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };

        // forward hover to row
        foreach (var c in new Control[] { titleLbl, detailLbl, timeLbl })
        {
            c.MouseEnter += (_, _) => row.BackColor = SurfaceHover;
            c.MouseLeave += (_, _) => row.BackColor = Surface;
        }

        row.Controls.Add(dot);
        row.Controls.Add(titleLbl);
        row.Controls.Add(detailLbl);
        row.Controls.Add(timeLbl);
        return row;
    }

    // =========================================================================
    // Actions
    // =========================================================================
    private async Task OnSyncAllAsync()
    {
        _btnSyncAll.Enabled = false;
        try
        {
            var ok = await _svc.SyncAllAsync();
            AddActivity(ActivityKind.Info, ok ? "Sync all triggered" : "Sync all failed",
                ok ? "queued unsynced records" : "service not reachable");
            await RefreshSoftAsync();
        }
        finally { _btnSyncAll.Enabled = true; }
    }

    private async Task OpenSettingsAsync()
    {
        using var form = new SettingsForm(_svc);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            AddActivity(ActivityKind.Info, "Settings saved", "applied");
            await BootstrapAsync();
        }
    }

    // =========================================================================
    // TabButton — segmented filter
    // =========================================================================
    private sealed class TabButton : Control
    {
        private bool _active;
        public bool IsActive
        {
            get => _active;
            set { _active = value; Invalidate(); }
        }

        public TabButton(string text)
        {
            Text = text;
            Font = FontBodyBold;
            Cursor = Cursors.Hand;
            BackColor = Bg;
            Height = 32;
            DoubleBuffered = true;
            using var g = CreateGraphics();
            var sz = TextRenderer.MeasureText(g, text, Font);
            Width = sz.Width + 28;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 2, Width - 1, Height - 4);
            using var path = RoundedRect(r, 6);

            if (_active)
            {
                using var fill = new SolidBrush(Surface);
                e.Graphics.FillPath(fill, path);
                using var pen = new Pen(BorderSoft);
                e.Graphics.DrawPath(pen, path);
                TextRenderer.DrawText(e.Graphics, Text, Font, r, TextPrimary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, Text, Font, r, TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!_active)
            {
                BackColor = SurfaceHover;
                Invalidate();
            }
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_active)
            {
                BackColor = Bg;
                Invalidate();
            }
        }
    }

    // =========================================================================
    // SearchBox with placeholder + magnifier
    // =========================================================================
    private sealed class SearchBox : Control
    {
        private readonly TextBox _box = new();
        public string Placeholder
        {
            get => _box.PlaceholderText;
            set => _box.PlaceholderText = value;
        }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string Text
        {
            get => _box.Text;
            set => _box.Text = value ?? "";
        }
        public event EventHandler? TextChangedInner;

        public SearchBox()
        {
            Height = 36;
            BackColor = Surface;
            DoubleBuffered = true;
            _box.BorderStyle = BorderStyle.None;
            _box.Font = FontBody;
            _box.ForeColor = TextPrimary;
            _box.BackColor = Surface;
            _box.Location = new Point(36, 9);
            _box.TextChanged += (s, e) => TextChangedInner?.Invoke(this, EventArgs.Empty);
            _box.TextChanged += (s, e) => OnTextChanged(EventArgs.Empty);
            Controls.Add(_box);
            Resize += (_, _) => _box.Width = Width - 48;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(r, 6);
            using var fill = new SolidBrush(Surface);
            e.Graphics.FillPath(fill, path);
            using var pen = new Pen(BorderSoft);
            e.Graphics.DrawPath(pen, path);

            // magnifier glyph
            TextRenderer.DrawText(e.Graphics, "🔍", new Font("Segoe UI Emoji", 9F),
                new Rectangle(10, 8, 24, 20), TextSubtle, TextFormatFlags.Left);
        }
    }
}
