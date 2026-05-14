using System;
using System.Collections.Generic;
using System.Drawing;
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
    // --- Palette: elegant minimalist ---------------------------------------
    private static readonly Color Bg          = Color.FromArgb(250, 250, 251);
    private static readonly Color Surface     = Color.White;
    private static readonly Color Border      = Color.FromArgb(229, 231, 235);
    private static readonly Color TextPrimary = Color.FromArgb(17, 24, 39);
    private static readonly Color TextMuted   = Color.FromArgb(107, 114, 128);
    private static readonly Color Accent      = Color.FromArgb(59, 130, 246);
    private static readonly Color StatusGood  = Color.FromArgb(5, 150, 105);
    private static readonly Color StatusWarn  = Color.FromArgb(217, 119, 6);
    private static readonly Color StatusBad   = Color.FromArgb(220, 38, 38);

    private static readonly Font HeaderFont = new("Segoe UI", 18F, FontStyle.Regular);
    private static readonly Font SmallFont  = new("Segoe UI", 8.5F);
    private static readonly Font BodyFont   = new("Segoe UI", 9F);
    private static readonly Font BodyBold   = new("Segoe UI Semibold", 9F);
    private static readonly Font StatNumber = new("Segoe UI", 22F, FontStyle.Regular);
    private static readonly Font ActivityMono = new("Cascadia Mono", 8.5F, FontStyle.Regular,
        GraphicsUnit.Point, gdiCharSet: 0);

    private readonly ServiceClient _svc = new();
    private CancellationTokenSource _sseCts = new();

    // Top bar
    private Label _appTitle = new();
    private Label _statusLine = new();
    private Button _btnSettings = new();

    // Stats
    private Label _statTotal = new();
    private Label _statSynced = new();
    private Label _statUnsynced = new();

    // Filter
    private ComboBox _filter = new();
    private TextBox _search = new();

    // Grid
    private DataGridView _grid = new();

    // Activity
    private RichTextBox _activity = new();

    // Bottom
    private Label _pageLabel = new();
    private Button _btnPrev = new();
    private Button _btnNext = new();
    private Button _btnSyncAll = new();

    private int _page = 1;
    private const int Limit = 15;
    private int _totalPages = 0;
    private string _filterValue = "all";
    private string _searchValue = "";
    private List<ParsedAttendanceVm> _rows = new();

    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 4000 };

    public MainForm()
    {
        Text = "PGL Attendance";
        Width = 1320;
        Height = 800;
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = BodyFont;
        BackColor = Bg;
        ForeColor = TextPrimary;
        Icon = LoadAppIcon();
        DoubleBuffered = true;

        BuildLayout();

        _poll.Tick += async (_, _) => await RefreshSoftAsync();
        Load += async (_, _) =>
        {
            await BootstrapAsync();
            _poll.Start();
            _ = Task.Run(() => StartSseLoopAsync(_sseCts.Token));
        };
        FormClosed += (_, _) =>
        {
            _poll.Stop();
            try { _sseCts.Cancel(); } catch { }
        };
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { }
        return SystemIcons.Application;
    }

    // -----------------------------------------------------------------------
    // Layout
    // -----------------------------------------------------------------------
    private void BuildLayout()
    {
        // --- Top bar -------------------------------------------------------
        var top = new Panel
        {
            Dock = DockStyle.Top,
            Height = 88,
            BackColor = Surface,
            Padding = new Padding(28, 18, 28, 12),
        };
        top.Paint += (s, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, ((Panel)s!).Height - 1, ((Panel)s).Width, ((Panel)s).Height - 1);
        };

        _appTitle = new Label
        {
            Text = "PGL Attendance",
            Location = new Point(28, 18),
            AutoSize = true,
            Font = HeaderFont,
            ForeColor = TextPrimary,
        };
        _statusLine = new Label
        {
            Text = "● Connecting…",
            Location = new Point(28, 52),
            AutoSize = true,
            Font = SmallFont,
            ForeColor = TextMuted,
        };

        _btnSettings = MakeGhostButton("Settings", 110);
        _btnSettings.Click += async (_, _) => await OpenSettingsAsync();
        top.Resize += (_, _) => _btnSettings.Location = new Point(top.Width - _btnSettings.Width - 28, 26);

        top.Controls.Add(_appTitle);
        top.Controls.Add(_statusLine);
        top.Controls.Add(_btnSettings);

        // --- Stats row -----------------------------------------------------
        var stats = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 110,
            ColumnCount = 3,
            Padding = new Padding(20, 16, 20, 4),
            BackColor = Bg,
        };
        for (int i = 0; i < 3; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
        stats.Controls.Add(MakeStatCard("Total records", _statTotal, TextPrimary), 0, 0);
        stats.Controls.Add(MakeStatCard("Synced",        _statSynced, StatusGood), 1, 0);
        stats.Controls.Add(MakeStatCard("Pending",       _statUnsynced, StatusWarn), 2, 0);

        // --- Filter + search row ------------------------------------------
        var filterRow = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Bg, Padding = new Padding(28, 6, 28, 12) };
        _filter = new ComboBox
        {
            Location = new Point(28, 14),
            Width = 180,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = BodyFont,
            BackColor = Surface,
        };
        _filter.Items.AddRange(new object[] { "All records", "Synced only", "Unsynced only" });
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += async (_, _) =>
        {
            _filterValue = _filter.SelectedIndex switch { 1 => "synced", 2 => "unsynced", _ => "all" };
            _page = 1;
            await RefreshSoftAsync();
        };

        _search = new TextBox
        {
            Location = new Point(220, 14),
            Width = 280,
            PlaceholderText = "Search user ID, status, verify type…",
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Surface,
            Font = BodyFont,
        };
        _search.TextChanged += (_, _) => { _searchValue = _search.Text.Trim(); RenderGrid(); };

        filterRow.Controls.Add(_filter);
        filterRow.Controls.Add(_search);

        // --- Split: Grid (left) + Activity (right) ------------------------
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            SplitterDistance = 880,
            SplitterWidth = 1,
            BackColor = Border,
            Panel1 = { BackColor = Bg, Padding = new Padding(28, 0, 14, 12) },
            Panel2 = { BackColor = Bg, Padding = new Padding(14, 0, 28, 12) },
            Orientation = Orientation.Vertical,
            IsSplitterFixed = false,
        };
        BuildGrid(split.Panel1);
        BuildActivity(split.Panel2);

        // --- Bottom bar (pagination + Sync All) ---------------------------
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Surface, Padding = new Padding(28, 14, 28, 14) };
        bottom.Paint += (s, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, 0, ((Panel)s!).Width, 0);
        };
        _btnPrev = MakeGhostButton("← Previous", 110);
        _btnNext = MakeGhostButton("Next →", 100);
        _pageLabel = new Label { Text = "—", AutoSize = true, ForeColor = TextMuted, Font = SmallFont };
        _btnSyncAll = MakePrimaryButton("Sync All Unsynced", 170);
        _btnSyncAll.Click += async (_, _) => await OnSyncAllAsync();
        _btnPrev.Click += async (_, _) => { if (_page > 1) { _page--; await RefreshSoftAsync(); } };
        _btnNext.Click += async (_, _) => { if (_page < _totalPages) { _page++; await RefreshSoftAsync(); } };

        bottom.Controls.Add(_btnPrev);
        bottom.Controls.Add(_btnNext);
        bottom.Controls.Add(_pageLabel);
        bottom.Controls.Add(_btnSyncAll);
        bottom.Resize += (_, _) =>
        {
            _btnPrev.Location  = new Point(28, 12);
            _btnNext.Location  = new Point(142, 12);
            _pageLabel.Location = new Point(254, 18);
            _btnSyncAll.Location = new Point(bottom.Width - _btnSyncAll.Width - 28, 12);
        };

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(filterRow);
        Controls.Add(stats);
        Controls.Add(top);
    }

    private void BuildGrid(SplitterPanel host)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        card.Paint += (s, e) => DrawBorder(s!, e);
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Surface,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ColumnHeadersHeight = 38,
            EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            GridColor = Border,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            DefaultCellStyle =
            {
                Padding = new Padding(12, 0, 12, 0),
                SelectionBackColor = Color.FromArgb(239, 246, 255),
                SelectionForeColor = TextPrimary,
                Font = BodyFont,
                ForeColor = TextPrimary,
                BackColor = Surface,
            },
            RowTemplate = { Height = 36 },
        };
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Padding = new Padding(12, 0, 12, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID",          FillWeight = 6 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "USER ID",     FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "DATETIME",    FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "STATUS",      FillWeight = 8 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "VERIFY TYPE", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SYNC",        FillWeight = 9 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "CREATED",     FillWeight = 18 });

        card.Padding = new Padding(1);
        card.Controls.Add(_grid);
        host.Controls.Add(card);
    }

    private void BuildActivity(SplitterPanel host)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        card.Paint += (s, e) => DrawBorder(s!, e);

        var header = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Surface, Padding = new Padding(16, 12, 16, 8) };
        header.Paint += (s, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, ((Panel)s!).Height - 1, ((Panel)s).Width, ((Panel)s).Height - 1);
        };
        var lbl = new Label
        {
            Text = "ACTIVITY",
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = TextMuted,
            Location = new Point(16, 12),
        };
        header.Controls.Add(lbl);

        _activity = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            BackColor = Surface,
            ForeColor = TextPrimary,
            Font = ActivityMono,
            Multiline = true,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            HideSelection = true,
        };
        _activity.MouseEnter += (_, _) => _activity.Cursor = Cursors.Default;

        card.Controls.Add(_activity);
        card.Controls.Add(header);
        host.Controls.Add(card);
        AppendActivity("info", "Waiting for events…");
    }

    private static void DrawBorder(object sender, PaintEventArgs e)
    {
        var p = (Panel)sender;
        using var pen = new Pen(Border);
        e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
    }

    private static Panel MakeStatCard(string label, Label value, Color valueColor)
    {
        var panel = new Panel
        {
            BackColor = Surface,
            Margin = new Padding(8, 0, 8, 0),
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 14, 18, 14),
        };
        panel.Paint += (s, e) =>
        {
            var p = (Panel)s!;
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        var lbl = new Label
        {
            Text = label.ToUpperInvariant(),
            Location = new Point(18, 12),
            AutoSize = true,
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = TextMuted,
        };
        value.Text = "0";
        value.Location = new Point(18, 32);
        value.AutoSize = true;
        value.Font = StatNumber;
        value.ForeColor = valueColor;
        panel.Controls.Add(lbl);
        panel.Controls.Add(value);
        return panel;
    }

    private static Button MakeGhostButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = TextPrimary,
            Font = BodyFont,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 244, 246);
        return b;
    }

    private static Button MakePrimaryButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = BodyBold,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(37, 99, 235);
        return b;
    }

    // -----------------------------------------------------------------------
    // Data + render
    // -----------------------------------------------------------------------
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
                _statusLine.Text = "● Service offline — will retry";
                _statusLine.ForeColor = StatusBad;
                return;
            }
            _statusLine.Text = $"● Listening on port {health.Port}  •  HRMIS: {health.HrmisUrl}";
            _statusLine.ForeColor = StatusGood;

            var stats = await _svc.GetStatsAsync();
            RenderStats(stats);

            var page = await _svc.GetAttendanceAsync(_page, Limit, _filterValue);
            _rows = page.Data;
            _totalPages = page.TotalPages;
            RenderGrid();
            _pageLabel.Text = $"Page {page.PageNumber} of {Math.Max(1, page.TotalPages)}  •  {page.Total} records";
            _btnPrev.Enabled = _page > 1;
            _btnNext.Enabled = _page < _totalPages;
        }
        catch { /* swallow — keep polling */ }
    }

    private void RenderStats(ServiceClient.Stats? s)
    {
        _statTotal.Text    = $"{(s?.Total ?? 0):N0}";
        _statSynced.Text   = $"{(s?.Synced ?? 0):N0}";
        _statUnsynced.Text = $"{(s?.Unsynced ?? 0):N0}";
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
            var idx = _grid.Rows.Add(
                r.Id,
                r.UserId,
                FormatDt(r.DateTime),
                r.Status,
                r.VerifyType,
                r.IsSynced ? "● Synced" : "● Pending",
                r.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            var c = _grid.Rows[idx].Cells[5];
            c.Style.ForeColor = r.IsSynced ? StatusGood : StatusWarn;
            c.Style.Font = BodyBold;
        }
        _grid.ResumeLayout();
    }

    private static string FormatDt(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (DateTime.TryParse(s, out var dt)) return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return s;
    }

    // -----------------------------------------------------------------------
    // Activity log + SSE
    // -----------------------------------------------------------------------
    private async Task StartSseLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _svc.SubscribeEventsAsync(OnSseEvent, ct);
            }
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
                var root = doc.RootElement;
                var userId  = root.TryGetProperty("userId",   out var u) ? u.GetString() ?? "?" : "?";
                var status  = root.TryGetProperty("status",   out var st) ? st.GetString() ?? "?" : "?";
                var datetime= root.TryGetProperty("datetime", out var d) ? d.GetString() ?? "?" : "?";
                BeginInvoke((Action)(() => AppendActivity("recv", $"Received  user {userId}  status {status}  at {datetime}")));
                BeginInvoke((Action)(async () => await RefreshSoftAsync()));
            }
            else if (evt == "syncUpdate")
            {
                using var doc = JsonDocument.Parse(jsonData);
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var i) && i.TryGetInt64(out var il) ? il : 0L;
                var ok = root.TryGetProperty("isSynced", out var s) && s.GetBoolean();
                BeginInvoke((Action)(() => AppendActivity(ok ? "sync" : "fail",
                    ok ? $"Synced    record #{id}" : $"Failed    record #{id}")));
                BeginInvoke((Action)(async () => await RefreshSoftAsync()));
            }
            else if (evt == "statsUpdate")
            {
                BeginInvoke((Action)(async () => RenderStats(await _svc.GetStatsAsync())));
            }
        }
        catch { /* malformed event */ }
    }

    private void AppendActivity(string kind, string message)
    {
        var now = DateTime.Now.ToString("HH:mm:ss");
        var (prefix, color) = kind switch
        {
            "recv" => ("→",  Accent),
            "sync" => ("✓",  StatusGood),
            "fail" => ("✗",  StatusBad),
            "info" => ("·",  TextMuted),
            _      => ("·",  TextMuted),
        };

        // Prepend at top (newest first)
        _activity.SelectionStart = 0;
        _activity.SelectionLength = 0;

        _activity.SelectionColor = TextMuted;
        _activity.SelectionFont = ActivityMono;
        _activity.SelectedText = now + "  ";

        _activity.SelectionColor = color;
        _activity.SelectionFont = new Font(ActivityMono, FontStyle.Bold);
        _activity.SelectedText = prefix + " ";

        _activity.SelectionColor = TextPrimary;
        _activity.SelectionFont = ActivityMono;
        _activity.SelectedText = message + Environment.NewLine;

        // Trim old lines beyond 200
        const int maxLines = 200;
        if (_activity.Lines.Length > maxLines)
        {
            var kept = _activity.Lines.Take(maxLines).ToArray();
            _activity.SuspendLayout();
            _activity.Clear();
            // Re-set from kept lines isn't trivial to preserve coloring; just take the trim hit by clearing colors on overflow
            foreach (var line in kept) _activity.AppendText(line + Environment.NewLine);
            _activity.ResumeLayout();
        }
    }

    // -----------------------------------------------------------------------
    // Actions
    // -----------------------------------------------------------------------
    private async Task OnSyncAllAsync()
    {
        _btnSyncAll.Enabled = false;
        try
        {
            var ok = await _svc.SyncAllAsync();
            if (!ok) MessageBox.Show("Could not start sync.", "Sync");
            AppendActivity("info", "Sync All triggered");
            await RefreshSoftAsync();
        }
        finally { _btnSyncAll.Enabled = true; }
    }

    private async Task OpenSettingsAsync()
    {
        using var form = new SettingsForm(_svc);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            AppendActivity("info", "Settings saved");
            await BootstrapAsync();
        }
    }
}
