using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GwtLauncher;

public sealed class LauncherForm : Form
{
    private readonly string? _detectedEmuDir;
    private LauncherSettings _settings;

    private readonly Label _subtitleLabel;
    private readonly Label _serverStatusLabel;
    private readonly Label _gameStatusLabel;
    private readonly Label _pathInfoLabel;
    private readonly ProgressBar _progressBar;
    private readonly Label _progressLabel;
    private readonly Button _primaryButton;
    private readonly Button _verifyButton;
    private readonly Button _settingsButton;
    private readonly Button _stopServerButton;
    private readonly CheckBox _autoStopServerCheck;
    private readonly CheckBox _minimizeOnStartCheck;
    private readonly CheckBox _rotateShowcaseCheck;
    private readonly TextBox _logBox;
    private readonly System.Windows.Forms.Timer _pollTimer;

    private Process? _serverProcess;
    private Process? _gameProcess;
    private bool _serverStartedByLauncher;
    private bool _isLaunching;
    private bool _isUpdating;
    private bool _remoteServerOnline;
    private long _lastRemotePingMs;
    private CancellationTokenSource? _updateCts;
    private UpdateCheckResult? _pendingUpdate;

    private string GameDir => _settings.GameDir;
    private string TankExePath => Path.Combine(GameDir, "Tank.exe");

    public LauncherForm()
    {
        var (detectedGame, detectedEmu) = ResolveLocalDirectories();
        _detectedEmuDir = detectedEmu;
        _settings = LauncherSettings.Load(detectedGame, detectedEmu);

        Text = $"Ground War Tanks — Лаунчер v{LauncherSettings.CurrentLauncherVersion}";
        ClientSize = new Size(690, 520);
        MinimumSize = new Size(610, 460);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 27, 32);
        ForeColor = Color.FromArgb(232, 236, 242);
        Font = new Font("Segoe UI", 9.5f);

        TryApplyIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 6,
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Header
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Status card
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Progress
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Options
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // Log

        // ── Header ───────────────────────────────────────────────────────────
        var headerPanel = new Panel { Dock = DockStyle.Top, Height = 58, Margin = new Padding(0, 0, 0, 10) };
        PictureBox? logo = null;
        if (Icon != null)
        {
            logo = new PictureBox
            {
                Image = Icon.ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(0, 4),
                Size = new Size(48, 48),
            };
            headerPanel.Controls.Add(logo);
        }
        int titleX = logo != null ? 60 : 0;
        var titleLabel = new Label
        {
            Text = "GROUND WAR TANKS",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(245, 200, 92),
            AutoSize = true,
            Location = new Point(titleX, 2),
        };
        _subtitleLabel = new Label
        {
            Text = BuildSubtitleText(),
            Font = new Font("Segoe UI", 9.25f),
            ForeColor = Color.FromArgb(165, 174, 188),
            AutoSize = true,
            Location = new Point(titleX, 32),
        };
        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(_subtitleLabel);
        root.Controls.Add(headerPanel, 0, 0);

        // ── Status card ──────────────────────────────────────────────────────
        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.FromArgb(33, 37, 44),
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 0, 0, 10),
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        _serverStatusLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(180, 186, 196),
            Text = "Сервер: проверка...",
            Margin = new Padding(0, 0, 8, 4),
        };
        _gameStatusLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(180, 186, 196),
            Text = "Клиент: проверка...",
            Margin = new Padding(8, 0, 0, 4),
        };
        _pathInfoLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(135, 144, 158),
            Font = new Font("Segoe UI", 8.25f),
        };
        statusPanel.Controls.Add(_serverStatusLabel, 0, 0);
        statusPanel.Controls.Add(_gameStatusLabel, 1, 0);
        statusPanel.Controls.Add(_pathInfoLabel, 0, 1);
        statusPanel.SetColumnSpan(_pathInfoLabel, 2);
        root.Controls.Add(statusPanel, 0, 1);

        // ── Update Progress ──────────────────────────────────────────────────
        var progressPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 10),
        };
        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 16,
            Minimum = 0,
            Maximum = 1000,
            Style = ProgressBarStyle.Continuous,
        };
        _progressLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(165, 174, 188),
            Font = new Font("Segoe UI", 8.5f),
            Text = "Готов к проверке обновлений",
            Margin = new Padding(0, 4, 0, 0),
        };
        progressPanel.Controls.Add(_progressBar, 0, 0);
        progressPanel.Controls.Add(_progressLabel, 0, 1);
        root.Controls.Add(progressPanel, 0, 2);

        // ── Action buttons ───────────────────────────────────────────────────
        var btnPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 10),
        };
        btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48f));
        btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19f));
        btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16f));
        btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17f));

        _primaryButton = MakeActionButton("ИГРАТЬ", Color.FromArgb(46, 148, 79), 12.5f, new Padding(0, 0, 6, 0));
        _primaryButton.Click += async (_, _) => await PrimaryButtonClickedAsync();

        _verifyButton = MakeActionButton("Проверить файлы", Color.FromArgb(55, 84, 128), 9f, new Padding(4, 0, 4, 0));
        _verifyButton.Click += async (_, _) => await CheckUpdatesAsync(forceFullHash: true, promptSelfUpdate: true);

        _settingsButton = MakeActionButton("Настройки", Color.FromArgb(62, 68, 79), 9f, new Padding(4, 0, 4, 0));
        _settingsButton.Click += async (_, _) => await OpenSettingsAsync();

        _stopServerButton = MakeActionButton("Стоп сервер", Color.FromArgb(62, 68, 79), 9f, new Padding(4, 0, 0, 0));
        _stopServerButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(168, 56, 56);
        _stopServerButton.Click += (_, _) => StopServer();

        btnPanel.Controls.Add(_primaryButton, 0, 0);
        btnPanel.Controls.Add(_verifyButton, 1, 0);
        btnPanel.Controls.Add(_settingsButton, 2, 0);
        btnPanel.Controls.Add(_stopServerButton, 3, 0);
        root.Controls.Add(btnPanel, 0, 3);

        // ── Options ──────────────────────────────────────────────────────────
        var optionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 10),
        };
        _autoStopServerCheck = new CheckBox
        {
            AutoSize = true,
            Checked = _settings.AutoStopServer,
            Text = "Автоматически останавливать локальный сервер после закрытия игры",
            ForeColor = Color.FromArgb(208, 214, 224),
        };
        _autoStopServerCheck.CheckedChanged += (_, _) =>
        {
            _settings.AutoStopServer = _autoStopServerCheck.Checked;
            _settings.Save();
        };

        _minimizeOnStartCheck = new CheckBox
        {
            AutoSize = true,
            Checked = _settings.MinimizeOnStart,
            Text = "Сворачивать лаунчер в панель задач после запуска игры",
            ForeColor = Color.FromArgb(208, 214, 224),
        };
        _minimizeOnStartCheck.CheckedChanged += (_, _) =>
        {
            _settings.MinimizeOnStart = _minimizeOnStartCheck.Checked;
            _settings.Save();
        };

        _rotateShowcaseCheck = new CheckBox
        {
            AutoSize = true,
            Checked = _settings.RotateShowcase,
            Text = "Менять витрину «Золотого запаса» перед запуском (showcase_level0)",
            ForeColor = Color.FromArgb(208, 214, 224),
        };
        _rotateShowcaseCheck.CheckedChanged += (_, _) =>
        {
            _settings.RotateShowcase = _rotateShowcaseCheck.Checked;
            _settings.Save();
        };

        optionsPanel.Controls.Add(_autoStopServerCheck);
        optionsPanel.Controls.Add(_minimizeOnStartCheck);
        optionsPanel.Controls.Add(_rotateShowcaseCheck);
        root.Controls.Add(optionsPanel, 0, 4);

        // ── Log box ──────────────────────────────────────────────────────────
        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(16, 18, 22),
            ForeColor = Color.FromArgb(190, 205, 195),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.75f),
            WordWrap = true,
        };
        root.Controls.Add(_logBox, 0, 5);

        Controls.Add(root);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _pollTimer.Tick += async (_, _) => await PollTickAsync();
        _pollTimer.Start();

        FormClosing += OnFormClosing;
        Shown += async (_, _) => await OnFormShownAsync();

        UpdatePathAndOptionsUi();
        AppendLog($"[Launcher] Версия {LauncherSettings.CurrentLauncherVersion} готова.");
        AppendLog($"[Launcher] Папка игры: {GameDir}");
        RefreshStatus();
    }

    private async Task OnFormShownAsync()
    {
        await PingRemoteServerIfNeededAsync(force: true);
        if (!string.IsNullOrWhiteSpace(_settings.ManifestUrl))
        {
            await CheckUpdatesAsync(forceFullHash: false, promptSelfUpdate: true);
        }
        else if (!File.Exists(TankExePath))
        {
            _progressLabel.Text = "Игра не найдена. Откройте «Настройки» и укажите URL manifest.json или папку игры.";
            AppendLog("[Launcher] ManifestUrl не задан. Задайте его в Настройках или в launcher_config.json.");
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

        if (!File.Exists(TankExePath) && !string.IsNullOrWhiteSpace(_settings.ManifestUrl))
        {
            await CheckUpdatesAsync(forceFullHash: false, promptSelfUpdate: true);
            if (_pendingUpdate != null && _pendingUpdate.FilesToDownload.Count > 0)
            {
                await DownloadPendingUpdatesAsync();
            }
            if (!File.Exists(TankExePath)) return;
        }

        await LaunchGameAsync();
    }

    private async Task CheckUpdatesAsync(bool forceFullHash, bool promptSelfUpdate)
    {
        if (_isUpdating || _isLaunching) return;
        if (string.IsNullOrWhiteSpace(_settings.ManifestUrl))
        {
            MessageBox.Show(this,
                "Не указан URL манифеста обновлений (manifest.json).\nУкажите его в Настройках.",
                "Обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _isUpdating = true;
        _updateCts = new CancellationTokenSource();
        RefreshStatus();

        try
        {
            _progressBar.Value = 0;
            _progressLabel.Text = "Загрузка manifest.json...";
            AppendLog($"[Updater] Проверка обновлений: {_settings.ManifestUrl}");

            var manifest = await UpdaterService.FetchManifestAsync(_settings.ManifestUrl, _updateCts.Token);

            // Подхватываем адрес онлайн-сервера из манифеста, если он задан
            if (!string.IsNullOrWhiteSpace(manifest.ServerHost)
                && !LauncherSettings.IsLocalHost(manifest.ServerHost)
                && !_settings.UseLocalServer)
            {
                _settings.ServerHost = manifest.ServerHost.Trim();
                if (manifest.GamePort > 0) _settings.GamePort = manifest.GamePort;
                if (manifest.AuthPort > 0) _settings.AuthPort = manifest.AuthPort;
                if (manifest.AssetPort > 0) _settings.AssetPort = manifest.AssetPort;
                _settings.Save();
                UpdatePathAndOptionsUi();
            }

            if (promptSelfUpdate
                && UpdaterService.IsNewerVersion(manifest.LauncherVersion, LauncherSettings.CurrentLauncherVersion)
                && !string.IsNullOrWhiteSpace(manifest.LauncherUrl))
            {
                var ans = MessageBox.Show(this,
                    $"Доступна новая версия лаунчера ({manifest.LauncherVersion}).\nУстановить сейчас?",
                    "Обновление лаунчера", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ans == DialogResult.Yes)
                {
                    _progressLabel.Text = "Скачивание новой версии лаунчера...";
                    await UpdaterService.ApplyLauncherSelfUpdateAsync(manifest.LauncherUrl!, _updateCts.Token);
                    AppendLog("[Updater] Перезапуск для обновления лаунчера...");
                    Application.Exit();
                    return;
                }
            }

            var progress = new Progress<UpdateProgress>(p =>
            {
                int pct = (int)Math.Clamp(p.CompletedBytes * 1000.0 / Math.Max(1, p.TotalBytes), 0, 1000);
                _progressBar.Value = pct;
                _progressLabel.Text = $"{p.Stage} {p.CompletedFiles}/{p.TotalFiles} ({p.CurrentFile})";
            });

            var result = await UpdaterService.CheckForUpdatesAsync(
                manifest, GameDir, forceFullHash, progress, _updateCts.Token);
            _pendingUpdate = result;

            if (result.FilesToDownload.Count == 0)
            {
                _progressBar.Value = 1000;
                _progressLabel.Text = $"Игра актуальна (версия {manifest.GameVersion})";
                AppendLog($"[Updater] Все файлы ({manifest.Files.Count}) в актуальном состоянии (v{manifest.GameVersion}).");
            }
            else
            {
                double mb = result.TotalBytesToDownload / (1024.0 * 1024.0);
                _progressBar.Value = 0;
                string pkgInfo = result.PackagesToDownloadCount > 0
                    ? $" ({result.PackagesToDownloadCount} пак., ~{mb:F1} МБ)"
                    : $" ({mb:F1} МБ)";
                _progressLabel.Text = $"Требуется загрузка: {result.FilesToDownload.Count} ф.{pkgInfo} -> v{manifest.GameVersion}";
                AppendLog($"[Updater] Найдено к загрузке: {result.FilesToDownload.Count} файлов{pkgInfo}.");
            }
        }
        catch (OperationCanceledException)
        {
            _progressLabel.Text = "Проверка обновлений отменена.";
            AppendLog("[Updater] Проверка отменена.");
        }
        catch (Exception ex)
        {
            _progressLabel.Text = $"Ошибка проверки обновлений: {ex.Message}";
            AppendLog($"[Updater] Ошибка: {ex.Message}");
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
        if (_isUpdating || _pendingUpdate == null || _pendingUpdate.FilesToDownload.Count == 0) return;

        _isUpdating = true;
        _updateCts = new CancellationTokenSource();
        RefreshStatus();

        try
        {
            var upd = _pendingUpdate;
            AppendLog($"[Updater] Начинаю скачивание {upd.FilesToDownload.Count} файлов...");
            var progress = new Progress<UpdateProgress>(p =>
            {
                int pct = (int)Math.Clamp(p.CompletedBytes * 1000.0 / Math.Max(1, p.TotalBytes), 0, 1000);
                _progressBar.Value = pct;
                double doneMb = p.CompletedBytes / (1024.0 * 1024.0);
                double totalMb = p.TotalBytes / (1024.0 * 1024.0);
                if (p.BytesPerSecond > 0)
                {
                    double speedMb = p.BytesPerSecond / (1024.0 * 1024.0);
                    _progressLabel.Text =
                        $"{p.Stage} {doneMb:F1} / {totalMb:F1} МБ ({speedMb:F2} МБ/с) • {p.CompletedFiles}/{p.TotalFiles} • {p.CurrentFile}";
                }
                else
                {
                    _progressLabel.Text =
                        $"{p.Stage} {doneMb:F1} / {totalMb:F1} МБ • {p.CompletedFiles}/{p.TotalFiles} • {p.CurrentFile}";
                }
            });

            await UpdaterService.DownloadUpdatesAsync(
                upd.Manifest, _settings.ManifestUrl, upd.FilesToDownload, GameDir, progress, _updateCts.Token);

            _pendingUpdate = null;
            _progressBar.Value = 1000;
            _progressLabel.Text = $"Установлена версия {upd.Manifest.GameVersion}. Готово к запуску!";
            AppendLog($"[Updater] Загрузка завершена. Версия игры: {upd.Manifest.GameVersion}.");
            UpdatePathAndOptionsUi();
            TryApplyIcon();
        }
        catch (OperationCanceledException)
        {
            _progressLabel.Text = "Загрузка приостановлена пользователем.";
            AppendLog("[Updater] Скачивание отменено.");
        }
        catch (Exception ex)
        {
            _progressLabel.Text = $"Ошибка скачивания: {ex.Message}";
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

            // Вариант А: прописываем адрес активного сервера (онлайн или 127.0.0.1) в ClientConfig.tk
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
                bool reachable = await PingTcpAsync(targetHost, _settings.GamePort, 1200);
                _remoteServerOnline = reachable;
                if (!reachable)
                {
                    var ans = MessageBox.Show(this,
                        $"Сервер {targetHost}:{_settings.GamePort} сейчас не отвечает.\nВсё равно запустить клиент?",
                        "Сервер недоступен", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ans != DialogResult.Yes) return;
                }
            }

            if (_rotateShowcaseCheck.Visible && _rotateShowcaseCheck.Checked)
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

    private async Task OpenSettingsAsync()
    {
        if (_isUpdating || _isLaunching) return;

        using var dlg = new SettingsForm(_settings, _detectedEmuDir != null);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        bool manifestChanged = !string.Equals(_settings.ManifestUrl, dlg.Result.ManifestUrl, StringComparison.OrdinalIgnoreCase);
        bool dirChanged = !string.Equals(_settings.GameDir, dlg.Result.GameDir, StringComparison.OrdinalIgnoreCase);
        _settings = dlg.Result;
        _settings.Save();
        _pendingUpdate = null;

        UpdatePathAndOptionsUi();
        await PingRemoteServerIfNeededAsync(force: true);
        RefreshStatus();

        if ((manifestChanged || dirChanged) && !string.IsNullOrWhiteSpace(_settings.ManifestUrl))
        {
            await CheckUpdatesAsync(forceFullHash: false, promptSelfUpdate: true);
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
        var iconPath = Path.Combine(GameDir, "GameIcon.ico");
        if (File.Exists(iconPath))
        {
            try { Icon = new Icon(iconPath); } catch { }
        }
    }

    private string BuildSubtitleText() =>
        _settings.UseLocalServer
            ? $"Локальный режим • Лаунчер v{LauncherSettings.CurrentLauncherVersion}"
            : $"Онлайн-сервер: {_settings.ServerHost}:{_settings.GamePort} • Лаунчер v{LauncherSettings.CurrentLauncherVersion}";

    private void UpdatePathAndOptionsUi()
    {
        _subtitleLabel.Text = BuildSubtitleText();
        _pathInfoLabel.Text = $"Папка игры: {GameDir}   |   ID аккаунта: {_settings.AccountId}";
        _autoStopServerCheck.Visible = _settings.UseLocalServer;
        _stopServerButton.Visible = _settings.UseLocalServer;
        _rotateShowcaseCheck.Visible = Directory.Exists(Path.Combine(GameDir, "showcase_level0"));
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
        _remoteServerOnline = await PingTcpAsync(_settings.ServerHost, _settings.GamePort, 700);
    }

    private static async Task<bool> PingTcpAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await tcp.ConnectAsync(host.Trim(), port, cts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
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
        bool serverUp = _settings.UseLocalServer
            ? ((_serverProcess != null && !_serverProcess.HasExited) || IsPortOpen(_settings.GamePort))
            : _remoteServerOnline;
        bool gameInstalled = File.Exists(TankExePath);
        bool gameUp = (_gameProcess != null && !_gameProcess.HasExited) || IsGameRunning();

        _serverStatusLabel.Text = _settings.UseLocalServer
            ? (serverUp ? "Сервер: Работает (локально)" : "Сервер: Остановлен")
            : (serverUp ? $"Сервер: Онлайн ({_settings.ServerHost})" : $"Сервер: Нет ответа ({_settings.ServerHost})");
        _serverStatusLabel.ForeColor = serverUp ? Color.FromArgb(108, 224, 142) : Color.FromArgb(224, 132, 120);

        if (!gameInstalled)
        {
            _gameStatusLabel.Text = "Клиент: Не установлен";
            _gameStatusLabel.ForeColor = Color.FromArgb(245, 190, 92);
        }
        else if (gameUp)
        {
            _gameStatusLabel.Text = "Клиент: Запущен";
            _gameStatusLabel.ForeColor = Color.FromArgb(108, 224, 142);
        }
        else if (_pendingUpdate != null && _pendingUpdate.FilesToDownload.Count > 0)
        {
            _gameStatusLabel.Text = $"Клиент: Доступна v{_pendingUpdate.Manifest.GameVersion}";
            _gameStatusLabel.ForeColor = Color.FromArgb(110, 192, 255);
        }
        else
        {
            _gameStatusLabel.Text = "Клиент: Готов";
            _gameStatusLabel.ForeColor = Color.FromArgb(180, 186, 196);
        }

        if (_isUpdating)
        {
            _primaryButton.Enabled = true;
            _primaryButton.Text = "ОТМЕНА";
            _primaryButton.BackColor = Color.FromArgb(168, 56, 56);
        }
        else if (gameUp)
        {
            _primaryButton.Enabled = false;
            _primaryButton.Text = "ИГРА ЗАПУЩЕНА";
            _primaryButton.BackColor = Color.FromArgb(62, 68, 79);
        }
        else if (_isLaunching)
        {
            _primaryButton.Enabled = false;
            _primaryButton.Text = "ЗАПУСК...";
            _primaryButton.BackColor = Color.FromArgb(46, 148, 79);
        }
        else if (_pendingUpdate != null && _pendingUpdate.FilesToDownload.Count > 0)
        {
            double mb = _pendingUpdate.TotalBytesToDownload / (1024.0 * 1024.0);
            _primaryButton.Enabled = true;
            _primaryButton.Text = gameInstalled ? $"ОБНОВИТЬ ({mb:F1} МБ)" : $"УСТАНОВИТЬ ({mb:F1} МБ)";
            _primaryButton.BackColor = Color.FromArgb(44, 126, 200);
        }
        else if (!gameInstalled)
        {
            _primaryButton.Enabled = true;
            _primaryButton.Text = "УСТАНОВИТЬ ИГРУ";
            _primaryButton.BackColor = Color.FromArgb(44, 126, 200);
        }
        else
        {
            _primaryButton.Enabled = true;
            _primaryButton.Text = "ИГРАТЬ";
            _primaryButton.BackColor = Color.FromArgb(46, 148, 79);
        }

        _verifyButton.Enabled = !_isUpdating && !_isLaunching && !gameUp;
        _settingsButton.Enabled = !_isUpdating && !_isLaunching;
        _stopServerButton.Enabled = _settings.UseLocalServer && serverUp;
    }

    private void AppendLog(string line)
    {
        if (_logBox.TextLength > 32000) _logBox.Text = _logBox.Text[^16000..];
        _logBox.AppendText(line + Environment.NewLine);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _pollTimer.Stop();
        _updateCts?.Cancel();
        if (_serverProcess != null && !_serverProcess.HasExited && _serverStartedByLauncher)
        {
            try { _serverProcess.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static Button MakeActionButton(string text, Color back, float fontSize, Padding margin)
    {
        var b = new Button
        {
            Dock = DockStyle.Fill,
            Text = text,
            Font = new Font("Segoe UI", fontSize, FontStyle.Bold),
            BackColor = back,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = margin,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }
}
