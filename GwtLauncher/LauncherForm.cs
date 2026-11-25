using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GwtLauncher;

public sealed class LauncherForm : Form, IBackdropProvider
{
    private enum LauncherTab
    {
        Home,
        Patches,
    }

    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private readonly string? _detectedEmuDir;
    private readonly Image? _backdropImage;
    private Bitmap? _backdropSource;
    private Bitmap? _sunOverlaySprite;
    private Bitmap? _emberSprite;
    private Bitmap? _vignetteSprite;
    private Bitmap? _patchesCardSprite;
    private Bitmap? _drawerSlideSprite;
    private Bitmap? _composedFrame;
    private ImageAttributes? _sunAttrs;
    private ImageAttributes? _emberAttrs;
    private ImageAttributes? _patchesAttrs;
    private LauncherSettings _settings;

    private sealed record ChangelogEntry(string Version, string Date, string Badge, Color BadgeColor, string[] Bullets);
    private static readonly ChangelogEntry[] ChangelogEntries =
    {
        new("v1.3.0", "25 ноября 2026", "ЛАУНЧЕР", SpaceTheme.AccentRedBright, new[]
        {
            "Новый полноэкранный Space-интерфейс в стиле SpaceRust с плавающим HUD и выдвижными настройками.",
            "Добавлены живые анимации фона (параллакс, лучи заката, искры) и сдвоенная кнопка запуска.",
            "Добавлен встроенный список изменений и диагностический журнал операций.",
        }),
        new("v1.2.0", "24 ноября 2026", "ГЕЙМПЛЕЙ", SpaceTheme.AccentCyan, new[]
        {
            "Полностью реализована работа всех 162 модулей оборудования, 14 расходников и дымовых гранат в бою.",
            "Исправлены покупка, установка, снятие и автопополнение боекомплекта и расходников в ангаре.",
            "Улучшен баланс начисления бонусов, ремонта техники и переподключения игроков.",
        }),
        new("v1.1.0", "22 ноября 2026", "СЕТЬ", SpaceTheme.AccentGreen, new[]
        {
            "Подбор команды показывает и собирает реальных игроков на одну карту без подмены ботами.",
            "Исправлены случайные разрывы соединения у клиентов при игре через Radmin VPN.",
        }),
    };

    private struct EmberParticle
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Size;
        public float Alpha;
        public float Phase;
        public float Life;
        public float MaxLife;
        public bool IsOrange;
    }

    private readonly EmberParticle[] _embers = new EmberParticle[44];
    private readonly List<IAnimatable> _animatables = new();
    private readonly Stopwatch _animClock = Stopwatch.StartNew();
    private float _lastAnimSec;
    private float _animTime;
    private float _parallaxX;
    private float _parallaxY;
    private float _drawerX = 1020f;
    private float _patchesAlpha;
    private readonly System.Windows.Forms.Timer _animTimer;

    private Process? _serverProcess;
    private Process? _gameProcess;
    private bool _serverStartedByLauncher;
    private bool _isLaunching;
    private bool _isUpdating;
    private bool _remoteServerOnline;
    private int _remoteLatencyMs = -1;
    private long _lastRemotePingMs;
    private string _installedGameVersion = "1.0.0";
    private CancellationTokenSource? _updateCts;
    private UpdateCheckResult? _pendingUpdate;
    private LauncherTab _activeTab = LauncherTab.Home;
    private bool _settingsOpen;
    private bool _showingLogInPatches;

    // Header Controls
    private readonly SpaceGlassPanel _headerBar;
    private readonly PictureBox _brandIconBox;
    private readonly SpaceHeaderTab _tabHome;
    private readonly SpaceHeaderTab _tabPatches;
    private readonly SpaceButton _headerSettingsBtn;

    // Bottom Floating HUD
    private readonly SpaceGlassPanel _profileCard;
    private readonly PictureBox _profileIconBox;
    private readonly Label _profileNameLabel;
    private readonly Label _profileStatusLabel;
    private string _statusCaptionText = "ВЕРСИЯ: v1.0.0";
    private readonly SpaceProgressBar _progressBar;
    private readonly SpaceSplitPlayButton _primaryButton;
    private readonly ContextMenuStrip _playDropdownMenu;
    private readonly SpaceGlassPanel _serverPillPanel;
    private readonly Label _serverPillLabel;

    // Patches & Logs Overlay
    private readonly SpaceGlassPanel _patchesOverlay;
    private readonly Panel _consoleHostPanel;
    private readonly SpaceButton _patchesModeToggleBtn;
    private readonly TextBox _logBox;

    // Right-Side Settings Drawer
    private readonly SpaceGlassPanel _settingsDrawer;
    private readonly SpaceButton _setModeOnlineBtn;
    private readonly SpaceButton _setModeLocalBtn;
    private readonly TextBox _setServerHostBox;
    private readonly NumericUpDown _setGamePortNum;
    private readonly NumericUpDown _setAuthPortNum;
    private readonly NumericUpDown _setAssetPortNum;
    private readonly TextBox _setAccountIdBox;
    private readonly SpaceToggle _minimizeOnStartCheck;
    private readonly SpaceToggle _autoStopServerCheck;
    private readonly TextBox _setGameDirBox;
    private readonly TextBox _setManifestUrlBox;
    private readonly SpaceButton _setSaveBtn;
    private readonly SpaceButton _setVerifyBtn;

    private readonly System.Windows.Forms.Timer _pollTimer;

    private string GameDir => _settings.GameDir;
    private string TankExePath => Path.Combine(GameDir, "Tank.exe");

    public Bitmap? GetComposedBackdrop() => _composedFrame;
    public Form HostForm => this;
    public float AnimationTime => _animTime;

    public LauncherForm()
    {
        var (detectedGameDir, detectedEmuDir) = ResolveLocalDirectories();
        _detectedEmuDir = detectedEmuDir;
        _settings = LauncherSettings.Load(detectedGameDir, detectedEmuDir);
        _backdropImage = LoadEmbeddedBannerImage();
        InitializeAnimatedBackdrop();

        Text = $"Ground War Tanks — Launcher v{LauncherSettings.CurrentLauncherVersion}";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1020, 600);
        BackColor = SpaceTheme.BgWindow;
        ForeColor = SpaceTheme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // ---------------- TOP HEADER BAR (56px) ----------------
        _headerBar = new SpaceGlassPanel
        {
            Dock = DockStyle.Top,
            Height = 56,
            FullRectNoRadius = true,
            DrawBottomSeparator = true,
            GlassTint = SpaceTheme.BgHeader,
            BorderColor = Color.FromArgb(45, 255, 255, 255),
        };
        _headerBar.Paint += PaintHeaderBrand;
        EnableDrag(_headerBar);

        _brandIconBox = new PictureBox
        {
            Location = new Point(18, 12),
            Size = new Size(32, 32),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        EnableDrag(_brandIconBox);

        _tabHome = new SpaceHeaderTab
        {
            Location = new Point(386, 11),
            Size = new Size(122, 34),
            IconGlyph = "⌂",
            Text = "ГЛАВНАЯ",
            IsActive = true,
        };
        _tabPatches = new SpaceHeaderTab
        {
            Location = new Point(516, 11),
            Size = new Size(114, 34),
            IconGlyph = "≡",
            Text = "ПАТЧИ",
            IsActive = false,
        };
        _tabHome.Click += (_, _) => SwitchTab(LauncherTab.Home);
        _tabPatches.Click += (_, _) => SwitchTab(LauncherTab.Patches);

        _headerSettingsBtn = new SpaceButton
        {
            Location = new Point(794, 11),
            Size = new Size(124, 34),
            Text = "НАСТРОЙКИ",
            DrawGearIcon = true,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            GradientStart = Color.FromArgb(24, 28, 38),
            GradientEnd = Color.FromArgb(18, 21, 29),
            BorderColor = SpaceTheme.BorderSubtle,
            CornerRadius = 7,
        };
        _headerSettingsBtn.Click += (_, _) => ToggleSettingsDrawer(!_settingsOpen);

        var minBtn = new SpaceTitleButton
        {
            Location = new Point(932, 11),
            Size = new Size(36, 34),
            Text = "—",
        };
        minBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;

        var closeBtn = new SpaceTitleButton
        {
            Location = new Point(972, 11),
            Size = new Size(36, 34),
            Text = "✕",
            IsClose = true,
        };
        closeBtn.Click += (_, _) => Close();

        _headerBar.Controls.Add(_brandIconBox);
        _headerBar.Controls.Add(_tabHome);
        _headerBar.Controls.Add(_tabPatches);
        _headerBar.Controls.Add(_headerSettingsBtn);
        _headerBar.Controls.Add(minBtn);
        _headerBar.Controls.Add(closeBtn);

        // ---------------- BOTTOM FLOATING HUD ----------------
        _profileCard = new SpaceGlassPanel
        {
            Location = new Point(22, 508),
            Size = new Size(238, 70),
            CornerRadius = 10,
            GlassTint = Color.FromArgb(215, 16, 19, 27),
            BorderColor = SpaceTheme.BorderGlass,
            Cursor = Cursors.Hand,
        };
        _profileCard.Click += (_, _) => ToggleSettingsDrawer(true);

        _profileIconBox = new PictureBox
        {
            Location = new Point(12, 15),
            Size = new Size(40, 40),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        _profileIconBox.Click += (_, _) => ToggleSettingsDrawer(true);

        _profileNameLabel = new Label
        {
            Location = new Point(60, 14),
            Size = new Size(164, 20),
            Text = $"Player_{_settings.AccountId}",
            Font = new Font("Segoe UI", 9.8f, FontStyle.Bold),
            ForeColor = SpaceTheme.TextPrimary,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };
        _profileNameLabel.Click += (_, _) => ToggleSettingsDrawer(true);

        _profileStatusLabel = new Label
        {
            Location = new Point(60, 36),
            Size = new Size(164, 18),
            Text = "● Проверка...",
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = SpaceTheme.AccentGreen,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };
        _profileStatusLabel.Click += (_, _) => ToggleSettingsDrawer(true);

        _profileCard.Controls.Add(_profileIconBox);
        _profileCard.Controls.Add(_profileNameLabel);
        _profileCard.Controls.Add(_profileStatusLabel);

        _statusCaptionText = $"ВЕРСИЯ: v{_installedGameVersion}";

        _progressBar = new SpaceProgressBar
        {
            Location = new Point(386, 523),
            Size = new Size(248, 5),
            Maximum = 1000,
            Value = 0,
            Visible = false,
        };

        _primaryButton = new SpaceSplitPlayButton
        {
            Location = new Point(386, 532),
            Size = new Size(248, 46),
            Text = "ИГРАТЬ",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        };
        _primaryButton.PrimaryClick += async (_, _) => await PrimaryButtonClickedAsync();
        _primaryButton.DropdownClick += (_, _) => ShowPlayDropdownMenu();

        _playDropdownMenu = BuildPlayDropdownMenu();

        _serverPillPanel = new SpaceGlassPanel
        {
            Location = new Point(756, 532),
            Size = new Size(242, 46),
            CornerRadius = 10,
            GlassTint = Color.FromArgb(215, 16, 19, 27),
            BorderColor = SpaceTheme.BorderGlass,
            Cursor = Cursors.Hand,
        };
        _serverPillPanel.Click += (_, _) => ToggleSettingsDrawer(true);

        _serverPillLabel = new Label
        {
            Location = new Point(12, 6),
            Size = new Size(218, 34),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "● Ожидание пинга...",
            Font = new Font("Segoe UI", 8.4f, FontStyle.Bold),
            ForeColor = SpaceTheme.TextSecondary,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };
        _serverPillLabel.Click += (_, _) => ToggleSettingsDrawer(true);
        _serverPillPanel.Controls.Add(_serverPillLabel);

        // ---------------- PATCHES & LOGS OVERLAY ----------------
        _patchesOverlay = new SpaceGlassPanel
        {
            Location = new Point(0, 56),
            Size = new Size(1020, 434),
            FullRectNoRadius = true,
            GlassTint = Color.FromArgb(0, 11, 13, 18),
            Visible = false,
        };
        _patchesOverlay.Paint += PaintPatchesOverlay;

        _patchesModeToggleBtn = new SpaceButton
        {
            Location = new Point(680, 38),
            Size = new Size(160, 34),
            Text = "ЖУРНАЛ ЛОГОВ",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BorderColor = SpaceTheme.BorderBright,
            CornerRadius = 7,
        };
        _patchesModeToggleBtn.Click += (_, _) => TogglePatchesConsoleView();

        _consoleHostPanel = new Panel
        {
            Location = new Point(180, 96),
            Size = new Size(660, 326),
            BackColor = Color.FromArgb(11, 13, 18),
            Visible = false,
        };
        _logBox = new TextBox
        {
            Location = new Point(0, 0),
            Size = new Size(660, 282),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SpaceTheme.BgConsole,
            ForeColor = Color.FromArgb(182, 194, 214),
            Font = new Font("Consolas", 8.8f),
            BorderStyle = BorderStyle.FixedSingle,
        };
        var copyLogBtn = new SpaceButton
        {
            Location = new Point(0, 290),
            Size = new Size(140, 32),
            Text = "КОПИРОВАТЬ ЛОГ",
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            BorderColor = SpaceTheme.BorderBright,
        };
        copyLogBtn.Click += (_, _) => { try { if (!string.IsNullOrEmpty(_logBox.Text)) Clipboard.SetText(_logBox.Text); } catch { } };
        var clearLogBtn = new SpaceButton
        {
            Location = new Point(150, 290),
            Size = new Size(116, 32),
            Text = "ОЧИСТИТЬ",
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            BorderColor = SpaceTheme.BorderSubtle,
        };
        clearLogBtn.Click += (_, _) => _logBox.Clear();
        _consoleHostPanel.Controls.Add(_logBox);
        _consoleHostPanel.Controls.Add(copyLogBtn);
        _consoleHostPanel.Controls.Add(clearLogBtn);

        _patchesOverlay.Controls.Add(_patchesModeToggleBtn);
        _patchesOverlay.Controls.Add(_consoleHostPanel);

        // ---------------- RIGHT-SIDE SETTINGS DRAWER ----------------
        _settingsDrawer = new SpaceGlassPanel
        {
            Location = new Point(1020, 56),
            Size = new Size(394, 544),
            FullRectNoRadius = true,
            UseSolidBackground = true,
            GlassTint = Color.FromArgb(255, 13, 16, 23),
            Visible = false,
        };
        _settingsDrawer.Paint += (_, e) =>
        {
            using var p = new Pen(SpaceTheme.BorderBright, 1f);
            e.Graphics.DrawLine(p, 0, 0, 0, _settingsDrawer.Height);
        };

        var drawerTitle = new Label
        {
            Location = new Point(22, 16),
            Size = new Size(260, 24),
            Text = "НАСТРОЙКИ",
            Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
            ForeColor = SpaceTheme.TextPrimary,
            BackColor = Color.Transparent,
        };
        var drawerCloseBtn = new SpaceTitleButton
        {
            Location = new Point(342, 12),
            Size = new Size(34, 30),
            Text = "✕",
            IsClose = true,
        };
        drawerCloseBtn.Click += (_, _) => ToggleSettingsDrawer(false);
        _settingsDrawer.Controls.Add(drawerTitle);
        _settingsDrawer.Controls.Add(drawerCloseBtn);

        int dy = 56;
        _settingsDrawer.Controls.Add(MakeDrawerSectionLabel("РЕЖИМ ПОДКЛЮЧЕНИЯ", 22, dy));
        dy += 20;
        _setModeOnlineBtn = new SpaceButton
        {
            Location = new Point(22, dy),
            Size = new Size(172, 32),
            Text = "ОНЛАЙН / RADMIN",
            Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
            IsSelected = !_settings.UseLocalServer,
        };
        _setModeLocalBtn = new SpaceButton
        {
            Location = new Point(200, dy),
            Size = new Size(172, 32),
            Text = "ЛОКАЛЬНЫЙ ПК",
            Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
            IsSelected = _settings.UseLocalServer,
            Enabled = _detectedEmuDir != null || _settings.UseLocalServer,
        };
        _setModeOnlineBtn.Click += (_, _) => SetDrawerMode(useLocal: false);
        _setModeLocalBtn.Click += (_, _) => SetDrawerMode(useLocal: true);
        _settingsDrawer.Controls.Add(_setModeOnlineBtn);
        _settingsDrawer.Controls.Add(_setModeLocalBtn);

        dy += 42;
        _settingsDrawer.Controls.Add(MakeDrawerFieldHint("IP сервера и порты (Login / Auth / Asset):", 22, dy));
        dy += 18;
        _setServerHostBox = MakeDarkInput(_settings.ServerHost, 22, dy, 162);
        _setGamePortNum = MakeDarkPort(_settings.GamePort, 9801, 190, dy, 58);
        _setAuthPortNum = MakeDarkPort(_settings.AuthPort, 18080, 252, dy, 58);
        _setAssetPortNum = MakeDarkPort(_settings.AssetPort, 18081, 314, dy, 58);
        _settingsDrawer.Controls.Add(_setServerHostBox);
        _settingsDrawer.Controls.Add(_setGamePortNum);
        _settingsDrawer.Controls.Add(_setAuthPortNum);
        _settingsDrawer.Controls.Add(_setAssetPortNum);

        dy += 40;
        _settingsDrawer.Controls.Add(MakeDrawerSectionLabel("ПРОФИЛЬ И ЗАПУСК", 22, dy));
        dy += 20;
        _setAccountIdBox = MakeDarkInput(_settings.AccountId, 22, dy, 240);
        var genIdBtn = new SpaceButton
        {
            Location = new Point(268, dy - 1),
            Size = new Size(104, 28),
            Text = "НОВЫЙ ID",
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            BorderColor = SpaceTheme.BorderBright,
        };
        genIdBtn.Click += (_, _) => _setAccountIdBox.Text = Random.Shared.Next(100_000, 9_999_999).ToString();
        _settingsDrawer.Controls.Add(_setAccountIdBox);
        _settingsDrawer.Controls.Add(genIdBtn);

        dy += 36;
        _minimizeOnStartCheck = new SpaceToggle
        {
            Location = new Point(22, dy),
            Size = new Size(350, 28),
            Text = "Сворачивать лаунчер при запуске",
            Checked = _settings.MinimizeOnStart,
        };
        _minimizeOnStartCheck.CheckedChanged += (_, _) => { _settings.MinimizeOnStart = _minimizeOnStartCheck.Checked; _settings.Save(); };

        dy += 30;
        _autoStopServerCheck = new SpaceToggle
        {
            Location = new Point(22, dy),
            Size = new Size(350, 28),
            Text = "Останавливать локальный сервер",
            Checked = _settings.AutoStopServer,
        };
        _autoStopServerCheck.CheckedChanged += (_, _) => { _settings.AutoStopServer = _autoStopServerCheck.Checked; _settings.Save(); };

        _settingsDrawer.Controls.Add(_minimizeOnStartCheck);
        _settingsDrawer.Controls.Add(_autoStopServerCheck);

        dy += 38;
        _settingsDrawer.Controls.Add(MakeDrawerSectionLabel("ПАПКА ИГРЫ И ОБНОВЛЕНИЯ", 22, dy));
        dy += 20;
        _setGameDirBox = MakeDarkInput(_settings.GameDir, 22, dy, 350);
        _settingsDrawer.Controls.Add(_setGameDirBox);

        dy += 32;
        var browseDirBtn = new SpaceButton
        {
            Location = new Point(22, dy),
            Size = new Size(172, 30),
            Text = "ИЗМЕНИТЬ ПАПКУ",
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            BorderColor = SpaceTheme.BorderBright,
        };
        browseDirBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Выберите папку игры", SelectedPath = _setGameDirBox.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _setGameDirBox.Text = dlg.SelectedPath;
        };
        var openDirBtn = new SpaceButton
        {
            Location = new Point(200, dy),
            Size = new Size(172, 30),
            Text = "ОТКРЫТЬ ПАПКУ",
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            BorderColor = SpaceTheme.BorderBright,
        };
        openDirBtn.Click += (_, _) => OpenGameDirectory();
        _settingsDrawer.Controls.Add(browseDirBtn);
        _settingsDrawer.Controls.Add(openDirBtn);

        dy += 38;
        _settingsDrawer.Controls.Add(MakeDrawerFieldHint("URL манифеста (manifest.json):", 22, dy));
        dy += 18;
        _setManifestUrlBox = MakeDarkInput(_settings.ManifestUrl, 22, dy, 350);
        _setManifestUrlBox.PlaceholderText = LauncherSettings.DefaultManifestUrl;
        _settingsDrawer.Controls.Add(_setManifestUrlBox);

        // Drawer Footer Buttons
        _setVerifyBtn = new SpaceButton
        {
            Location = new Point(22, 492),
            Size = new Size(146, 36),
            Text = "ПРОВЕРКА ФАЙЛОВ",
            Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
            BorderColor = SpaceTheme.BorderBright,
        };
        _setVerifyBtn.Click += async (_, _) =>
        {
            ToggleSettingsDrawer(false);
            await CheckUpdatesAsync(forceFullHash: true, promptSelfUpdate: true);
        };

        _setSaveBtn = new SpaceButton
        {
            Location = new Point(176, 492),
            Size = new Size(196, 36),
            Text = "СОХРАНИТЬ",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            GradientStart = SpaceTheme.AccentRedBright,
            GradientEnd = SpaceTheme.AccentRedDark,
            DrawGlow = true,
        };
        _setSaveBtn.Click += async (_, _) => await SaveSettingsFromDrawerAsync();

        _settingsDrawer.Controls.Add(_setVerifyBtn);
        _settingsDrawer.Controls.Add(_setSaveBtn);

        Controls.Add(_settingsDrawer);
        Controls.Add(_patchesOverlay);
        Controls.Add(_profileCard);
        Controls.Add(_progressBar);
        Controls.Add(_primaryButton);
        Controls.Add(_serverPillPanel);
        Controls.Add(_headerBar);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _pollTimer.Tick += async (_, _) => await PollTickAsync();

        _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animTimer.Tick += (_, _) => OnAnimationTick();

        Shown += async (_, _) => await OnFormShownAsync();
        FormClosing += OnFormClosing;
        MouseDown += (_, _) => { if (_settingsOpen) ToggleSettingsDrawer(false); };

        CollectAnimatables(this);
        TryApplyIcon();
        SetDrawerMode(_settings.UseLocalServer);
        UpdatePathAndOptionsUi();
        RefreshStatus();
        RenderComposedBackdropFrame();
        _animTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SpaceTheme.EnableWindowRoundedCorners(Handle);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_composedFrame != null)
        {
            e.Graphics.DrawImageUnscaled(_composedFrame, 0, 0);
            return;
        }
        using var fallback = new SolidBrush(SpaceTheme.BgWindow);
        e.Graphics.FillRectangle(fallback, ClientRectangle);
    }

    private void CollectAnimatables(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is IAnimatable anim) _animatables.Add(anim);
            if (c.HasChildren) CollectAnimatables(c);
        }
    }

    private void InitializeAnimatedBackdrop()
    {
        if (_backdropImage != null)
        {
            _backdropSource = new Bitmap(1068, 632, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(_backdropSource);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_backdropImage, BackdropRenderer.ComputeCoverRect(_backdropImage.Size, new Size(1068, 632)));
        }

        // Pre-bake sunset radial glow + god rays sprite (560x380) once at startup
        _sunOverlaySprite = new Bitmap(560, 380, PixelFormat.Format32bppPArgb);
        using (var sg = Graphics.FromImage(_sunOverlaySprite))
        {
            sg.SmoothingMode = SmoothingMode.AntiAlias;
            using var sunPath = new GraphicsPath();
            sunPath.AddEllipse(0f, 0f, 558f, 340f);
            using var sunBrush = new PathGradientBrush(sunPath)
            {
                CenterPoint = new PointF(330f, 155f),
                CenterColor = Color.FromArgb(64, 255, 142, 58),
                SurroundColors = new[] { Color.FromArgb(0, 255, 100, 30) },
            };
            sg.FillPath(sunBrush, sunPath);

            DrawAnimatedGodRay(sg, 310f, 145f, 35f, 375f, 75f, 28f);
            DrawAnimatedGodRay(sg, 340f, 150f, 200f, 375f, 90f, 24f);
            DrawAnimatedGodRay(sg, 380f, 155f, 435f, 375f, 65f, 22f);
        }

        // Pre-bake soft ember glow sprite (24x24) once at startup
        _emberSprite = new Bitmap(24, 24, PixelFormat.Format32bppPArgb);
        using (var eg = Graphics.FromImage(_emberSprite))
        {
            eg.SmoothingMode = SmoothingMode.AntiAlias;
            using var ep = new GraphicsPath();
            ep.AddEllipse(0, 0, 24, 24);
            using var eb = new PathGradientBrush(ep)
            {
                CenterPoint = new PointF(12f, 12f),
                CenterColor = Color.FromArgb(255, 255, 205, 125),
                SurroundColors = new[] { Color.FromArgb(0, 255, 95, 25) },
            };
            eg.FillPath(eb, ep);
        }

        // Pre-bake bottom dark vignette (1020x185) once at startup
        _vignetteSprite = new Bitmap(1020, 185, PixelFormat.Format32bppPArgb);
        using (var vg = Graphics.FromImage(_vignetteSprite))
        {
            var vigRect = new Rectangle(0, 0, 1020, 189);
            using var vb = new LinearGradientBrush(vigRect, Color.FromArgb(0, 8, 10, 14), Color.FromArgb(242, 8, 10, 14), 90f);
            vb.WrapMode = WrapMode.TileFlipXY;
            vg.FillRectangle(vb, 0, 0, 1020, 185);
        }

        _sunAttrs = new ImageAttributes();
        _emberAttrs = new ImageAttributes();
        _patchesAttrs = new ImageAttributes();
        _composedFrame = new Bitmap(1020, 600, PixelFormat.Format32bppPArgb);
        _drawerSlideSprite = new Bitmap(394, 544, PixelFormat.Format32bppPArgb);

        // Pre-bake full Patches overlay (dim + changelog cards) once so fade in/out runs at 60 FPS with zero flicker
        _patchesCardSprite = new Bitmap(1020, 434, PixelFormat.Format32bppPArgb);
        using (var pg = Graphics.FromImage(_patchesCardSprite))
        {
            using var dim = new SolidBrush(Color.FromArgb(232, 11, 13, 18));
            pg.FillRectangle(dim, 0, 0, 1020, 434);
            DrawChangelogContent(pg, 1f);
        }

        for (int i = 0; i < _embers.Length; i++)
        {
            _embers[i] = SpawnEmber(randomY: true);
        }
    }

    private static EmberParticle SpawnEmber(bool randomY)
    {
        float x = Random.Shared.Next(20, 1000);
        float y = randomY ? Random.Shared.Next(90, 595) : Random.Shared.Next(490, 608);
        bool bright = Random.Shared.NextDouble() > 0.32;
        return new EmberParticle
        {
            X = x,
            Y = y,
            Vx = 10f + (float)Random.Shared.NextDouble() * 24f,
            Vy = -(16f + (float)Random.Shared.NextDouble() * 28f),
            Size = bright ? (7f + (float)Random.Shared.NextDouble() * 7f) : (4f + (float)Random.Shared.NextDouble() * 4f),
            Alpha = bright ? (0.55f + (float)Random.Shared.NextDouble() * 0.4f) : (0.18f + (float)Random.Shared.NextDouble() * 0.22f),
            Phase = (float)(Random.Shared.NextDouble() * Math.PI * 2.0),
            Life = randomY ? (float)Random.Shared.NextDouble() * 5f : 0f,
            MaxLife = 4.2f + (float)Random.Shared.NextDouble() * 4.5f,
            IsOrange = bright,
        };
    }

    private void OnAnimationTick()
    {
        if (IsDisposed || WindowState == FormWindowState.Minimized || !Visible) return;

        float now = (float)_animClock.Elapsed.TotalSeconds;
        float dt = Math.Clamp(now - _lastAnimSec, 0.004f, 0.05f);
        _lastAnimSec = now;
        _animTime += dt;

        // Smooth cursor parallax inside launcher bounds
        Point cursor = PointToClient(Cursor.Position);
        float nx = Math.Clamp((cursor.X / Math.Max(1f, ClientSize.Width)) * 2f - 1f, -1f, 1f);
        float ny = Math.Clamp((cursor.Y / Math.Max(1f, ClientSize.Height)) * 2f - 1f, -1f, 1f);
        _parallaxX = BackdropRenderer.Approach(_parallaxX, nx * 15f, 5.5f, dt);
        _parallaxY = BackdropRenderer.Approach(_parallaxY, ny * 9f, 5.5f, dt);

        // Advance floating embers
        for (int i = 0; i < _embers.Length; i++)
        {
            ref var p = ref _embers[i];
            p.Life += dt;
            p.X += (p.Vx + MathF.Sin(_animTime * 1.9f + p.Phase) * 9f) * dt;
            p.Y += p.Vy * dt;
            if (p.Life >= p.MaxLife || p.Y < 52 || p.X > 1036)
            {
                _embers[i] = SpawnEmber(randomY: false);
            }
        }

        // Snapshot-based slide transition for Settings drawer (zero Win32 child HWND stutter -> locked 60 FPS)
        float targetDrawerX = _settingsOpen ? 626f : 1020f;
        bool drawerAnimating = MathF.Abs(_drawerX - targetDrawerX) >= 0.8f;
        if (drawerAnimating)
        {
            _drawerX = BackdropRenderer.Approach(_drawerX, targetDrawerX, 24f, dt);
            if (MathF.Abs(_drawerX - targetDrawerX) < 1.2f)
            {
                _drawerX = targetDrawerX;
                if (_settingsOpen)
                {
                    _settingsDrawer.Left = 626;
                    _settingsDrawer.Visible = true;
                    _settingsDrawer.BringToFront();
                }
            }
        }

        // Snapshot-based fade transition for Patches overlay (zero exit lag, zero child control flicker)
        float targetPatchesAlpha = _activeTab == LauncherTab.Patches ? 232f : 0f;
        bool patchesAnimating = MathF.Abs(_patchesAlpha - targetPatchesAlpha) >= 1f;
        if (patchesAnimating)
        {
            _patchesAlpha = BackdropRenderer.Approach(_patchesAlpha, targetPatchesAlpha, 22f, dt);
            if (MathF.Abs(_patchesAlpha - targetPatchesAlpha) < 1.5f)
            {
                _patchesAlpha = targetPatchesAlpha;
                if (_activeTab == LauncherTab.Patches)
                {
                    _patchesOverlay.GlassTint = Color.FromArgb(232, 11, 13, 18);
                    _patchesOverlay.Visible = true;
                    _patchesOverlay.BringToFront();
                    if (_settingsOpen) _settingsDrawer.BringToFront();
                }
            }
        }

        // Step per-control animations
        for (int i = 0; i < _animatables.Count; i++)
        {
            _animatables[i].StepAnimation(dt);
        }

        bool patchesFullOpaque = _activeTab == LauncherTab.Patches && !patchesAnimating && _patchesOverlay.Visible;
        if (!patchesFullOpaque)
        {
            RenderComposedBackdropFrame();
            Invalidate(false);
            _headerBar.Invalidate(false);
            _profileCard.Invalidate(false);
            _serverPillPanel.Invalidate(false);
        }
    }

    private void RenderComposedBackdropFrame()
    {
        if (_composedFrame == null) return;
        using var g = Graphics.FromImage(_composedFrame);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;

        if (_backdropSource != null)
        {
            int sx = Math.Clamp((int)Math.Round(24f + MathF.Sin(_animTime * 0.35f) * 8f + _parallaxX), 0, 48);
            int sy = Math.Clamp((int)Math.Round(16f + MathF.Cos(_animTime * 0.25f) * 5f + _parallaxY), 0, 32);
            g.DrawImage(_backdropSource, new Rectangle(0, 0, 1020, 600), sx, sy, 1020, 600, GraphicsUnit.Pixel);
        }
        else
        {
            using var bg = new SolidBrush(SpaceTheme.BgWindow);
            g.FillRectangle(bg, 0, 0, 1020, 600);
        }

        g.CompositingMode = CompositingMode.SourceOver;
        g.InterpolationMode = InterpolationMode.Bilinear;

        // Pre-baked sunset glow + god rays with breathing intensity
        if (_sunOverlaySprite != null && _sunAttrs != null)
        {
            float sunAlpha = 0.48f + 0.48f * (0.5f + 0.5f * MathF.Sin(_animTime * 0.9f));
            _sunAttrs.SetColorMatrix(new ColorMatrix { Matrix33 = sunAlpha });
            int sunX = 450 - (int)Math.Round(_parallaxX * 0.45f);
            int sunY = 48 - (int)Math.Round(_parallaxY * 0.45f);
            g.DrawImage(_sunOverlaySprite, new Rectangle(sunX, sunY, _sunOverlaySprite.Width, _sunOverlaySprite.Height),
                0, 0, _sunOverlaySprite.Width, _sunOverlaySprite.Height, GraphicsUnit.Pixel, _sunAttrs);
        }

        // Pre-baked soft ember particles
        if (_emberSprite != null && _emberAttrs != null)
        {
            var cm = new ColorMatrix();
            for (int i = 0; i < _embers.Length; i++)
            {
                ref readonly var p = ref _embers[i];
                float lifeRatio = p.Life / Math.Max(0.1f, p.MaxLife);
                float env = MathF.Sin(lifeRatio * MathF.PI);
                float flicker = 0.72f + 0.28f * MathF.Sin(_animTime * 7f + p.Phase);
                float a = Math.Clamp(p.Alpha * env * flicker, 0f, 1f);
                if (a <= 0.04f) continue;

                cm.Matrix33 = a;
                _emberAttrs.SetColorMatrix(cm);
                float px = p.X - _parallaxX * 0.9f - p.Size * 0.5f;
                float py = p.Y - _parallaxY * 0.9f - p.Size * 0.5f;
                g.DrawImage(_emberSprite, new Rectangle((int)px, (int)py, (int)Math.Ceiling(p.Size), (int)Math.Ceiling(p.Size)),
                    0, 0, _emberSprite.Width, _emberSprite.Height, GraphicsUnit.Pixel, _emberAttrs);
            }
        }

        // Pre-baked bottom vignette
        if (_vignetteSprite != null)
        {
            g.DrawImageUnscaled(_vignetteSprite, 0, 415);
        }

        // Smooth fading Patches overlay during transition (single sprite blit, zero child HWND stalls)
        if (_patchesAlpha > 1f && !_patchesOverlay.Visible && _patchesCardSprite != null && _patchesAttrs != null)
        {
            float pAlpha = Math.Clamp(_patchesAlpha / 232f, 0f, 1f);
            _patchesAttrs.SetColorMatrix(new ColorMatrix { Matrix33 = pAlpha });
            g.DrawImage(_patchesCardSprite, new Rectangle(0, 56, 1020, 434),
                0, 0, 1020, 434, GraphicsUnit.Pixel, _patchesAttrs);
        }

        // Smooth sliding Settings drawer snapshot during open/close slide
        if (_drawerSlideSprite != null && !_settingsDrawer.Visible && _drawerX < 1019f)
        {
            g.DrawImageUnscaled(_drawerSlideSprite, (int)Math.Round(_drawerX), 56);
        }
    }

    private static void DrawAnimatedGodRay(Graphics g, float originX, float originY, float endX, float endY, float halfSpread, float alpha)
    {
        int a = Math.Clamp((int)Math.Round(alpha), 0, 80);
        if (a <= 1) return;

        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(originX - 10, originY),
            new PointF(originX + 10, originY),
            new PointF(endX + halfSpread, endY),
            new PointF(endX - halfSpread, endY),
        });
        using var brush = new LinearGradientBrush(
            new PointF(originX, originY),
            new PointF(endX, endY),
            Color.FromArgb(a, 255, 175, 95),
            Color.FromArgb(0, 255, 120, 45));
        g.FillPath(brush, path);
    }

    private void PaintHeaderBrand(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var titleFont = new Font("Segoe UI", 10.2f, FontStyle.Bold);
        using var subFont = new Font("Segoe UI", 6.8f, FontStyle.Bold);

        const string part1 = "GROUND WAR ";
        const string part2 = "TANKS";
        var sz1 = TextRenderer.MeasureText(g, part1, titleFont, Size.Empty, TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, part1, titleFont, new Point(58, 11), SpaceTheme.TextPrimary, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, part2, titleFont, new Point(58 + sz1.Width, 11), SpaceTheme.AccentRedBright, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, $"LAUNCHER v{LauncherSettings.CurrentLauncherVersion}", subFont, new Point(59, 31),
            SpaceTheme.TextMuted, TextFormatFlags.NoPadding);
    }

    private void PaintPatchesOverlay(object? sender, PaintEventArgs e)
    {
        if (_showingLogInPatches) return;
        DrawChangelogContent(e.Graphics, 1f);
    }

    private static void DrawChangelogContent(Graphics g, float fade)
    {
        if (fade <= 0.02f) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int A(int max) => Math.Clamp((int)Math.Round(max * fade), 0, 255);

        using var subFont = new Font("Segoe UI", 8f, FontStyle.Bold);
        using var headFont = new Font("Segoe UI", 21f, FontStyle.Bold);
        using var verFont = new Font("Consolas", 10.5f, FontStyle.Bold);
        using var dateFont = new Font("Segoe UI", 8.5f);
        using var badgeFont = new Font("Segoe UI", 7.2f, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", 8.6f);

        TextRenderer.DrawText(g, "ИСТОРИЯ ОБНОВЛЕНИЙ", subFont, new Point(180, 22),
            Color.FromArgb(A(255), SpaceTheme.AccentRedBright), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "СПИСОК ИЗМЕНЕНИЙ", headFont, new Point(178, 40),
            Color.FromArgb(A(255), SpaceTheme.TextPrimary), TextFormatFlags.NoPadding);

        int y = 94;
        foreach (var entry in ChangelogEntries)
        {
            int cardH = 38 + entry.Bullets.Length * 21 + 10;
            var cardRect = new Rectangle(180, y, 660, cardH);

            using (var cardPath = SpaceTheme.CreateRoundedRect(cardRect, 8))
            using (var cardFill = new SolidBrush(Color.FromArgb(A(235), 20, 24, 34)))
            using (var cardPen = new Pen(Color.FromArgb(A(220), SpaceTheme.BorderSubtle), 1f))
            {
                g.FillPath(cardFill, cardPath);
                g.DrawPath(cardPen, cardPath);
            }

            TextRenderer.DrawText(g, entry.Version, verFont, new Point(196, y + 11),
                Color.FromArgb(A(255), SpaceTheme.TextPrimary), TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, entry.Date, dateFont, new Point(266, y + 13),
                Color.FromArgb(A(255), SpaceTheme.TextMuted), TextFormatFlags.NoPadding);

            var badgeRect = new Rectangle(744, y + 11, 80, 20);
            using (var badgePath = SpaceTheme.CreateRoundedRect(badgeRect, 5))
            using (var badgeBg = new SolidBrush(Color.FromArgb(A(220), 30, 36, 50)))
            {
                g.FillPath(badgeBg, badgePath);
            }
            TextRenderer.DrawText(g, entry.Badge, badgeFont, badgeRect,
                Color.FromArgb(A(255), entry.BadgeColor), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int by = y + 38;
            foreach (var b in entry.Bullets)
            {
                TextRenderer.DrawText(g, "•  " + b, bodyFont, new Rectangle(198, by, 626, 20),
                    Color.FromArgb(A(255), SpaceTheme.TextSecondary), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                by += 21;
            }

            y += cardH + 10;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var statusFont = new Font("Consolas", 8.2f, FontStyle.Bold);
        var captionRect = new Rectangle(340, 502, 340, 18);
        TextRenderer.DrawText(e.Graphics, _statusCaptionText, statusFont, captionRect,
            Color.FromArgb(225, 230, 240), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        using var border = new Pen(Color.FromArgb(70, 255, 255, 255), 1f);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void EnableDrag(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, HtCaption, 0);
        };
    }

    private void SwitchTab(LauncherTab tab)
    {
        if (_activeTab == tab) return;
        _activeTab = tab;
        _tabHome.IsActive = tab == LauncherTab.Home;
        _tabPatches.IsActive = tab == LauncherTab.Patches;
        if (tab == LauncherTab.Patches)
        {
            if (_settingsOpen) ToggleSettingsDrawer(false);
        }
        else
        {
            _patchesOverlay.Visible = false;
        }
    }

    private void ToggleSettingsDrawer(bool open)
    {
        if (_settingsOpen == open) return;
        _settingsOpen = open;
        if (open)
        {
            LoadSettingsInputsFromModel();
            if (_drawerSlideSprite != null)
            {
                _settingsDrawer.Left = 2000;
                _settingsDrawer.Visible = true;
                _settingsDrawer.PerformLayout();
                _settingsDrawer.DrawToBitmap(_drawerSlideSprite, new Rectangle(0, 0, 394, 544));
                _settingsDrawer.Visible = false;
            }
            if (!_settingsDrawer.Visible)
            {
                _drawerX = 1020f;
            }
        }
        else
        {
            if (_settingsDrawer.Visible && _drawerSlideSprite != null)
            {
                _settingsDrawer.DrawToBitmap(_drawerSlideSprite, new Rectangle(0, 0, 394, 544));
                _settingsDrawer.Visible = false;
                _drawerX = 626f;
            }
        }
        _headerSettingsBtn.IsSelected = open;
    }

    private void TogglePatchesConsoleView()
    {
        _showingLogInPatches = !_showingLogInPatches;
        _consoleHostPanel.Visible = _showingLogInPatches;
        _patchesModeToggleBtn.Text = _showingLogInPatches ? "СПИСОК ИЗМЕНЕНИЙ" : "ЖУРНАЛ ЛОГОВ";
        _patchesModeToggleBtn.IsSelected = _showingLogInPatches;
        _patchesOverlay.Invalidate(false);
    }

    private ContextMenuStrip BuildPlayDropdownMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = SpaceTheme.BgCard,
            ForeColor = SpaceTheme.TextPrimary,
            ShowImageMargin = false,
            Renderer = new SpaceMenuRenderer(),
        };

        var checkItem = new ToolStripMenuItem("Проверить целостность файлов");
        checkItem.Click += async (_, _) => await CheckUpdatesAsync(forceFullHash: true, promptSelfUpdate: true);

        var switchModeItem = new ToolStripMenuItem("Переключить Онлайн / Локальный сервер");
        switchModeItem.Click += async (_, _) =>
        {
            if (_detectedEmuDir == null && !_settings.UseLocalServer)
            {
                MessageBox.Show(this, "Локальный эмулятор не найден рядом с лаунчером.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _settings.UseLocalServer = !_settings.UseLocalServer;
            _settings.Save();
            SetDrawerMode(_settings.UseLocalServer);
            UpdatePathAndOptionsUi();
            await PingRemoteServerIfNeededAsync(force: true);
            RefreshStatus();
        };

        var serverCtrlItem = new ToolStripMenuItem("Остановить локальный сервер");
        serverCtrlItem.Click += (_, _) => StopServer();

        var folderItem = new ToolStripMenuItem("Открыть папку игры");
        folderItem.Click += (_, _) => OpenGameDirectory();

        var logItem = new ToolStripMenuItem("Открыть журнал операций");
        logItem.Click += (_, _) =>
        {
            SwitchTab(LauncherTab.Patches);
            if (!_showingLogInPatches) TogglePatchesConsoleView();
        };

        menu.Items.Add(checkItem);
        menu.Items.Add(switchModeItem);
        menu.Items.Add(serverCtrlItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(folderItem);
        menu.Items.Add(logItem);
        menu.Opening += (_, _) =>
        {
            bool localUp = (_serverProcess != null && !_serverProcess.HasExited) || IsPortOpen(_settings.GamePort);
            serverCtrlItem.Visible = _settings.UseLocalServer && localUp;
        };
        return menu;
    }

    private void ShowPlayDropdownMenu()
    {
        _playDropdownMenu.Show(_primaryButton, new Point(0, -_playDropdownMenu.PreferredSize.Height - 4));
    }

    private async Task OnFormShownAsync()
    {
        AppendLog($"[Launcher] Space Launcher v{LauncherSettings.CurrentLauncherVersion}");
        AppendLog(string.IsNullOrWhiteSpace(_settings.ManifestUrl)
            ? "[Launcher] URL манифеста не задан (работа в оффлайн/локальном режиме)."
            : $"[Launcher] Манифест: {_settings.ManifestUrl}");
        AppendLog(_settings.UseLocalServer
            ? "[Launcher] Режим: локальный сервер на этом ПК."
            : $"[Launcher] Режим: онлайн-сервер {_settings.ServerHost}:{_settings.GamePort}.");
        AppendLog($"[Launcher] Папка игры: {GameDir}");

        _pollTimer.Start();
        await PingRemoteServerIfNeededAsync(force: true);
        RefreshStatus();

        if (!string.IsNullOrWhiteSpace(_settings.ManifestUrl))
        {
            await CheckUpdatesAsync(forceFullHash: false, promptSelfUpdate: true);
        }
    }

    private async Task PrimaryButtonClickedAsync()
    {
        if (_isUpdating)
        {
            _updateCts?.Cancel();
            return;
        }
        if (_pendingUpdate != null && _pendingUpdate.FilesToDownload.Count > 0)
        {
            await DownloadPendingUpdatesAsync();
            return;
        }
        await LaunchGameAsync();
    }

    private async Task CheckUpdatesAsync(bool forceFullHash, bool promptSelfUpdate)
    {
        if (_isUpdating || _isLaunching) return;

        if (string.IsNullOrWhiteSpace(_settings.ManifestUrl))
        {
            MessageBox.Show(this,
                "Не указан URL манифеста обновлений (manifest.json).\nОткройте «Настройки» и вставьте ссылку.",
                "Манифест не настроен", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _isUpdating = true;
        _updateCts = new CancellationTokenSource();
        _progressBar.Visible = true;
        _progressBar.Value = 0;
        _statusCaptionText = "ПРОВЕРКА МАНИФЕСТА...";
        RefreshStatus();

        try
        {
            AppendLog($"[Updater] Проверка манифеста: {_settings.ManifestUrl}");
            var manifest = await UpdaterService.FetchManifestAsync(_settings.ManifestUrl, _updateCts.Token);

            if (!_settings.UseLocalServer && !string.IsNullOrWhiteSpace(manifest.ServerHost))
            {
                bool changed = !string.Equals(_settings.ServerHost, manifest.ServerHost.Trim(), StringComparison.OrdinalIgnoreCase)
                    || (manifest.GamePort > 0 && _settings.GamePort != manifest.GamePort)
                    || (manifest.AuthPort > 0 && _settings.AuthPort != manifest.AuthPort)
                    || (manifest.AssetPort > 0 && _settings.AssetPort != manifest.AssetPort);

                if (changed)
                {
                    _settings.ServerHost = manifest.ServerHost.Trim();
                    if (manifest.GamePort > 0) _settings.GamePort = manifest.GamePort;
                    if (manifest.AuthPort > 0) _settings.AuthPort = manifest.AuthPort;
                    if (manifest.AssetPort > 0) _settings.AssetPort = manifest.AssetPort;
                    _settings.Save();
                    UpdatePathAndOptionsUi();
                    _ = PingRemoteServerIfNeededAsync(force: true);
                    AppendLog($"[Updater] Адрес сервера обновлён из манифеста: {_settings.ServerHost}:{_settings.GamePort}");
                }
            }

            if (promptSelfUpdate &&
                UpdaterService.IsNewerVersion(manifest.LauncherVersion, LauncherSettings.CurrentLauncherVersion) &&
                !string.IsNullOrWhiteSpace(manifest.LauncherUrl))
            {
                var ans = MessageBox.Show(this,
                    $"Доступна новая версия лаунчера v{manifest.LauncherVersion} (у вас v{LauncherSettings.CurrentLauncherVersion}).\n" +
                    "Обновить лаунчер сейчас?",
                    "Обновление лаунчера", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ans == DialogResult.Yes)
                {
                    _statusCaptionText = $"ОБНОВЛЕНИЕ ЛАУНЧЕРА v{manifest.LauncherVersion}...";
                    await UpdaterService.ApplyLauncherSelfUpdateAsync(manifest.LauncherUrl, _updateCts.Token);
                    Close();
                    return;
                }
            }

            var progress = new Progress<UpdateProgress>(p =>
            {
                int pct = (int)Math.Clamp(p.CompletedBytes * 1000.0 / Math.Max(1, p.TotalBytes), 0, 1000);
                _progressBar.Value = pct;
                _statusCaptionText = $"ПРОВЕРКА {pct / 10:F0}% • {p.CompletedFiles}/{p.TotalFiles}";
            });

            var result = await UpdaterService.CheckForUpdatesAsync(
                manifest, GameDir, forceFullHash, progress, _updateCts.Token);

            _pendingUpdate = result;
            _installedGameVersion = result.LocalGameVersion;
            if (result.FilesToDownload.Count == 0)
            {
                _installedGameVersion = manifest.GameVersion;
                _progressBar.Visible = false;
                _statusCaptionText = $"ВЕРСИЯ: v{manifest.GameVersion}";
                AppendLog($"[Updater] Проверка завершена: все {manifest.Files.Count} файлов актуальны.");
            }
            else
            {
                double mb = result.TotalBytesToDownload / (1024.0 * 1024.0);
                _progressBar.Visible = false;
                _statusCaptionText = $"ДОСТУПНО v{manifest.GameVersion} • {mb:F1} МБ";
                AppendLog($"[Updater] Требуется загрузка: {result.FilesToDownload.Count} файлов ({mb:F1} МБ).");
            }
        }
        catch (OperationCanceledException)
        {
            _progressBar.Visible = false;
            _statusCaptionText = "ПРОВЕРКА ОТМЕНЕНА";
            AppendLog("[Updater] Проверка отменена.");
        }
        catch (Exception ex)
        {
            _progressBar.Visible = false;
            _statusCaptionText = $"ВЕРСИЯ: v{_installedGameVersion}";
            AppendLog($"[Updater] Ошибка проверки: {ex.Message}");
        }
        finally
        {
            _isUpdating = false;
            _updateCts?.Dispose();
            _updateCts = null;
            RefreshStatus();
        }
    }

    private async Task DownloadPendingUpdatesAsync()
    {
        if (_pendingUpdate == null || _pendingUpdate.FilesToDownload.Count == 0 || _isUpdating) return;

        _isUpdating = true;
        _updateCts = new CancellationTokenSource();
        _progressBar.Visible = true;
        _progressBar.Value = 0;
        RefreshStatus();

        try
        {
            var upd = _pendingUpdate;
            AppendLog($"[Updater] Начинаю скачивание {upd.FilesToDownload.Count} файлов...");
            var progress = new Progress<UpdateProgress>(p =>
            {
                int pct = (int)Math.Clamp(p.CompletedBytes * 1000.0 / Math.Max(1, p.TotalBytes), 0, 1000);
                _progressBar.Value = pct;
                double percent = pct / 10.0;
                double speedMb = p.BytesPerSecond > 0 ? p.BytesPerSecond / (1024.0 * 1024.0) : 0;
                _statusCaptionText = speedMb > 0
                    ? $"{percent:F0}% • v{upd.Manifest.GameVersion} ({speedMb:F1} МБ/с)"
                    : $"{percent:F0}% • v{upd.Manifest.GameVersion}";
            });

            await UpdaterService.DownloadUpdatesAsync(
                upd.Manifest, _settings.ManifestUrl, upd.FilesToDownload, GameDir, progress, _updateCts.Token);

            _installedGameVersion = upd.Manifest.GameVersion;
            _pendingUpdate = null;
            _progressBar.Value = 1000;
            _progressBar.Visible = false;
            _statusCaptionText = $"ВЕРСИЯ: v{upd.Manifest.GameVersion}";
            AppendLog($"[Updater] Загрузка завершена. Версия игры: {upd.Manifest.GameVersion}.");
            UpdatePathAndOptionsUi();
            TryApplyIcon();
        }
        catch (OperationCanceledException)
        {
            _progressBar.Visible = false;
            _statusCaptionText = "ЗАГРУЗКА ПРИОСТАНОВЛЕНА";
            AppendLog("[Updater] Скачивание отменено.");
        }
        catch (Exception ex)
        {
            _progressBar.Visible = false;
            _statusCaptionText = "ОШИБКА ЗАГРУЗКИ";
            AppendLog($"[Updater] Ошибка скачивания: {ex.Message}");
            MessageBox.Show(this, $"Ошибка при скачивании обновлений:\n{ex.Message}", "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _isUpdating = false;
            _updateCts?.Dispose();
            _updateCts = null;
            RefreshStatus();
        }
    }

    private async Task LaunchGameAsync()
    {
        if (_isLaunching) return;
        _isLaunching = true;
        RefreshStatus();

        try
        {
            if (!File.Exists(TankExePath))
            {
                MessageBox.Show(this, $"Не найден файл игры:\n{TankExePath}", "Ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsGameRunning())
            {
                AppendLog("[Launcher] Игра (Tank.exe) уже запущена.");
                return;
            }

            string targetHost = _settings.UseLocalServer ? "127.0.0.1" : _settings.ServerHost;
            ClientConfigHelper.ApplyServerEndpoint(
                GameDir, targetHost, _settings.GamePort, _settings.AuthPort, _settings.AssetPort);

            if (_settings.UseLocalServer)
            {
                var ready = await EnsureLocalServerStartedAsync();
                if (!ready)
                {
                    AppendLog("[Launcher] Локальный сервер ещё инициализируется, запускаю клиент параллельно...");
                }
            }
            else
            {
                var (reachable, rtt) = await PingTcpWithLatencyAsync(targetHost, _settings.GamePort, 1200);
                _remoteServerOnline = reachable;
                if (reachable) _remoteLatencyMs = rtt;
                if (!reachable)
                {
                    var ans = MessageBox.Show(this,
                        $"Сервер {targetHost}:{_settings.GamePort} сейчас не отвечает.\nВсё равно запустить клиент?",
                        "Сервер недоступен", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ans != DialogResult.Yes) return;
                }
            }

            if (Directory.Exists(Path.Combine(GameDir, "showcase_level0")))
            {
                RotateShowcase();
            }

            var clientLog = Path.Combine(GameDir, "Tank_Data", "output_log.txt");
            if (File.Exists(clientLog))
            {
                try { File.Delete(clientLog); } catch { }
            }

            var args = _settings.BuildLaunchArgs();
            AppendLog($"[Launcher] Запускаю Tank.exe -> {targetHost}:{_settings.GamePort} (Account ID={_settings.AccountId})");
            var psi = new ProcessStartInfo
            {
                FileName = TankExePath,
                Arguments = args,
                WorkingDirectory = GameDir,
                UseShellExecute = true,
            };
            _gameProcess = Process.Start(psi);
            if (_gameProcess != null)
            {
                _gameProcess.EnableRaisingEvents = true;
                _gameProcess.Exited += (_, _) => BeginInvoke(OnGameExited);
            }

            if (_minimizeOnStartCheck.Checked)
            {
                WindowState = FormWindowState.Minimized;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Launcher] Ошибка запуска: {ex.Message}");
            MessageBox.Show(this, $"Не удалось запустить игру:\n{ex.Message}", "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _isLaunching = false;
            RefreshStatus();
        }
    }

    private void SetDrawerMode(bool useLocal)
    {
        _setModeOnlineBtn.IsSelected = !useLocal;
        _setModeLocalBtn.IsSelected = useLocal;
        bool online = !useLocal;
        _setServerHostBox.Enabled = online;
        _setGamePortNum.Enabled = online;
        _setAuthPortNum.Enabled = online;
        _setAssetPortNum.Enabled = online;
    }

    private void LoadSettingsInputsFromModel()
    {
        _setGameDirBox.Text = _settings.GameDir;
        _setManifestUrlBox.Text = _settings.ManifestUrl;
        _setServerHostBox.Text = _settings.ServerHost;
        _setGamePortNum.Value = Math.Clamp(_settings.GamePort > 0 ? _settings.GamePort : 9801, 1, 65535);
        _setAuthPortNum.Value = Math.Clamp(_settings.AuthPort > 0 ? _settings.AuthPort : 18080, 1, 65535);
        _setAssetPortNum.Value = Math.Clamp(_settings.AssetPort > 0 ? _settings.AssetPort : 18081, 1, 65535);
        _setAccountIdBox.Text = _settings.AccountId;
        SetDrawerMode(_settings.UseLocalServer);
    }

    private async Task SaveSettingsFromDrawerAsync()
    {
        if (_isUpdating || _isLaunching) return;
        if (string.IsNullOrWhiteSpace(_setGameDirBox.Text))
        {
            MessageBox.Show(this, "Укажите папку установки игры.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string prevManifest = _settings.ManifestUrl;
        string prevDir = _settings.GameDir;

        _settings.GameDir = _setGameDirBox.Text.Trim();
        _settings.ManifestUrl = _setManifestUrlBox.Text.Trim();
        _settings.UseLocalServer = _setModeLocalBtn.IsSelected;
        _settings.ServerHost = _settings.UseLocalServer
            ? "127.0.0.1"
            : (string.IsNullOrWhiteSpace(_setServerHostBox.Text) ? LauncherSettings.DefaultOnlineServerHost : _setServerHostBox.Text.Trim());
        _settings.GamePort = (int)_setGamePortNum.Value;
        _settings.AuthPort = (int)_setAuthPortNum.Value;
        _settings.AssetPort = (int)_setAssetPortNum.Value;
        _settings.AccountId = string.IsNullOrWhiteSpace(_setAccountIdBox.Text)
            ? Random.Shared.Next(100_000, 9_999_999).ToString()
            : _setAccountIdBox.Text.Trim();
        _settings.Save();
        _pendingUpdate = null;

        UpdatePathAndOptionsUi();
        await PingRemoteServerIfNeededAsync(force: true);
        RefreshStatus();
        ToggleSettingsDrawer(false);
        AppendLog("[Settings] Настройки сохранены.");

        bool manifestChanged = !string.Equals(prevManifest, _settings.ManifestUrl, StringComparison.OrdinalIgnoreCase);
        bool dirChanged = !string.Equals(prevDir, _settings.GameDir, StringComparison.OrdinalIgnoreCase);
        if ((manifestChanged || dirChanged) && !string.IsNullOrWhiteSpace(_settings.ManifestUrl))
        {
            await CheckUpdatesAsync(forceFullHash: false, promptSelfUpdate: true);
        }
    }

    private void OpenGameDirectory()
    {
        try
        {
            if (Directory.Exists(GameDir))
                Process.Start(new ProcessStartInfo { FileName = GameDir, UseShellExecute = true });
            else
                MessageBox.Show(this, $"Папка игры пока не создана:\n{GameDir}", "Папка не найдена", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"[Launcher] Не удалось открыть папку: {ex.Message}");
        }
    }

    private async Task<bool> EnsureLocalServerStartedAsync()
    {
        if (IsPortOpen(_settings.GamePort) || (_serverProcess != null && !_serverProcess.HasExited))
        {
            AppendLog("[Launcher] Локальный сервер уже запущен.");
            return await WaitForPortsAsync(2000, _settings.GamePort, _settings.AuthPort);
        }

        var serverExe = FindServerExe();
        if (serverExe == null && _detectedEmuDir == null)
        {
            throw new DirectoryNotFoundException("Локальный эмулятор GwtEmulator не найден рядом с лаунчером.");
        }

        string emuDir = _detectedEmuDir!;
        ProcessStartInfo psi;
        if (serverExe != null)
        {
            AppendLog($"[Launcher] Запускаю сервер: {Path.GetFileName(serverExe)}");
            psi = new ProcessStartInfo
            {
                FileName = serverExe,
                WorkingDirectory = emuDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
        }
        else
        {
            var appProj = Path.Combine(emuDir, "GwtEmulator.App");
            if (!Directory.Exists(appProj))
                throw new DirectoryNotFoundException($"Не найден проект эмулятора: {appProj}");
            AppendLog("[Launcher] Бинарник сервера не найден — запускаю через dotnet run...");
            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{appProj}\"",
                WorkingDirectory = emuDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
        }

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null && !IsDisposed) BeginInvoke(() => AppendLog(e.Data)); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null && !IsDisposed) BeginInvoke(() => AppendLog("[ERR] " + e.Data)); };
        proc.Exited += (_, _) => { if (!IsDisposed) BeginInvoke(() => { AppendLog("[Launcher] Процесс сервера завершён."); RefreshStatus(); }); };

        if (!proc.Start()) throw new InvalidOperationException("Не удалось запустить процесс сервера.");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _serverProcess = proc;
        _serverStartedByLauncher = true;

        return await WaitForPortsAsync(5000, _settings.GamePort, _settings.AuthPort);
    }

    private string? FindServerExe()
    {
        if (_detectedEmuDir == null) return null;
        return new[]
        {
            Path.Combine(_detectedEmuDir, "GwtEmulator.App", "bin", "Release", "net8.0", "GwtEmulator.App.exe"),
            Path.Combine(_detectedEmuDir, "GwtEmulator.App", "bin", "Debug", "net8.0", "GwtEmulator.App.exe"),
        }
        .Where(File.Exists)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
    }

    private static (string? gameDir, string? emuDir) ResolveLocalDirectories()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var searchRoots = new List<string> { baseDir };
        var parent = Directory.GetParent(baseDir);
        for (int i = 0; i < 4 && parent != null; i++)
        {
            searchRoots.Add(parent.FullName);
            parent = parent.Parent;
        }

        string? foundGame = null;
        string? foundEmu = null;
        foreach (var r in searchRoots)
        {
            if (foundGame == null)
            {
                if (File.Exists(Path.Combine(r, "Tank.exe"))) foundGame = r;
                else if (File.Exists(Path.Combine(r, "Ground War Tanks", "Tank.exe")))
                    foundGame = Path.Combine(r, "Ground War Tanks");
            }
            if (foundEmu == null)
            {
                if (Directory.Exists(Path.Combine(r, "GwtEmulator.App"))) foundEmu = r;
                else if (Directory.Exists(Path.Combine(r, "GwtEmulator", "GwtEmulator.App")))
                    foundEmu = Path.Combine(r, "GwtEmulator");
            }
        }
        return (foundGame, foundEmu);
    }

    private void TryApplyIcon()
    {
        try
        {
            var iconPath = Path.Combine(GameDir, "GameIcon.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
                var bmp = new Icon(iconPath, 48, 48).ToBitmap();
                _brandIconBox.Image = bmp;
                _profileIconBox.Image = bmp;
                return;
            }
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                using var exeIcon = Icon.ExtractAssociatedIcon(exePath);
                if (exeIcon != null)
                {
                    Icon = (Icon)exeIcon.Clone();
                    var bmp = exeIcon.ToBitmap();
                    _brandIconBox.Image = bmp;
                    _profileIconBox.Image = bmp;
                }
            }
        }
        catch { }
    }

    private static Image? LoadEmbeddedBannerImage()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("hero_banner.jpg", StringComparison.OrdinalIgnoreCase));
            if (resName == null) return null;
            using var s = asm.GetManifestResourceStream(resName);
            return s != null ? Image.FromStream(s) : null;
        }
        catch
        {
            return null;
        }
    }

    private void UpdatePathAndOptionsUi()
    {
        _profileNameLabel.Text = $"Player_{_settings.AccountId}";
        _autoStopServerCheck.Visible = _settings.UseLocalServer;
    }

    private async Task PollTickAsync()
    {
        await PingRemoteServerIfNeededAsync(force: false);
        RefreshStatus();
    }

    private async Task PingRemoteServerIfNeededAsync(bool force)
    {
        if (_settings.UseLocalServer) return;
        if (!force && IsGameRunning()) return;
        long now = Environment.TickCount64;
        if (!force && now - _lastRemotePingMs < 15000) return;
        _lastRemotePingMs = now;
        var (ok, ms) = await PingTcpWithLatencyAsync(_settings.ServerHost, _settings.GamePort, 750);
        _remoteServerOnline = ok;
        _remoteLatencyMs = ok ? ms : -1;
    }

    private static async Task<(bool ok, int ms)> PingTcpWithLatencyAsync(string host, int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await tcp.ConnectAsync(host.Trim(), port, cts.Token);
            sw.Stop();
            return (tcp.Connected, (int)Math.Max(1, sw.ElapsedMilliseconds));
        }
        catch
        {
            return (false, -1);
        }
    }

    private static async Task<bool> WaitForPortsAsync(int timeoutMs, params int[] ports)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (ArePortsListening(ports)) return true;
            await Task.Delay(50);
        }
        return ArePortsListening(ports);
    }

    private static bool ArePortsListening(params int[] ports)
    {
        try
        {
            var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            foreach (int p in ports)
                if (!active.Any(ep => ep.Port == p)) return false;
            return true;
        }
        catch { return false; }
    }

    private bool IsPortOpen(int port) => ArePortsListening(port);
    private static bool IsGameRunning() => Process.GetProcessesByName("Tank").Length > 0;

    private void OnGameExited()
    {
        AppendLog("[Launcher] Игра закрыта.");
        if (_settings.UseLocalServer && _autoStopServerCheck.Checked && _serverStartedByLauncher)
        {
            StopServer();
        }
        RefreshStatus();
    }

    private void StopServer()
    {
        try
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                AppendLog("[Launcher] Останавливаю сервер...");
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.WaitForExit(2000);
                _serverProcess = null;
                _serverStartedByLauncher = false;
                return;
            }
            foreach (var p in Process.GetProcessesByName("GwtEmulator.App"))
            {
                AppendLog("[Launcher] Останавливаю процесс GwtEmulator.App...");
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Launcher] Ошибка остановки сервера: {ex.Message}");
        }
        finally
        {
            RefreshStatus();
        }
    }

    private void RotateShowcase()
    {
        try
        {
            var variants = new (string file, int tankId)[]
            {
                ("level0_197_Chieftain", 197), ("level0_149_Obj279", 149),
                ("level0_137_Object140", 137), ("level0_163_FV214", 163),
                ("level0_168_1K17", 168),      ("level0_174_FV4030", 174),
                ("level0_179_Object263", 179), ("level0_208_XM815_Chrysler", 208),
            };
            var showcaseDir = Path.Combine(GameDir, "showcase_level0");
            var level0 = Path.Combine(GameDir, "Tank_Data", "level0");
            var orig = Path.Combine(GameDir, "Tank_Data", "level0.orig");
            if (File.Exists(level0) && !File.Exists(orig)) File.Copy(level0, orig);

            var idxFile = Path.Combine(showcaseDir, ".idx");
            int idx = 0;
            if (File.Exists(idxFile) && int.TryParse(File.ReadAllText(idxFile).Trim(), out var parsed))
                idx = Math.Abs(parsed) % variants.Length;

            var pick = variants[idx];
            var pickPath = Path.Combine(showcaseDir, pick.file);
            if (File.Exists(pickPath))
            {
                File.Copy(pickPath, level0, overwrite: true);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "gwt_mainprize.txt"), pick.tankId + Environment.NewLine);
                File.WriteAllText(idxFile, ((idx + 1) % variants.Length).ToString());
                AppendLog($"[Showcase] Витрина -> {pick.file} (id={pick.tankId})");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Showcase] Не удалось обновить витрину: {ex.Message}");
        }
    }

    private void RefreshStatus()
    {
        bool localServerUp = (_serverProcess != null && !_serverProcess.HasExited) || IsPortOpen(_settings.GamePort);
        bool serverUp = _settings.UseLocalServer ? localServerUp : _remoteServerOnline;
        bool gameInstalled = File.Exists(TankExePath);
        bool gameUp = (_gameProcess != null && !_gameProcess.HasExited) || IsGameRunning();

        if (_settings.UseLocalServer)
        {
            _profileStatusLabel.Text = localServerUp ? "● Локально • Работает" : "● Локальный режим";
            _profileStatusLabel.ForeColor = localServerUp ? SpaceTheme.AccentGreen : SpaceTheme.AccentAmber;
            _serverPillLabel.Text = localServerUp
                ? $"● ЛОКАЛЬНО • 127.0.0.1:{_settings.GamePort}"
                : $"● ЛОКАЛЬНО • 127.0.0.1:{_settings.GamePort}";
            _serverPillLabel.ForeColor = localServerUp ? SpaceTheme.AccentGreen : SpaceTheme.AccentAmber;
        }
        else
        {
            _profileStatusLabel.Text = _remoteServerOnline
                ? $"● Онлайн • {_remoteLatencyMs} мс"
                : "● Нет ответа сервера";
            _profileStatusLabel.ForeColor = _remoteServerOnline ? SpaceTheme.AccentGreen : SpaceTheme.AccentRedBright;
            _serverPillLabel.Text = _remoteServerOnline
                ? $"● {_settings.ServerHost}:{_settings.GamePort} • {_remoteLatencyMs} мс"
                : $"● {_settings.ServerHost}:{_settings.GamePort} • ОФФЛАЙН";
            _serverPillLabel.ForeColor = _remoteServerOnline ? SpaceTheme.AccentGreen : SpaceTheme.TextSecondary;
        }

        if (!_isUpdating)
        {
            if (!gameInstalled)
                _statusCaptionText = "КЛИЕНТ НЕ УСТАНОВЛЕН";
            else if (gameUp)
                _statusCaptionText = $"В ИГРЕ • v{_installedGameVersion}";
            else if (_pendingUpdate != null && _pendingUpdate.FilesToDownload.Count > 0)
                _statusCaptionText = $"ДОСТУПНО v{_pendingUpdate.Manifest.GameVersion}";
            else
                _statusCaptionText = $"ВЕРСИЯ: v{_installedGameVersion}";
        }

        if (_isUpdating)
        {
            _primaryButton.Enabled = true;
            _primaryButton.Text = "ОТМЕНА";
            _primaryButton.GradientStart = SpaceTheme.AccentRedBright;
            _primaryButton.GradientEnd = SpaceTheme.AccentRedDark;
        }
        else if (gameUp)
        {
            _primaryButton.Enabled = false;
            _primaryButton.Text = "ИГРА ЗАПУЩЕНА";
        }
        else if (_isLaunching)
        {
            _primaryButton.Enabled = false;
            _primaryButton.Text = "ЗАПУСК...";
            _primaryButton.GradientStart = SpaceTheme.AccentRedBright;
            _primaryButton.GradientEnd = SpaceTheme.AccentRedDark;
        }
        else if (_pendingUpdate != null && _pendingUpdate.FilesToDownload.Count > 0)
        {
            double mb = _pendingUpdate.TotalBytesToDownload / (1024.0 * 1024.0);
            _primaryButton.Enabled = true;
            _primaryButton.Text = gameInstalled ? $"ОБНОВИТЬ ({mb:F0} МБ)" : $"УСТАНОВИТЬ ({mb:F0} МБ)";
            _primaryButton.GradientStart = SpaceTheme.AccentRedBright;
            _primaryButton.GradientEnd = SpaceTheme.AccentRedDark;
        }
        else if (!gameInstalled)
        {
            _primaryButton.Enabled = true;
            _primaryButton.Text = "УСТАНОВИТЬ";
            _primaryButton.GradientStart = SpaceTheme.AccentRedBright;
            _primaryButton.GradientEnd = SpaceTheme.AccentRedDark;
        }
        else
        {
            _primaryButton.Enabled = true;
            _primaryButton.Text = "ИГРАТЬ";
            _primaryButton.GradientStart = SpaceTheme.AccentRedBright;
            _primaryButton.GradientEnd = SpaceTheme.AccentRedDark;
        }

        _setVerifyBtn.Enabled = !_isUpdating && !_isLaunching && !gameUp;
        _setSaveBtn.Enabled = !_isUpdating && !_isLaunching;
    }

    private void AppendLog(string line)
    {
        if (_logBox.TextLength > 32000) _logBox.Text = _logBox.Text[^16000..];
        _logBox.AppendText(line + Environment.NewLine);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _animTimer.Stop();
        _pollTimer.Stop();
        _updateCts?.Cancel();
        if (_serverProcess != null && !_serverProcess.HasExited && _serverStartedByLauncher)
        {
            try { _serverProcess.Kill(entireProcessTree: true); } catch { }
        }
        _backdropSource?.Dispose();
        _sunOverlaySprite?.Dispose();
        _emberSprite?.Dispose();
        _vignetteSprite?.Dispose();
        _patchesCardSprite?.Dispose();
        _drawerSlideSprite?.Dispose();
        _composedFrame?.Dispose();
        _sunAttrs?.Dispose();
        _emberAttrs?.Dispose();
        _patchesAttrs?.Dispose();
    }

    private static Label MakeDrawerSectionLabel(string text, int x, int y) => new()
    {
        Location = new Point(x, y),
        Size = new Size(340, 16),
        Text = text,
        Font = new Font("Segoe UI", 7.8f, FontStyle.Bold),
        ForeColor = SpaceTheme.AccentRedBright,
        BackColor = Color.Transparent,
    };

    private static Label MakeDrawerFieldHint(string text, int x, int y) => new()
    {
        Location = new Point(x, y),
        Size = new Size(348, 16),
        Text = text,
        Font = new Font("Segoe UI", 8f),
        ForeColor = SpaceTheme.TextSecondary,
        BackColor = Color.Transparent,
    };

    private static TextBox MakeDarkInput(string text, int x, int y, int width) => new()
    {
        Location = new Point(x, y),
        Size = new Size(width, 26),
        Text = text,
        Font = new Font("Consolas", 8.8f),
        BackColor = SpaceTheme.BgInput,
        ForeColor = SpaceTheme.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static NumericUpDown MakeDarkPort(int val, int fallback, int x, int y, int width) => new()
    {
        Location = new Point(x, y),
        Size = new Size(width, 26),
        Minimum = 1,
        Maximum = 65535,
        Value = Math.Clamp(val > 0 ? val : fallback, 1, 65535),
        Font = new Font("Consolas", 8.8f),
        BackColor = SpaceTheme.BgInput,
        ForeColor = SpaceTheme.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private sealed class SpaceMenuRenderer : ToolStripProfessionalRenderer
    {
        public SpaceMenuRenderer() : base(new SpaceColorTable()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : SpaceTheme.TextPrimary;
            base.OnRenderItemText(e);
        }
    }

    private sealed class SpaceColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => SpaceTheme.BgCard;
        public override Color MenuBorder => SpaceTheme.BorderBright;
        public override Color MenuItemBorder => SpaceTheme.AccentRed;
        public override Color MenuItemSelected => SpaceTheme.AccentRedDark;
        public override Color SeparatorDark => SpaceTheme.BorderSubtle;
        public override Color SeparatorLight => SpaceTheme.BorderSubtle;
    }
}
