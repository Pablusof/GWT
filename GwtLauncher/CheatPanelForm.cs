#if !PLAYER_EDITION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GwtLauncher;

public sealed class CheatPanelForm : Form
{
    private sealed class AdminPlayerDto
    {
        [JsonPropertyName("playerName")] public string PlayerName { get; set; } = string.Empty;
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("gold")] public int Gold { get; set; }
        [JsonPropertyName("silver")] public int Silver { get; set; }
        [JsonPropertyName("experience")] public int Experience { get; set; }
        [JsonPropertyName("tankCount")] public int TankCount { get; set; }
        [JsonPropertyName("online")] public bool Online { get; set; }
    }

    private sealed class CatalogTankDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("tier")] public int Tier { get; set; }
        [JsonPropertyName("currency")] public int Currency { get; set; }
        public override string ToString()
        {
            string prem = Currency == 2 ? " [ПРЕМ]" : "";
            return $"[{Id}]  {Tier} ур.  {Name}{prem}";
        }
    }

    private readonly string _adminHost;
    private readonly int _authPort;

    private readonly TextBox _targetNickBox;
    private readonly ListBox _playersListBox;
    private readonly TextBox _amountBox;
    private readonly TextBox _tankSearchBox;
    private readonly ComboBox _tierFilterBox;
    private readonly ListBox _tanksListBox;
    private readonly Label _statusLabel;

    private List<AdminPlayerDto> _players = new();
    private List<CatalogTankDto> _catalogTanks = new();

    public CheatPanelForm(string adminHost, int authPort, string initialPlayerNick)
    {
        _adminHost = string.IsNullOrWhiteSpace(adminHost) ? "127.0.0.1" : adminHost.Trim();
        _authPort = authPort > 0 ? authPort : 18080;

        Text = "Ground War Tanks — Чит-панель администратора";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(920, 600);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = SpaceTheme.BgWindow;
        ForeColor = SpaceTheme.TextPrimary;
        Font = new Font("Segoe UI", 9.2f);

        // Top Bar
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = SpaceTheme.BgCard, Padding = new Padding(16, 10, 16, 10) };
        var titleLbl = new Label
        {
            Location = new Point(16, 12),
            AutoSize = true,
            Text = "ЧИТ-ПАНЕЛЬ // ВЫДАЧА ТАНКОВ И РЕСУРСОВ ПО НИКУ",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = SpaceTheme.TextPrimary,
        };
        var refreshAllBtn = MakeActionBtn("ОБНОВИТЬ", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.BorderBright, 110, 32);
        refreshAllBtn.Location = new Point(632, 11);
        refreshAllBtn.Click += async (_, _) => await LoadAllAsync();

        var webBtn = MakeActionBtn("ВЕБ-ПАНЕЛЬ (/panel)", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.AccentCyan, 154, 32);
        webBtn.Location = new Point(748, 11);
        webBtn.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = $"http://{_adminHost}:{_authPort}/panel", UseShellExecute = true });
            }
            catch { }
        };
        topPanel.Controls.Add(titleLbl);
        topPanel.Controls.Add(refreshAllBtn);
        topPanel.Controls.Add(webBtn);

        // Bottom Status Bar
        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            BackColor = SpaceTheme.BgConsole,
            ForeColor = SpaceTheme.AccentCyan,
            Font = new Font("Consolas", 9f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 14, 0),
            Text = "Подключение к серверу...",
        };

        // Main Body
        var body = new Panel { Dock = DockStyle.Fill, BackColor = SpaceTheme.BgWindow, Padding = new Padding(16) };

        // Left Column: Player selector + Resource/Account actions
        var leftCard = new Panel { Location = new Point(16, 12), Size = new Size(368, 490), BackColor = SpaceTheme.BgCard };
        leftCard.Paint += (_, e) => { using var p = new Pen(SpaceTheme.BorderSubtle); e.Graphics.DrawRectangle(p, 0, 0, leftCard.Width - 1, leftCard.Height - 1); };

        leftCard.Controls.Add(MakeSectionLbl("ЦЕЛЕВОЙ НИК ИГРОКА", 14, 12));
        _targetNickBox = MakeInput(initialPlayerNick, 14, 32, 340);
        leftCard.Controls.Add(_targetNickBox);

        leftCard.Controls.Add(MakeSectionLbl("ИГРОКИ НА СЕРВЕРЕ (КЛИК ДЛЯ ВЫБОРА)", 14, 66));
        _playersListBox = new ListBox
        {
            Location = new Point(14, 86),
            Size = new Size(340, 150),
            BackColor = SpaceTheme.BgInput,
            ForeColor = SpaceTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.8f),
        };
        _playersListBox.SelectedIndexChanged += (_, _) =>
        {
            if (_playersListBox.SelectedIndex >= 0 && _playersListBox.SelectedIndex < _players.Count)
                _targetNickBox.Text = _players[_playersListBox.SelectedIndex].PlayerName;
        };
        leftCard.Controls.Add(_playersListBox);

        leftCard.Controls.Add(MakeSectionLbl("РЕСУРСЫ И ПРОКАЧКА", 14, 246));
        _amountBox = MakeInput("1000000", 14, 266, 120);
        leftCard.Controls.Add(_amountBox);

        var addGoldBtn = MakeActionBtn("+ЗОЛОТО", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.AccentAmber, 70, 28);
        addGoldBtn.Location = new Point(140, 265);
        addGoldBtn.Click += async (_, _) => await RunAdminAsync("add_gold", new() { ["amount"] = SafeInt(_amountBox.Text, 1000000) });

        var addSilverBtn = MakeActionBtn("+СЕРЕБРО", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.BorderBright, 74, 28);
        addSilverBtn.Location = new Point(214, 265);
        addSilverBtn.Click += async (_, _) => await RunAdminAsync("add_silver", new() { ["amount"] = SafeInt(_amountBox.Text, 1000000) });

        var addExpBtn = MakeActionBtn("+ОПЫТ", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.AccentCyan, 62, 28);
        addExpBtn.Location = new Point(292, 265);
        addExpBtn.Click += async (_, _) => await RunAdminAsync("add_exp", new() { ["amount"] = SafeInt(_amountBox.Text, 1000000) });

        var lvlBtn = MakeActionBtn("УР. 99", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.BorderBright, 108, 30);
        lvlBtn.Location = new Point(14, 302);
        lvlBtn.Click += async (_, _) => await RunAdminAsync("set_level", new() { ["amount"] = "99" });

        var premBtn = MakeActionBtn("ПРЕМ 30д", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.BorderBright, 112, 30);
        premBtn.Location = new Point(128, 302);
        premBtn.Click += async (_, _) => await RunAdminAsync("give_premium", new() { ["days"] = "30" });

        var vipBtn = MakeActionBtn("VIP 30д", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.BorderBright, 108, 30);
        vipBtn.Location = new Point(246, 302);
        vipBtn.Click += async (_, _) => await RunAdminAsync("give_vip", new() { ["days"] = "30" });

        var consBtn = MakeActionBtn("РАСХОДНИКИ x50", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.BorderBright, 167, 32);
        consBtn.Location = new Point(14, 340);
        consBtn.Click += async (_, _) => await RunAdminAsync("give_consumables", new() { ["amount"] = "50" });

        var eqBtn = MakeActionBtn("ОБОРУДОВАНИЕ x5", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.BorderBright, 167, 32);
        eqBtn.Location = new Point(187, 340);
        eqBtn.Click += async (_, _) => await RunAdminAsync("give_equipment", new() { ["amount"] = "5" });

        var maxBtn = MakeActionBtn("МАКС РЕСУРСЫ (999M)", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.AccentCyan, 340, 32);
        maxBtn.Location = new Point(14, 382);
        maxBtn.Click += async (_, _) => await RunAdminAsync("max");

        var godBtn = MakeActionBtn("GOD MODE (ВСЁ И СРАЗУ)", SpaceTheme.AccentRedBright, SpaceTheme.AccentRedDark, Color.Transparent, 340, 38);
        godBtn.Location = new Point(14, 424);
        godBtn.Click += async (_, _) => await RunAdminAsync("god");

        leftCard.Controls.AddRange(new Control[] { addGoldBtn, addSilverBtn, addExpBtn, lvlBtn, premBtn, vipBtn, consBtn, eqBtn, maxBtn, godBtn });

        // Right Column: Searchable Tank Catalog + Grant/Remove
        var rightCard = new Panel { Location = new Point(398, 12), Size = new Size(506, 490), BackColor = SpaceTheme.BgCard };
        rightCard.Paint += (_, e) => { using var p = new Pen(SpaceTheme.BorderSubtle); e.Graphics.DrawRectangle(p, 0, 0, rightCard.Width - 1, rightCard.Height - 1); };

        rightCard.Controls.Add(MakeSectionLbl("ВЫДАЧА ТАНКОВ ПО КАТАЛОГУ", 14, 12));
        _tankSearchBox = MakeInput(string.Empty, 14, 32, 354);
        _tankSearchBox.PlaceholderText = "Поиск танка по названию или ID (например: ИС-7, Армата, 197)...";
        _tankSearchBox.TextChanged += (_, _) => RenderTankList();

        _tierFilterBox = new ComboBox
        {
            Location = new Point(374, 32),
            Size = new Size(118, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = SpaceTheme.BgInput,
            ForeColor = SpaceTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
        };
        _tierFilterBox.Items.Add("Все уровни");
        for (int t = 10; t >= 1; t--) _tierFilterBox.Items.Add($"{t} ур.");
        _tierFilterBox.SelectedIndex = 0;
        _tierFilterBox.SelectedIndexChanged += (_, _) => RenderTankList();

        _tanksListBox = new ListBox
        {
            Location = new Point(14, 68),
            Size = new Size(478, 306),
            BackColor = SpaceTheme.BgInput,
            ForeColor = SpaceTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f),
        };

        var giveTankBtn = MakeActionBtn("ВЫДАТЬ ВЫБРАННЫЙ ТАНК", SpaceTheme.AccentRedBright, SpaceTheme.AccentRedDark, Color.Transparent, 310, 38);
        giveTankBtn.Location = new Point(14, 384);
        giveTankBtn.Click += async (_, _) => await GiveSelectedTankAsync();

        var removeTankBtn = MakeActionBtn("УДАЛИТЬ ТАНК", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.BorderBright, 162, 38);
        removeTankBtn.Location = new Point(330, 384);
        removeTankBtn.Click += async (_, _) => await RemoveSelectedTankAsync();

        var giveAllBtn = MakeActionBtn("ВЫДАТЬ ВСЕ ТАНКИ", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.BorderBright, 154, 34);
        giveAllBtn.Location = new Point(14, 432);
        giveAllBtn.Click += async (_, _) => await RunAdminAsync("give_all_tanks");

        var givePremTanksBtn = MakeActionBtn("ВСЕ ПРЕМ-ТАНКИ", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.AccentAmber, 156, 34);
        givePremTanksBtn.Location = new Point(175, 432);
        givePremTanksBtn.Click += async (_, _) => await RunAdminAsync("give_all_premium");

        var listPlayerTanksBtn = MakeActionBtn("ТАНКИ ИГРОКА", SpaceTheme.BgCardHover, SpaceTheme.BgInput, SpaceTheme.AccentCyan, 155, 34);
        listPlayerTanksBtn.Location = new Point(337, 432);
        listPlayerTanksBtn.Click += async (_, _) => await RunAdminAsync("list_tanks");

        rightCard.Controls.AddRange(new Control[]
        {
            _tankSearchBox, _tierFilterBox, _tanksListBox,
            giveTankBtn, removeTankBtn, giveAllBtn, givePremTanksBtn, listPlayerTanksBtn
        });

        body.Controls.Add(leftCard);
        body.Controls.Add(rightCard);

        Controls.Add(body);
        Controls.Add(topPanel);
        Controls.Add(_statusLabel);

        Shown += async (_, _) => await LoadAllAsync();
    }

    private async Task LoadAllAsync()
    {
        try
        {
            _statusLabel.Text = $"Загрузка данных с {_adminHost}:{_authPort}...";
            var playersJson = await UpdaterService.AdminRequestAsync(_adminHost, _authPort, "list_players");
            if (playersJson.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase))
            {
                _statusLabel.Text = playersJson;
                return;
            }
            _players = JsonSerializer.Deserialize<List<AdminPlayerDto>>(playersJson) ?? new();

            _playersListBox.Items.Clear();
            foreach (var p in _players)
            {
                string on = p.Online ? "● " : "  ";
                _playersListBox.Items.Add($"{on}{p.PlayerName,-18} Ур.{p.Level,-2}  Т:{p.TankCount,-3}  Au:{p.Gold}");
            }
            if (string.IsNullOrWhiteSpace(_targetNickBox.Text) && _players.Count > 0)
                _targetNickBox.Text = _players[0].PlayerName;

            if (_catalogTanks.Count == 0)
            {
                var tanksJson = await UpdaterService.AdminRequestAsync(_adminHost, _authPort, "list_catalog_tanks");
                _catalogTanks = JsonSerializer.Deserialize<List<CatalogTankDto>>(tanksJson) ?? new();
                RenderTankList();
            }

            _statusLabel.Text = $"Готово • Игроков в БД: {_players.Count} • Танков в каталоге: {_catalogTanks.Count}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Нет связи с сервером ({_adminHost}:{_authPort}): {ex.Message}";
        }
    }

    private void RenderTankList()
    {
        string q = _tankSearchBox.Text.Trim();
        int wantTier = _tierFilterBox.SelectedIndex > 0 ? 11 - _tierFilterBox.SelectedIndex : 0;

        _tanksListBox.BeginUpdate();
        _tanksListBox.Items.Clear();
        foreach (var t in _catalogTanks)
        {
            if (wantTier > 0 && t.Tier != wantTier) continue;
            if (!string.IsNullOrEmpty(q) &&
                !t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !t.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _tanksListBox.Items.Add(t);
        }
        if (_tanksListBox.Items.Count > 0) _tanksListBox.SelectedIndex = 0;
        _tanksListBox.EndUpdate();
    }

    private async Task GiveSelectedTankAsync()
    {
        if (_tanksListBox.SelectedItem is not CatalogTankDto tank)
        {
            _statusLabel.Text = "Выберите танк из списка справа.";
            return;
        }
        await RunAdminAsync("give_tank", new() { ["vehicleId"] = tank.Id.ToString() });
    }

    private async Task RemoveSelectedTankAsync()
    {
        if (_tanksListBox.SelectedItem is not CatalogTankDto tank)
        {
            _statusLabel.Text = "Выберите танк из списка справа.";
            return;
        }
        await RunAdminAsync("remove_tank", new() { ["vehicleId"] = tank.Id.ToString() });
    }

    private async Task RunAdminAsync(string cmd, Dictionary<string, string>? extra = null)
    {
        string nick = _targetNickBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(nick))
        {
            _statusLabel.Text = "Укажите никнейм игрока.";
            return;
        }

        try
        {
            string res = await UpdaterService.AdminRequestAsync(_adminHost, _authPort, cmd, nick, extra);
            _statusLabel.Text = res;
            if (!cmd.StartsWith("list_", StringComparison.Ordinal))
                await RefreshPlayersOnlyAsync();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Ошибка запроса: {ex.Message}";
        }
    }

    private async Task RefreshPlayersOnlyAsync()
    {
        try
        {
            var playersJson = await UpdaterService.AdminRequestAsync(_adminHost, _authPort, "list_players");
            if (playersJson.StartsWith("["))
            {
                _players = JsonSerializer.Deserialize<List<AdminPlayerDto>>(playersJson) ?? new();
                _playersListBox.Items.Clear();
                foreach (var p in _players)
                {
                    string on = p.Online ? "● " : "  ";
                    _playersListBox.Items.Add($"{on}{p.PlayerName,-18} Ур.{p.Level,-2}  Т:{p.TankCount,-3}  Au:{p.Gold}");
                }
            }
        }
        catch { }
    }

    private static string SafeInt(string? raw, int fallback) =>
        int.TryParse(raw?.Trim(), out int v) ? v.ToString() : fallback.ToString();

    private static Label MakeSectionLbl(string text, int x, int y) => new()
    {
        Location = new Point(x, y),
        Size = new Size(340, 16),
        Text = text,
        Font = new Font("Segoe UI", 7.6f, FontStyle.Bold),
        ForeColor = SpaceTheme.AccentRedBright,
    };

    private static TextBox MakeInput(string value, int x, int y, int w) => new()
    {
        Location = new Point(x, y),
        Size = new Size(w, 26),
        Text = value,
        BackColor = SpaceTheme.BgInput,
        ForeColor = SpaceTheme.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Consolas", 9f),
    };

    private static SpaceButton MakeActionBtn(string text, Color c1, Color c2, Color border, int w, int h) => new()
    {
        Text = text,
        Size = new Size(w, h),
        GradientStart = c1,
        GradientEnd = c2,
        BorderColor = border,
        CornerRadius = 6,
        Font = new Font("Segoe UI", 8f, FontStyle.Bold),
    };
}
#endif
