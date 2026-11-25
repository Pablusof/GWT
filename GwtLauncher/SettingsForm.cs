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
            AutoStopServer = current.AutoStopServer,
            MinimizeOnStart = current.MinimizeOnStart,
            RotateShowcase = current.RotateShowcase,
        };

        Text = "Настройки лаунчера";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 415);
        BackColor = Color.FromArgb(24, 27, 32);
        ForeColor = Color.FromArgb(232, 236, 242);
        Font = new Font("Segoe UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 10,
            BackColor = BackColor,
        };
        for (int i = 0; i < 9; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // 1. Папка игры
        root.Controls.Add(MakeLabel("Папка установки игры:"), 0, 0);
        var dirRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 30, ColumnCount = 2, Margin = new Padding(0, 2, 0, 10) };
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _gameDirBox = MakeTextBox(current.GameDir);
        var browseBtn = MakeButton("Обзор...", Color.FromArgb(56, 62, 74), 90);
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Выберите папку установки Ground War Tanks",
                SelectedPath = _gameDirBox.Text,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _gameDirBox.Text = dlg.SelectedPath;
            }
        };
        dirRow.Controls.Add(_gameDirBox, 0, 0);
        dirRow.Controls.Add(browseBtn, 1, 0);
        root.Controls.Add(dirRow, 0, 1);

        // 2. URL манифеста
        root.Controls.Add(MakeLabel("URL манифеста обновлений (manifest.json):"), 0, 2);
        _manifestUrlBox = MakeTextBox(current.ManifestUrl);
        _manifestUrlBox.PlaceholderText = "https://example.com/gwt/manifest.json";
        _manifestUrlBox.Margin = new Padding(0, 2, 0, 10);
        root.Controls.Add(_manifestUrlBox, 0, 3);

        // 3. Режим сервера (Онлайн Вариант А vs Локальный)
        root.Controls.Add(MakeLabel("Режим подключения к серверу:"), 0, 4);
        var modeFlow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 2, 0, 8) };
        _onlineModeRadio = new RadioButton
        {
            AutoSize = true,
            Text = "Онлайн-сервер (Вариант A)",
            Checked = !current.UseLocalServer,
            ForeColor = ForeColor,
            Margin = new Padding(0, 0, 18, 0),
        };
        _localModeRadio = new RadioButton
        {
            AutoSize = true,
            Text = localEmulatorAvailable ? "Локальный сервер на этом ПК" : "Локальный сервер (не найден рядом)",
            Checked = current.UseLocalServer,
            Enabled = localEmulatorAvailable || current.UseLocalServer,
            ForeColor = ForeColor,
        };
        _onlineModeRadio.CheckedChanged += (_, _) => UpdateServerHostEnabled();
        modeFlow.Controls.Add(_onlineModeRadio);
        modeFlow.Controls.Add(_localModeRadio);
        root.Controls.Add(modeFlow, 0, 5);

        // 4. Адрес сервера и порты
        root.Controls.Add(MakeLabel("Адрес игрового сервера (IP / домен) и порты (Login / Auth / Asset):"), 0, 6);
        var netRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 4, Margin = new Padding(0, 2, 0, 10) };
        netRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        netRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        netRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        netRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        _serverHostBox = MakeTextBox(current.ServerHost);
        _gamePortNum = MakePortBox(current.GamePort, 9801);
        _authPortNum = MakePortBox(current.AuthPort, 18080);
        _assetPortNum = MakePortBox(current.AssetPort, 18081);
        netRow.Controls.Add(_serverHostBox, 0, 0);
        netRow.Controls.Add(_gamePortNum, 1, 0);
        netRow.Controls.Add(_authPortNum, 2, 0);
        netRow.Controls.Add(_assetPortNum, 3, 0);
        root.Controls.Add(netRow, 0, 7);

        // 5. ID аккаунта
        root.Controls.Add(MakeLabel("ID аккаунта (используется для входа на сервер как Player_<ID>):"), 0, 8);
        var accRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 30, ColumnCount = 2, Margin = new Padding(0, 2, 0, 10) };
        accRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        accRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _accountIdBox = MakeTextBox(current.AccountId);
        var genIdBtn = MakeButton("Новый ID", Color.FromArgb(56, 62, 74), 90);
        genIdBtn.Click += (_, _) => _accountIdBox.Text = Random.Shared.Next(100_000, 9_999_999).ToString();
        accRow.Controls.Add(_accountIdBox, 0, 0);
        accRow.Controls.Add(genIdBtn, 1, 0);
        root.Controls.Add(accRow, 0, 9);

        // 6. Кнопки OK / Отмена
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(16, 6, 16, 8),
            BackColor = Color.FromArgb(30, 34, 40),
        };
        var cancelBtn = MakeButton("Отмена", Color.FromArgb(62, 68, 79), 96);
        cancelBtn.DialogResult = DialogResult.Cancel;
        var saveBtn = MakeButton("Сохранить", Color.FromArgb(46, 148, 79), 110);
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
        footer.Controls.Add(cancelBtn);
        footer.Controls.Add(saveBtn);

        Controls.Add(root);
        Controls.Add(footer);
        AcceptButton = saveBtn;
        CancelButton = cancelBtn;
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
        {
            _serverHostBox.Text = "127.0.0.1";
        }
    }

    private Label MakeLabel(string text) => new()
    {
        AutoSize = true,
        Text = text,
        ForeColor = Color.FromArgb(186, 194, 206),
        Margin = new Padding(0, 0, 0, 2),
    };

    private static TextBox MakeTextBox(string value) => new()
    {
        Dock = DockStyle.Fill,
        Text = value,
        BackColor = Color.FromArgb(35, 39, 46),
        ForeColor = Color.FromArgb(236, 240, 245),
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static NumericUpDown MakePortBox(int val, int fallback) => new()
    {
        Dock = DockStyle.Fill,
        Minimum = 1,
        Maximum = 65535,
        Value = Math.Clamp(val > 0 ? val : fallback, 1, 65535),
        BackColor = Color.FromArgb(35, 39, 46),
        ForeColor = Color.FromArgb(236, 240, 245),
        Margin = new Padding(6, 0, 0, 0),
    };

    private static Button MakeButton(string text, Color back, int width)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 28,
            BackColor = back,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }
}
