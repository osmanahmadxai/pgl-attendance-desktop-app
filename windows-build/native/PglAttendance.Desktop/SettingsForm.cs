using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PglAttendance.Desktop;

public sealed class SettingsForm : Form
{
    // Match MainForm palette
    private static readonly Color Bg          = Color.FromArgb(248, 249, 251);
    private static readonly Color Surface     = Color.White;
    private static readonly Color SurfaceHover= Color.FromArgb(241, 243, 245);
    private static readonly Color BorderHair  = Color.FromArgb(231, 233, 237);
    private static readonly Color BorderSoft  = Color.FromArgb(216, 220, 226);
    private static readonly Color TextPrimary = Color.FromArgb( 31,  35,  40);
    private static readonly Color TextMuted   = Color.FromArgb(101, 109, 118);
    private static readonly Color TextSubtle  = Color.FromArgb(139, 148, 158);
    private static readonly Color Accent      = Color.FromArgb(  9, 105, 218);
    private static readonly Color AccentHover = Color.FromArgb(  8,  87, 184);
    private static readonly Color WarnFg      = Color.FromArgb(154, 103,   0);
    private static readonly Color WarnBg      = Color.FromArgb(255, 248, 197);
    private static readonly Color DangerFg    = Color.FromArgb(207,  34,  46);

    private static readonly Font FontTitle   = new("Segoe UI Semibold", 13.5F);
    private static readonly Font FontH2      = new("Segoe UI Semibold", 9.5F);
    private static readonly Font FontBody    = new("Segoe UI", 9F);
    private static readonly Font FontBodyBold= new("Segoe UI Semibold", 9F);
    private static readonly Font FontLabel   = new("Segoe UI", 8.5F);
    private static readonly Font FontMono    = new("Consolas", 8F);

    private const int FieldWidth = 524;
    private const int LeftMargin = 28;

    private readonly ServiceClient _svc;

    // Sync settings
    private readonly TextBox _hrmis = new();
    private readonly NumericUpDown _port = new();
    private readonly Label _hint = new();

    // Browser access
    private readonly CheckBox _remote = new();
    private readonly NumericUpDown _httpsPort = new();
    private readonly TextBox _allowedIps = new();
    private readonly TextBox _deviceIps = new();
    private readonly Label _certInfo = new();

    // Administrator account
    private readonly TextBox _username = new();
    private readonly TextBox _currentPw = new();
    private readonly TextBox _newPw = new();
    private readonly TextBox _confirmPw = new();
    private readonly Label _accountHint = new();

    private readonly Button _save = new();
    private readonly Button _cancel = new();

    private int _initialPort = 4001;
    private int _initialHttpsPort = 4443;
    private bool _initialRemote;
    private bool _accountConfigured;
    private int _minPasswordLength = 12;

    public SettingsForm(ServiceClient svc)
    {
        _svc = svc;
        Text = "Settings";
        ClientSize = new Size(600, 640);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Bg;
        ForeColor = TextPrimary;
        Font = FontBody;
        DoubleBuffered = true;

        // ---------- Header --------------------------------------------------
        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Surface, Padding = new Padding(28, 16, 28, 16) };
        header.Paint += (_, e) => DrawBottomHairline(e.Graphics, header);
        header.Controls.Add(new Label
        {
            Text = "Settings",
            Font = FontTitle,
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(28, 18),
        });
        header.Controls.Add(new Label
        {
            Text = "Device port, HRMIS sync target, and browser access.",
            Font = FontLabel,
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(28, 46),
        });

        // ---------- Body ----------------------------------------------------
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = new Padding(0, 20, 0, 12),
            AutoScroll = true,
        };

        var y = 20;

        // --- HRMIS -----------------------------------------------------------
        AddLabel(body, "HRMIS API URL", ref y);
        StyleTextField(_hrmis, new Point(LeftMargin, y), FieldWidth);
        body.Controls.Add(_hrmis);
        y += 30;
        AddHint(body, "Records POST to  {url}/iclock/cdata.  Takes effect immediately — no restart.", ref y);

        // --- Device port -----------------------------------------------------
        y += 12;
        AddLabel(body, "Device port", ref y);
        StyleNumeric(_port, new Point(LeftMargin, y), 4001);
        body.Controls.Add(_port);
        y += 30;
        AddHint(body, "The device sends to  http://<this-PC-IP>:<port>/iclock/cdata.", ref y);

        _hint.Location = new Point(LeftMargin, y + 6);
        _hint.AutoSize = false;
        _hint.Width = FieldWidth;
        _hint.Height = 32;
        _hint.Font = FontLabel;
        _hint.ForeColor = WarnFg;
        _hint.BackColor = WarnBg;
        _hint.TextAlign = ContentAlignment.MiddleLeft;
        _hint.Padding = new Padding(12, 0, 12, 0);
        _hint.Visible = false;
        _hint.Paint += (s, e) =>
        {
            var lb = (Label)s!;
            using var path = RoundedRect(new Rectangle(0, 0, lb.Width - 1, lb.Height - 1), 6);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(212, 167, 44));
            e.Graphics.DrawPath(pen, path);
        };
        body.Controls.Add(_hint);
        _port.ValueChanged += (_, _) => UpdateRestartHint();
        y += 44;

        // --- Browser access --------------------------------------------------
        AddSectionRule(body, ref y);
        AddSectionTitle(body, "Browser access", ref y);

        _remote.Text = "Allow access from other devices on the network";
        _remote.Location = new Point(LeftMargin, y);
        _remote.AutoSize = true;
        _remote.Font = FontBody;
        _remote.ForeColor = TextPrimary;
        _remote.CheckedChanged += (_, _) => { UpdateRemoteEnabledState(); UpdateRestartHint(); };
        body.Controls.Add(_remote);
        y += 26;
        AddHint(body, "When off, the app is reachable only on this computer — the port is not even opened.", ref y);

        y += 12;
        AddLabel(body, "Browser port (HTTPS)", ref y);
        StyleNumeric(_httpsPort, new Point(LeftMargin, y), 4443);
        _httpsPort.ValueChanged += (_, _) => UpdateRestartHint();
        body.Controls.Add(_httpsPort);
        y += 30;
        AddHint(body, "Browse to  https://<this-PC-IP>:<port>.  Must differ from the device port.", ref y);

        y += 12;
        AddLabel(body, "Restrict browser access to these IPs", ref y);
        StyleTextField(_allowedIps, new Point(LeftMargin, y), FieldWidth);
        body.Controls.Add(_allowedIps);
        y += 30;
        AddHint(body, "Comma-separated, e.g.  192.168.1.50, 192.168.1.0/24.  Empty allows any address (a password is still required).", ref y);

        y += 12;
        AddLabel(body, "Restrict the attendance device to these IPs", ref y);
        StyleTextField(_deviceIps, new Point(LeftMargin, y), FieldWidth);
        body.Controls.Add(_deviceIps);
        y += 30;
        AddHint(body, "Leave empty unless the device has a fixed address — if its IP changes, a restriction here stops attendance collection.", ref y);

        _certInfo.Location = new Point(LeftMargin, y + 8);
        _certInfo.AutoSize = false;
        _certInfo.Width = FieldWidth;
        _certInfo.Height = 30;
        _certInfo.Font = FontMono;
        _certInfo.ForeColor = TextSubtle;
        body.Controls.Add(_certInfo);
        y += 42;

        // --- Administrator account -------------------------------------------
        AddSectionRule(body, ref y);
        AddSectionTitle(body, "Administrator account", ref y);

        AddLabel(body, "Username", ref y);
        StyleTextField(_username, new Point(LeftMargin, y), 260);
        body.Controls.Add(_username);
        y += 34;

        AddLabel(body, "Current password", ref y);
        StyleTextField(_currentPw, new Point(LeftMargin, y), 260);
        _currentPw.UseSystemPasswordChar = true;
        body.Controls.Add(_currentPw);
        y += 34;

        AddLabel(body, "New password", ref y);
        StyleTextField(_newPw, new Point(LeftMargin, y), 260);
        _newPw.UseSystemPasswordChar = true;
        body.Controls.Add(_newPw);
        y += 34;

        AddLabel(body, "Confirm new password", ref y);
        StyleTextField(_confirmPw, new Point(LeftMargin, y), 260);
        _confirmPw.UseSystemPasswordChar = true;
        body.Controls.Add(_confirmPw);
        y += 32;

        _accountHint.Location = new Point(LeftMargin, y);
        _accountHint.AutoSize = false;
        _accountHint.Width = FieldWidth;
        _accountHint.Height = 46;
        _accountHint.Font = FontLabel;
        _accountHint.ForeColor = TextSubtle;
        body.Controls.Add(_accountHint);
        y += 56;

        // ---------- Footer --------------------------------------------------
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Surface, Padding = new Padding(28, 14, 28, 14) };
        footer.Paint += (_, e) => DrawTopHairline(e.Graphics, footer);

        StyleGhostButton(_cancel, "Cancel", 96);
        StylePrimaryButton(_save, "Save", 96);
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _save.Click += async (_, _) => await SaveAsync();
        AcceptButton = _save;
        CancelButton = _cancel;
        footer.Resize += (_, _) =>
        {
            _save.Location = new Point(footer.Width - _save.Width - 28, 14);
            _cancel.Location = new Point(_save.Left - _cancel.Width - 8, 14);
        };
        footer.Controls.Add(_cancel);
        footer.Controls.Add(_save);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);

        Load += async (_, _) => await LoadCurrentAsync();
    }

    // -------------------------------------------------------------------------
    // Layout helpers — each advances the running y so the form stays readable
    // as fields are added.
    // -------------------------------------------------------------------------
    private static void AddLabel(Control host, string text, ref int y)
    {
        host.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(LeftMargin, y),
            AutoSize = true,
            Font = FontH2,
            ForeColor = TextPrimary,
        });
        y += 24;
    }

    private static void AddHint(Control host, string text, ref int y)
    {
        var label = new Label
        {
            Text = text,
            Location = new Point(LeftMargin, y),
            Width = FieldWidth,
            Height = 30,
            AutoSize = false,
            Font = FontLabel,
            ForeColor = TextSubtle,
        };
        host.Controls.Add(label);
        y += 22;
    }

    private static void AddSectionRule(Control host, ref int y)
    {
        host.Controls.Add(new Panel
        {
            Location = new Point(LeftMargin, y),
            Width = FieldWidth,
            Height = 1,
            BackColor = BorderHair,
        });
        y += 18;
    }

    private static void AddSectionTitle(Control host, string text, ref int y)
    {
        host.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(LeftMargin, y),
            AutoSize = true,
            Font = FontBodyBold,
            ForeColor = TextPrimary,
        });
        y += 26;
    }

    private static void StyleTextField(TextBox box, Point loc, int width)
    {
        box.Location = loc;
        box.Width = width;
        box.Font = new("Segoe UI", 9.5F);
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = Surface;
        box.ForeColor = TextPrimary;
    }

    private static void StyleNumeric(NumericUpDown n, Point loc, int value)
    {
        n.Location = loc;
        n.Width = 140;
        n.Minimum = 1;
        n.Maximum = 65535;
        n.Value = value;
        n.Font = FontBody;
        n.BorderStyle = BorderStyle.FixedSingle;
        n.BackColor = Surface;
    }

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

    // -------------------------------------------------------------------------
    private void UpdateRemoteEnabledState()
    {
        var on = _remote.Checked;
        _httpsPort.Enabled = on;
        _allowedIps.Enabled = on;
    }

    /// <summary>
    /// Anything that changes a listener needs a service restart, so warn about
    /// all three the same way the port field always did.
    /// </summary>
    private void UpdateRestartHint()
    {
        var changed = (int)_port.Value != _initialPort
                      || (int)_httpsPort.Value != _initialHttpsPort
                      || _remote.Checked != _initialRemote;
        _hint.Text = "   Saving will restart the background service to apply the new ports.";
        _hint.Visible = changed;
    }

    private async Task LoadCurrentAsync()
    {
        var cur = await _svc.GetSettingsAsync();
        if (cur is null)
        {
            _accountHint.ForeColor = DangerFg;
            _accountHint.Text = "The background service is not reachable — settings cannot be loaded.";
            return;
        }

        _hrmis.Text = cur.HrmisUrl;
        _port.Value = Math.Clamp(cur.Port, 1, 65535);
        _httpsPort.Value = Math.Clamp(cur.AdminHttpsPort, 1, 65535);
        _remote.Checked = cur.RemoteAccessEnabled;
        _allowedIps.Text = string.Join(", ", cur.AllowedIps);
        _deviceIps.Text = string.Join(", ", cur.DeviceAllowedIps);
        _username.Text = cur.Username;

        _initialPort = cur.Port;
        _initialHttpsPort = cur.AdminHttpsPort;
        _initialRemote = cur.RemoteAccessEnabled;
        _accountConfigured = cur.AccountConfigured;
        _minPasswordLength = cur.MinPasswordLength > 0 ? cur.MinPasswordLength : 12;

        _accountHint.ForeColor = TextSubtle;
        _accountHint.Text = _accountConfigured
            ? $"Leave the password fields blank to keep the current password. New passwords must be at least {_minPasswordLength} characters, and changing one signs out every browser."
            : $"No account is set yet. Choose a username and a password of at least {_minPasswordLength} characters before enabling browser access.";

        if (!string.IsNullOrEmpty(cur.CertificateError))
        {
            _certInfo.ForeColor = DangerFg;
            _certInfo.Text = "Browser access could not start: " + cur.CertificateError;
        }
        else if (cur.RemoteAccessActive && !string.IsNullOrEmpty(cur.CertificateFingerprint))
        {
            _certInfo.ForeColor = TextSubtle;
            _certInfo.Text = (cur.CertificateSelfSigned ? "Self-signed certificate SHA-256:\n" : "Certificate SHA-256:\n")
                             + cur.CertificateFingerprint;
        }
        else
        {
            _certInfo.Text = "";
        }

        UpdateRemoteEnabledState();
        UpdateRestartHint();
    }

    private async Task SaveAsync()
    {
        var url = _hrmis.Text.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var u)
            || (u.Scheme != "http" && u.Scheme != "https"))
        {
            Warn("Please enter a valid http(s) URL.");
            return;
        }

        var newPort = (int)_port.Value;
        var newHttpsPort = (int)_httpsPort.Value;
        if (newPort == newHttpsPort)
        {
            Warn("The device port and the browser port must be different.");
            return;
        }

        var newPw = _newPw.Text;
        var confirmPw = _confirmPw.Text;
        if (newPw.Length > 0 || confirmPw.Length > 0)
        {
            if (newPw != confirmPw)
            {
                Warn("The new passwords do not match.");
                return;
            }
            if (newPw.Length < _minPasswordLength)
            {
                Warn($"The new password must be at least {_minPasswordLength} characters.");
                return;
            }
        }

        if (_remote.Checked && !_accountConfigured && newPw.Length == 0)
        {
            Warn("Set a username and password before allowing access from other devices.");
            return;
        }

        _save.Enabled = false;
        try
        {
            // Credentials first — the service refuses to enable browser access
            // while no account exists, so on first-time setup the order matters.
            if (newPw.Length > 0)
            {
                var cred = await _svc.ChangePasswordAsync(_username.Text.Trim(), _currentPw.Text, newPw);
                if (!cred.Ok)
                {
                    Warn(cred.Error ?? "Could not update the administrator account.");
                    return;
                }
                _accountConfigured = true;
            }

            var result = await _svc.UpdateSettingsAsync(
                url,
                newPort,
                _remote.Checked,
                newHttpsPort,
                SplitList(_allowedIps.Text),
                SplitList(_deviceIps.Text));

            if (!result.Ok)
            {
                Warn(result.Error ?? "Could not save settings. Is the service running?");
                return;
            }

            var restarting = newPort != _initialPort
                             || newHttpsPort != _initialHttpsPort
                             || _remote.Checked != _initialRemote;
            if (restarting)
            {
                MessageBox.Show(this,
                    "Settings saved. The service is restarting to apply the new ports — the dashboard will reconnect automatically.",
                    "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        finally { _save.Enabled = true; }
    }

    private static string[] SplitList(string text)
        => (text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

    private void Warn(string message)
        => MessageBox.Show(this, message, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
