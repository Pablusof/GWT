using System;
using System.Drawing;
using System.Windows.Forms;

namespace GwtLauncher;

public sealed class SettingsForm : Form
{
    private readonly TextBox _gameDirBox;
    private readonly TextBox _manifestUrlBox;
    private readonly RadioButton _onlineModeRadio;
    private readonly RadioButton _localModeRadio;
    private readonly TextBox _serverHostBox;
    private readonly NumericUpDown _gamePortNum;
    private readonly NumericUpDown _authPortNum;
    private readonly NumericUpDown _assetPortNum;
    private readonly TextBox _accountIdBox;

    public LauncherSettings Result { get; }

    public SettingsForm(LauncherSettings current, bool localEmulatorAvailable)
    {
        Result = new LauncherSettings
        {
            ManifestUrl = current.ManifestUrl,
            GameDir = current.GameDir,
            ServerHost = current.ServerHost,
            GamePort = current.GamePort,
            AuthPort = current.AuthPort,
            AssetPort = current.AssetPort,
            UseLocalServer = current.UseLocalServer,
            AccountId = current.AccountId,
            Nickname = current.Nickname,
            AutoStopServer = current.AutoStopServer,
            MinimizeOnStart = current.MinimizeOnStart,
            RotateShowcase = current.RotateShowcase,
        };

        Text = "Настройки лаунчера";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(576, 430);
        BackColor = SpaceTheme.BgWindow;
        ForeColor = SpaceTheme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 10,
            BackColor = SpaceTheme.BgWindow,
        };
        for (int i = 0; i < 9; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        root.Controls.Add(MakeLabel("Папка установки игры:"), 0, 0);
        var dirRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 2, Margin = new Padding(0, 2, 0, 10), BackColor = SpaceTheme.BgWindow };
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _gameDirBox = MakeTextBox(current.GameDir);
        var browseBtn = MakeSpaceButton("Обзор...", SpaceTheme.BgCardHover, SpaceTheme.BgCard, SpaceTheme.BorderBright, 96);
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Выберите папку установки Ground War Tanks",
                SelectedPath = _gameDirBox.Text,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _gameDirBox.Text = dlg.SelectedPath;
        };
        dirRow.Controls.Add(_gameDirBox, 0, 0);
        dirRow.Controls.Add(browseBtn, 1, 0);
        root.Controls.Add(dirRow, 0, 1);

        root.Controls.Add(MakeLabel("URL манифеста обновлений (manifest.json):"), 0, 2);
        _manifestUrlBox = MakeTextBox(current.ManifestUrl);
        _manifestUrlBox.PlaceholderText = "https://example.com/gwt/manifest.json";
        _manifestUrlBox.Margin = new Padding(0, 2, 0, 10);
        root.Controls.Add(_manifestUrlBox, 0, 3);

        root.Controls.Add(MakeLabel("Режим подключения к серверу:"), 0, 4);
        var modeFlow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 2, 0, 8), BackColor = SpaceTheme.BgWindow };
        _onlineModeRadio = new RadioButton
        {
            AutoSize = true,
            Text = "Онлайн / Radmin VPN",
            Checked = !current.UseLocalServer,
            ForeColor = SpaceTheme.TextPrimary,
            Margin = new Padding(0, 0, 18, 0),
        };
        _localModeRadio = new RadioButton
        {
            AutoSize = true,
            Text = localEmulatorAvailable ? "Локальный сервер на этом ПК" : "Локальный сервер (не найден рядом)",
            Checked = current.UseLocalServer,
            Enabled = localEmulatorAvailable || current.UseLocalServer,
            ForeColor = SpaceTheme.TextPrimary,
        };
        _onlineModeRadio.CheckedChanged += (_, _) => UpdateServerHostEnabled();
        modeFlow.Controls.Add(_onlineModeRadio);
        modeFlow.Controls.Add(_localModeRadio);
        root.Controls.Add(modeFlow, 0, 5);

        root.Controls.Add(MakeLabel("Адрес игрового сервера (IP / домен) и порты (Login / Auth / Asset):"), 0, 6);
        var netRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 4, Margin = new Padding(0, 2, 0, 10), BackColor = SpaceTheme.BgWindow };
        netRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        netRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        netRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        netRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        _serverHostBox = MakeTextBox(current.ServerHost);
        _gamePortNum = MakePortBox(current.GamePort, 9801);
        _authPortNum = MakePortBox(current.AuthPort, 18080);
        _assetPortNum = MakePortBox(current.AssetPort, 18081);
        netRow.Controls.Add(_serverHostBox, 0, 0);
        netRow.Controls.Add(_gamePortNum, 1, 0);
        netRow.Controls.Add(_authPortNum, 2, 0);
        netRow.Controls.Add(_assetPortNum, 3, 0);
        root.Controls.Add(netRow, 0, 7);

        root.Controls.Add(MakeLabel("ID аккаунта (используется для входа на сервер как Player_<ID>):"), 0, 8);
        var accRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 2, Margin = new Padding(0, 2, 0, 10), BackColor = SpaceTheme.BgWindow };
        accRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        accRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _accountIdBox = MakeTextBox(current.AccountId);
        var genIdBtn = MakeSpaceButton("Новый ID", SpaceTheme.BgCardHover, SpaceTheme.BgCard, SpaceTheme.BorderBright, 96);
        genIdBtn.Click += (_, _) => _accountIdBox.Text = Random.Shared.Next(100_000, 9_999_999).ToString();
        accRow.Controls.Add(_accountIdBox, 0, 0);
        accRow.Controls.Add(genIdBtn, 1, 0);
        root.Controls.Add(accRow, 0, 9);

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            Padding = new Padding(18, 9, 18, 9),
            BackColor = SpaceTheme.BgCard,
        };
        var cancelBtn = MakeSpaceButton("Отмена", SpaceTheme.BgCardHover, SpaceTheme.BgCard, SpaceTheme.BorderBright, 104);
        cancelBtn.Dock = DockStyle.Right;
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var spacer = new Panel { Dock = DockStyle.Right, Width = 8, BackColor = SpaceTheme.BgCard };

        var saveBtn = MakeSpaceButton("Сохранить", SpaceTheme.AccentRedBright, SpaceTheme.AccentRedDark, Color.Transparent, 126);
        saveBtn.Dock = DockStyle.Right;
        saveBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_gameDirBox.Text))
            {
                MessageBox.Show(this, "Укажите папку установки игры.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Result.GameDir = _gameDirBox.Text.Trim();
            Result.ManifestUrl = _manifestUrlBox.Text.Trim();
            Result.UseLocalServer = _localModeRadio.Checked;
            Result.ServerHost = Result.UseLocalServer
                ? "127.0.0.1"
                : (string.IsNullOrWhiteSpace(_serverHostBox.Text) ? "127.0.0.1" : _serverHostBox.Text.Trim());
            Result.GamePort = (int)_gamePortNum.Value;
            Result.AuthPort = (int)_authPortNum.Value;
            Result.AssetPort = (int)_assetPortNum.Value;
            Result.AccountId = string.IsNullOrWhiteSpace(_accountIdBox.Text)
                ? Random.Shared.Next(100_000, 9_999_999).ToString()
                : _accountIdBox.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        };

        footer.Controls.Add(saveBtn);
        footer.Controls.Add(spacer);
        footer.Controls.Add(cancelBtn);

        Controls.Add(root);
        Controls.Add(footer);
        UpdateServerHostEnabled();
    }

    private void UpdateServerHostEnabled()
    {
        bool online = _onlineModeRadio.Checked;
        _serverHostBox.Enabled = online;
        _gamePortNum.Enabled = online;
        _authPortNum.Enabled = online;
        _assetPortNum.Enabled = online;
        if (!online)
            _serverHostBox.Text = "127.0.0.1";
    }

    private static Label MakeLabel(string text) => new()
    {
        AutoSize = true,
        Text = text,
        ForeColor = SpaceTheme.TextSecondary,
        Margin = new Padding(0, 0, 0, 3),
    };

    private static TextBox MakeTextBox(string value) => new()
    {
        Dock = DockStyle.Fill,
        Text = value,
        BackColor = SpaceTheme.BgInput,
        ForeColor = SpaceTheme.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static NumericUpDown MakePortBox(int val, int fallback) => new()
    {
        Dock = DockStyle.Fill,
        Minimum = 1,
        Maximum = 65535,
        Value = Math.Clamp(val > 0 ? val : fallback, 1, 65535),
        BackColor = SpaceTheme.BgInput,
        ForeColor = SpaceTheme.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(6, 0, 0, 0),
    };

    private static SpaceButton MakeSpaceButton(string text, Color c1, Color c2, Color border, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 32,
        GradientStart = c1,
        GradientEnd = c2,
        BorderColor = border,
        CornerRadius = 6,
        Margin = new Padding(6, 0, 0, 0),
    };
}
