using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PglAttendance.Desktop;

public sealed class SettingsForm : Form
{
    private static readonly Color Bg          = Color.FromArgb(250, 250, 251);
    private static readonly Color Surface     = Color.White;
    private static readonly Color Border      = Color.FromArgb(229, 231, 235);
    private static readonly Color TextPrimary = Color.FromArgb(17, 24, 39);
    private static readonly Color TextMuted   = Color.FromArgb(107, 114, 128);
    private static readonly Color Accent      = Color.FromArgb(59, 130, 246);

    private static readonly Font Title       = new("Segoe UI", 15F, FontStyle.Regular);
    private static readonly Font Body        = new("Segoe UI", 9F);
    private static readonly Font BodyBold    = new("Segoe UI Semibold", 9F);
    private static readonly Font SmallMuted  = new("Segoe UI", 8.5F);

    private readonly ServiceClient _svc;
    private TextBox _hrmis = new();
    private NumericUpDown _port = new();
    private Button _save = new();
    private Button _cancel = new();
    private int _initialPort = 4001;

    public SettingsForm(ServiceClient svc)
    {
        _svc = svc;
        Text = "Settings — PGL Attendance";
        ClientSize = new Size(560, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Bg;
        ForeColor = TextPrimary;
        Font = Body;
        ShowInTaskbar = false;

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Surface, Padding = new Padding(28, 18, 28, 18) };
        header.Paint += (s, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, ((Panel)s!).Height - 1, ((Panel)s).Width, ((Panel)s).Height - 1);
        };
        var titleLbl = new Label { Text = "Settings", Location = new Point(28, 18), AutoSize = true, Font = Title, ForeColor = TextPrimary };
        var subLbl = new Label
        {
            Text = "Configure the device-receive port and HRMIS sync target. Saved to disk.",
            Location = new Point(28, 46),
            AutoSize = true,
            Font = SmallMuted,
            ForeColor = TextMuted,
        };
        header.Controls.Add(titleLbl);
        header.Controls.Add(subLbl);

        // Body
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(28, 24, 28, 12) };

        var lbl1 = new Label { Text = "HRMIS API URL", Location = new Point(28, 28), AutoSize = true, Font = BodyBold };
        _hrmis = new TextBox
        {
            Location = new Point(28, 52),
            Width = 504,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Surface,
            Font = Body,
        };
        var hint1 = new Label
        {
            Text = "Records will be POSTed to {url}/iclock/cdata.  Hot-reloads — no service restart needed.",
            Location = new Point(28, 80),
            AutoSize = true,
            Font = SmallMuted,
            ForeColor = TextMuted,
        };

        var lbl2 = new Label { Text = "Listening port", Location = new Point(28, 118), AutoSize = true, Font = BodyBold };
        _port = new NumericUpDown
        {
            Location = new Point(28, 142),
            Width = 140,
            Minimum = 1,
            Maximum = 65535,
            Value = 4001,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Surface,
            Font = Body,
        };
        var hint2 = new Label
        {
            Text = "Devices POST to http://<this-PC-IP>:<port>/iclock/cdata.  Changing this restarts the service.",
            Location = new Point(28, 170),
            AutoSize = true,
            Font = SmallMuted,
            ForeColor = TextMuted,
        };

        body.Controls.Add(lbl1);
        body.Controls.Add(_hrmis);
        body.Controls.Add(hint1);
        body.Controls.Add(lbl2);
        body.Controls.Add(_port);
        body.Controls.Add(hint2);

        // Footer with buttons
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Surface, Padding = new Padding(28, 14, 28, 14) };
        footer.Paint += (s, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, 0, ((Panel)s!).Width, 0);
        };
        _cancel = MakeGhostButton("Cancel", 96);
        _save = MakePrimaryButton("Save", 96);
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _save.Click += async (_, _) => await SaveAsync();
        AcceptButton = _save;
        CancelButton = _cancel;
        footer.Controls.Add(_cancel);
        footer.Controls.Add(_save);
        footer.Resize += (_, _) =>
        {
            _save.Location = new Point(footer.Width - _save.Width - 28, 14);
            _cancel.Location = new Point(footer.Width - _save.Width - _cancel.Width - 36, 14);
        };

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);

        Load += async (_, _) => await LoadCurrentAsync();
    }

    private static Button MakeGhostButton(string text, int width)
    {
        var b = new Button
        {
            Text = text, Width = width, Height = 36,
            FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = TextPrimary,
            Font = Body, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 244, 246);
        return b;
    }

    private static Button MakePrimaryButton(string text, int width)
    {
        var b = new Button
        {
            Text = text, Width = width, Height = 36,
            FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White,
            Font = BodyBold, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(37, 99, 235);
        return b;
    }

    private async Task LoadCurrentAsync()
    {
        var cur = await _svc.GetSettingsAsync();
        if (cur is null) return;
        _hrmis.Text = cur.HrmisUrl;
        _port.Value = Math.Clamp(cur.Port, 1, 65535);
        _initialPort = cur.Port;
    }

    private async Task SaveAsync()
    {
        var url = _hrmis.Text.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var u)
            || (u.Scheme != "http" && u.Scheme != "https"))
        {
            MessageBox.Show(this, "Please enter a valid http(s) URL.", "Settings",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var newPort = (int)_port.Value;
        _save.Enabled = false;
        try
        {
            var ok = await _svc.UpdateSettingsAsync(url, newPort);
            if (!ok)
            {
                MessageBox.Show(this, "Could not save settings. Is the service running?", "Settings",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (newPort != _initialPort)
            {
                MessageBox.Show(this,
                    $"Port changed to {newPort}. The service is restarting — the dashboard may briefly disconnect.",
                    "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        finally { _save.Enabled = true; }
    }
}
