using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GwtLauncher;

namespace GwtInstaller;

public sealed class InstallerForm : Form
{
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private readonly Image? _heroBanner;
    private readonly TextBox _dirBox;
    private readonly TextBox _nickBox;
    private readonly CheckBox _desktopChk;
    private readonly CheckBox _startMenuChk;
    private readonly CheckBox _launchChk;
    private readonly Button _browseBtn;
    private readonly Button _installBtn;
    private readonly Button _cancelBtn;
    private readonly Label _statusLabel;
    private readonly Panel _progressTrack;
    private int _progressPct;
    private bool _installing;

    public InstallerForm()
    {
        Text = "Ground War Tanks — Установка";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 452);
        BackColor = SpaceTheme.BgWindow;
        ForeColor = SpaceTheme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;

        _heroBanner = LoadEmbeddedImage("GwtInstaller.Payload.hero_banner.jpg");
        TryApplyEmbeddedIcon();

        var header = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(640, 128),
            BackColor = SpaceTheme.BgCard,
        };
        header.Paint += PaintHeader;
        EnableDrag(header);

        var closeBtn = new Button
        {
            Location = new Point(598, 10),
            Size = new Size(32, 28),
            Text = "✕",
            FlatStyle = FlatStyle.Flat,
            ForeColor = SpaceTheme.TextSecondary,
            BackColor = Color.FromArgb(26, 30, 42),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.Click += (_, _) => { if (!_installing) Close(); };

        var minBtn = new Button
        {
            Location = new Point(562, 10),
            Size = new Size(32, 28),
            Text = "—",
            FlatStyle = FlatStyle.Flat,
            ForeColor = SpaceTheme.TextSecondary,
            BackColor = Color.FromArgb(26, 30, 42),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        minBtn.FlatAppearance.BorderSize = 0;
        minBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;

        header.Controls.Add(minBtn);
        header.Controls.Add(closeBtn);
        Controls.Add(header);

        int y = 146;
        Controls.Add(MakeSectionLabel("ПАПКА УСТАНОВКИ (ЛАУНЧЕР И ФАЙЛЫ ИГРЫ)", 28, y));
        y += 20;

        _dirBox = MakeDarkInput(InstallOptions.DefaultInstallDir, 28, y, 476);
        _browseBtn = new Button
        {
            Location = new Point(512, y - 1),
            Size = new Size(100, 29),
            Text = "ОБЗОР...",
            FlatStyle = FlatStyle.Flat,
            BackColor = SpaceTheme.BgCard,
            ForeColor = SpaceTheme.TextPrimary,
            Font = new Font("Segoe UI", 8.3f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _browseBtn.FlatAppearance.BorderColor = SpaceTheme.BorderBright;
        _browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Выберите папку для установки Ground War Tanks",
                SelectedPath = _dirBox.Text,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _dirBox.Text = dlg.SelectedPath;
        };
        Controls.Add(_dirBox);
        Controls.Add(_browseBtn);

        y += 42;
        Controls.Add(MakeSectionLabel("ИГРОВОЙ НИКНЕЙМ (НЕОБЯЗАТЕЛЬНО, МОЖНО ПОМЕНЯТЬ В ЛАУНЧЕРЕ)", 28, y));
        y += 20;
        _nickBox = MakeDarkInput(string.Empty, 28, y, 584);
        _nickBox.PlaceholderText = "Например: TankistPro (если оставить пустым — выдастся автоматически)";
        Controls.Add(_nickBox);

        y += 40;
        _desktopChk = MakeCheck("Создать ярлык на Рабочем столе", 28, y, true);
        _startMenuChk = MakeCheck("Создать ярлык в меню «Пуск»", 320, y, true);
        y += 28;
        _launchChk = MakeCheck("Запустить лаунчер сразу после установки (для загрузки игры)", 28, y, true);
        Controls.Add(_desktopChk);
        Controls.Add(_startMenuChk);
        Controls.Add(_launchChk);

        y += 40;
        _statusLabel = new Label
        {
            Location = new Point(28, y),
            Size = new Size(584, 18),
            Text = "Готово к установке клиентского лаунчера (без админ-функций).",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = SpaceTheme.TextSecondary,
        };
        Controls.Add(_statusLabel);

        y += 22;
        _progressTrack = new Panel
        {
            Location = new Point(28, y),
            Size = new Size(584, 10),
            BackColor = SpaceTheme.BgInput,
        };
        _progressTrack.Paint += (_, e) =>
        {
            using var pen = new Pen(SpaceTheme.BorderSubtle);
            e.Graphics.DrawRectangle(pen, 0, 0, _progressTrack.Width - 1, _progressTrack.Height - 1);
            if (_progressPct > 0)
            {
                int w = Math.Max(4, (int)Math.Round((_progressTrack.Width - 2) * (_progressPct / 100.0)));
                using var br = new LinearGradientBrush(
                    new Rectangle(1, 1, w, _progressTrack.Height - 2),
                    SpaceTheme.AccentRedBright, SpaceTheme.AccentRedDark, LinearGradientMode.Horizontal);
                e.Graphics.FillRectangle(br, 1, 1, w, _progressTrack.Height - 2);
            }
        };
        Controls.Add(_progressTrack);

        _cancelBtn = new Button
        {
            Location = new Point(28, 392),
            Size = new Size(136, 38),
            Text = "ОТМЕНА",
            FlatStyle = FlatStyle.Flat,
            BackColor = SpaceTheme.BgCard,
            ForeColor = SpaceTheme.TextSecondary,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _cancelBtn.FlatAppearance.BorderColor = SpaceTheme.BorderBright;
        _cancelBtn.Click += (_, _) => Close();

        _installBtn = new Button
        {
            Location = new Point(416, 392),
            Size = new Size(196, 38),
            Text = "УСТАНОВИТЬ",
            FlatStyle = FlatStyle.Flat,
            BackColor = SpaceTheme.AccentRedBright,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _installBtn.FlatAppearance.BorderSize = 0;
        _installBtn.Click += async (_, _) => await StartInstallAsync();

        Controls.Add(_cancelBtn);
        Controls.Add(_installBtn);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SpaceTheme.EnableWindowRoundedCorners(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(SpaceTheme.BorderBright, 1f);
        e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        if (_heroBanner != null)
        {
            g.DrawImage(_heroBanner, new Rectangle(0, -18, 640, 220));
        }

        using var overlay = new LinearGradientBrush(
            new Rectangle(0, 0, 640, 128),
            Color.FromArgb(145, 11, 13, 18),
            Color.FromArgb(250, 11, 13, 18),
            LinearGradientMode.Vertical);
        g.FillRectangle(overlay, 0, 0, 640, 128);

        using var titleFont = new Font("Segoe UI", 18f, FontStyle.Bold);
        using var subFont = new Font("Segoe UI", 9f);
        var sz1 = TextRenderer.MeasureText(g, "GROUND WAR ", titleFont, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "GROUND WAR ", titleFont, new Point(28, 36), SpaceTheme.TextPrimary, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "TANKS", titleFont, new Point(28 + sz1.Width, 36), SpaceTheme.AccentRedBright, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "УСТАНОВЩИК ИГРОВОГО ЛАУНЧЕРА • v1.4.1", subFont, new Point(29, 72), SpaceTheme.TextSecondary, TextFormatFlags.NoPadding);

        using var border = new Pen(SpaceTheme.BorderSubtle);
        g.DrawLine(border, 0, 127, 640, 127);
    }

    private async System.Threading.Tasks.Task StartInstallAsync()
    {
        if (_installing) return;
        string dir = _dirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            MessageBox.Show(this, "Выберите папку для установки.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _installing = true;
        _installBtn.Enabled = false;
        _cancelBtn.Enabled = false;
        _browseBtn.Enabled = false;
        _dirBox.Enabled = false;
        _nickBox.Enabled = false;

        try
        {
            var opts = new InstallOptions
            {
                InstallDir = dir,
                Nickname = _nickBox.Text.Trim(),
                CreateDesktopShortcut = _desktopChk.Checked,
                CreateStartMenuShortcut = _startMenuChk.Checked,
                LaunchAfterInstall = _launchChk.Checked,
            };

            var progress = new Progress<(int pct, string status)>(p =>
            {
                _progressPct = p.pct;
                _statusLabel.Text = p.status;
                _statusLabel.ForeColor = p.pct >= 100 ? SpaceTheme.AccentGreen : SpaceTheme.TextSecondary;
                _progressTrack.Invalidate();
            });

            string installedExe = await InstallerCore.RunInstallAsync(opts, progress);

            if (opts.LaunchAfterInstall && File.Exists(installedExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installedExe,
                    WorkingDirectory = Path.GetDirectoryName(installedExe)!,
                    UseShellExecute = true,
                });
                Close();
            }
            else
            {
                MessageBox.Show(this,
                    $"Лаунчер успешно установлен в:\n{opts.InstallDir}\n\nОткройте ярлык Ground War Tanks на Рабочем столе.",
                    "Установка завершена", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Ошибка установки: " + ex.Message;
            _statusLabel.ForeColor = SpaceTheme.AccentRedBright;
            MessageBox.Show(this, $"Не удалось выполнить установку:\n{ex.Message}", "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _installing = false;
            _installBtn.Enabled = true;
            _cancelBtn.Enabled = true;
            _browseBtn.Enabled = true;
            _dirBox.Enabled = true;
            _nickBox.Enabled = true;
        }
    }

    private void EnableDrag(Control c)
    {
        c.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WmNcLButtonDown, HtCaption, 0);
            }
        };
    }

    private void TryApplyEmbeddedIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("GwtInstaller.Payload.GameIcon.ico");
            if (s != null) Icon = new Icon(s);
        }
        catch { }
    }

    private static Image? LoadEmbeddedImage(string resourceName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream(resourceName);
            return s != null ? Image.FromStream(s) : null;
        }
        catch { return null; }
    }

    private static Label MakeSectionLabel(string text, int x, int y) => new()
    {
        Location = new Point(x, y),
        Size = new Size(584, 16),
        Text = text,
        Font = new Font("Segoe UI", 7.8f, FontStyle.Bold),
        ForeColor = SpaceTheme.AccentRedBright,
    };

    private static TextBox MakeDarkInput(string text, int x, int y, int width) => new()
    {
        Location = new Point(x, y),
        Size = new Size(width, 26),
        Text = text,
        BackColor = SpaceTheme.BgInput,
        ForeColor = SpaceTheme.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 9.2f),
    };

    private static CheckBox MakeCheck(string text, int x, int y, bool check) => new()
    {
        Location = new Point(x, y),
        AutoSize = true,
        Text = text,
        Checked = check,
        ForeColor = SpaceTheme.TextSecondary,
        Font = new Font("Segoe UI", 8.8f),
    };
}
