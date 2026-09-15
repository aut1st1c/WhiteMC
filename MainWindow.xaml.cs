using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhiteMC.Core;

namespace WhiteMC;

public partial class MainWindow : Window
{
    private readonly LauncherSettings _settings = SettingsService.Load();
    private readonly object _procLock = new();
    private System.Diagnostics.Process? _gameProc;
    private bool _busy;
    private int  _modCheckVersion;

    private LogsWindow? _logsWindow;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var (key, prof) in Profiles.All)
            CmbProfile.Items.Add(new ProfileEntry(key, prof.DisplayName ?? key));

        if (CmbProfile.Items.Count > 0)
            CmbProfile.SelectedIndex = 0;

        LblLogFile.Text    = Constants.LogFile;
        LblAppVersion.Text = $"v{AppVersion.Current}";

        BtnInstall.Click += (_, _) => _ = DoFullInstallAsync(force: false);
        BtnRepair.Click  += (_, _) => _ = DoFullInstallAsync(force: true);
        BtnUpdate.Click  += (_, _) => _ = DoCheckUpdatesAsync();
        BtnLaunch.Click  += (_, _) => DoLaunch();
        BtnKill.Click    += (_, _) => DoKill();

        // Проверка обновления лаунчера — после того, как окно показано
        // (иначе MessageBox не сможет стать модальным к этому окну).
        Loaded += (_, _) => CheckLauncherUpdate();

        LogService.Log($"[WhiteMC] Старт лаунчера v{AppVersion.Current}. Логи: {Constants.LogFile}");
        LogService.Log($"[WhiteMC] Лаунчер-папка: {Constants.LauncherDir}");
        LogService.Log($"[WhiteMC] ОС: {Constants.OsName}, {Constants.OsArchBits}-bit");

        UpdateInfo();
        RefreshState();
    }

    // -------------------------------------------------------------------- //
    //  Проверка обновления лаунчера
    // -------------------------------------------------------------------- //

    private void CheckLauncherUpdate()
    {
        var links = LinksService.Current;

        if (string.IsNullOrWhiteSpace(links.LauncherVersion))
        {
            LogService.Log("[WhiteMC] links.json: launcher_version не задан — проверка обновлений пропущена");
            return;
        }

        int cmp = AppVersion.Compare(AppVersion.Current, links.LauncherVersion);
        LogService.Log($"[WhiteMC] Версия: клиент {AppVersion.Current}, сервер {links.LauncherVersion} (cmp={cmp})");

        if (cmp >= 0)
        {
            LogService.Log("[WhiteMC] Обновление лаунчера не требуется");
            return;
        }

        var hasUrl = !string.IsNullOrWhiteSpace(links.LauncherDownloadUrl);

        var msg = $"Доступна новая версия лаунчера.\n\n" +
                  $"Установлена:  {AppVersion.Current}\n" +
                  $"Доступна:      {links.LauncherVersion}\n\n" +
                  (hasUrl ? "Открыть страницу загрузки?" : "Скачать можно позже.");

        var r = MessageBox.Show(this, msg, "WhiteMC — обновление",
            hasUrl ? MessageBoxButton.YesNo : MessageBoxButton.OK,
            MessageBoxImage.Information);

        if (r != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = links.LauncherDownloadUrl!,
                UseShellExecute = true
            });
            LogService.Log($"[WhiteMC] Открыт URL обновления: {links.LauncherDownloadUrl}");
        }
        catch (Exception ex)
        {
            LogService.Log($"[WhiteMC] Не удалось открыть URL обновления: {ex.Message}");
            MessageBox.Show(this,
                $"Не удалось открыть браузер:\n{ex.Message}\n\n" +
                $"Скачайте вручную:\n{links.LauncherDownloadUrl}",
                "WhiteMC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // -------------------------------------------------------------------- //
    //  Утилиты
    // -------------------------------------------------------------------- //

    private string? CurrentProfileKey => (CmbProfile.SelectedItem as ProfileEntry)?.Key;

    private bool ProfileReady(string profile)
    {
        var v = Profiles.Version(profile);
        var vdir = Path.Combine(Constants.VersionsDir, v, $"{v}.json");
        if (!File.Exists(vdir)) return false;
        if (Profiles.UsesNeoForge(profile))
            return NeoForgeService.Installed(v) != null;
        return true;
    }

    private bool GameRunning()
    {
        lock (_procLock) { return _gameProc != null && !_gameProc.HasExited; }
    }

    private void SetBusy(bool b)
    {
        _busy = b;
        Dispatcher.Invoke(RefreshState);
    }

    private void RefreshState()
    {
        var profile = CurrentProfileKey ?? "";
        bool ready   = profile.Length > 0 && ProfileReady(profile);
        bool running = GameRunning();

        BtnInstall.Visibility = Visibility.Collapsed;
        BtnUpdate.Visibility  = Visibility.Collapsed;
        BtnRepair.Visibility  = Visibility.Collapsed;
        BtnLaunch.Visibility  = Visibility.Collapsed;
        BtnKill.Visibility    = Visibility.Collapsed;

        if (_busy)
        {
            if (ready) { BtnUpdate.Visibility = Visibility.Visible; BtnLaunch.Visibility = Visibility.Visible; }
            else       { BtnInstall.Visibility = Visibility.Visible; }
        }
        else
        {
            if (!ready)
            {
                BtnInstall.Visibility = Visibility.Visible;
            }
            else
            {
                BtnUpdate.Visibility = Visibility.Visible;
                BtnRepair.Visibility = Visibility.Visible;
                if (running) BtnKill.Visibility = Visibility.Visible;
                else         BtnLaunch.Visibility = Visibility.Visible;
            }
        }

        BtnInstall.IsEnabled  = !_busy;
        BtnUpdate.IsEnabled   = !_busy && !running;
        BtnRepair.IsEnabled   = !_busy && !running;
        BtnLaunch.IsEnabled   = !_busy && !running;
        BtnKill.IsEnabled     = !_busy && running;
        BtnSettings.IsEnabled = !_busy;
    }

    private void UpdateInfo()
    {
        var profile = CurrentProfileKey;
        if (profile == null) return;

        var (_, v, nf) = Profiles.Resolve(profile);

        LblVersion.Text = $"MC {v}" + (nf ? "  •  NeoForge" : "  •  vanilla");

        var vjsonPath = Path.Combine(Constants.VersionsDir, v, $"{v}.json");
        int? major = null;
        if (File.Exists(vjsonPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(vjsonPath)) is JsonObject obj)
                    major = VersionInstaller.RequiredJavaMajor(obj);
            }
            catch { }
        }

        if (major == null)
        {
            LblJava.Text = "Java: требуется  (уточнится при установке)";
            LblJava.Foreground = (Brush)FindResource("Subtext");
        }
        else
        {
            var local = JavaService.InstalledJavaPath(major.Value);
            if (local != null)
            {
                LblJava.Text = $"Java {major}:  локальная сборка";
                LblJava.Foreground = (Brush)FindResource("Green");
            }
            else if (HasJavaInPath())
            {
                LblJava.Text = $"Java {major}:  системная (из PATH)";
                LblJava.Foreground = (Brush)FindResource("Yellow");
            }
            else
            {
                LblJava.Text = $"Java {major}:  будет скачана при установке";
                LblJava.Foreground = (Brush)FindResource("Red");
            }
        }

        if (nf)
        {
            var nfId = NeoForgeService.Installed(v);
            if (nfId != null)
            {
                LblNeoForge.Text = $"NeoForge:  установлен ({nfId})";
                LblNeoForge.Foreground = (Brush)FindResource("Green");
            }
            else
            {
                LblNeoForge.Text = "NeoForge:  будет установлен автоматически";
                LblNeoForge.Foreground = (Brush)FindResource("Yellow");
            }
        }
        else
        {
            LblNeoForge.Text = "NeoForge:  не требуется";
            LblNeoForge.Foreground = (Brush)FindResource("Subtext");
        }

        bool modpackShown = false;
        if (Profiles.HasComponents(profile))
        {
            var instDir = InstanceManager.GetDir(profile, create: false);
            var mfPath = Path.Combine(instDir, Constants.ModpackManifestFile);
            if (File.Exists(mfPath))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(mfPath)) is JsonObject obj
                        && obj["components"] is JsonObject comps
                        && comps.Count > 0)
                    {
                        var parts = comps
                            .Where(kv => kv.Key != "__legacy")
                            .OrderBy(kv => kv.Key)
                            .Select(kv => $"{kv.Key}={kv.Value?["version"]?.GetValue<string>() ?? "?"}")
                            .ToList();
                        int totalFiles = comps
                            .Where(kv => kv.Key != "__legacy")
                            .Sum(kv => (kv.Value?["files"] as JsonArray)?.Count ?? 0);
                        LblModpack.Text = $"Модпак:  {string.Join(", ", parts)}   ({totalFiles} файлов)";
                        LblModpack.Foreground = (Brush)FindResource("Green");
                        modpackShown = true;
                    }
                }
                catch { }
            }

            if (!modpackShown)
            {
                LblModpack.Text = "Модпак:  будет установлен при нажатии «Установить»";
                LblModpack.Foreground = (Brush)FindResource("Yellow");
            }
        }
        else
        {
            LblModpack.Text = "Модпак:  не задан для этого профиля";
            LblModpack.Foreground = (Brush)FindResource("Subtext");
        }

        if (Profiles.HasModsManifest(profile))
        {
            LblMods.Text = "Моды: проверка…";
            LblMods.Foreground = (Brush)FindResource("Subtext");
        }
        else
        {
            LblMods.Text = "";
        }

        LblInstance.Text = $"Инстанс:  {InstanceManager.GetDir(profile, create: false)}";

        LblStatus.Text = $"Профиль «{profile}»: {(ProfileReady(profile) ? "установлен" : "не установлен")}";
    }

    private static bool HasJavaInPath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, Constants.JavaBinaryName)))
                    return true;
            }
            catch { }
        }
        return false;
    }

    // -------------------------------------------------------------------- //
    //  Handlers
    // -------------------------------------------------------------------- //

    private void CmbProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInfo();
        RefreshState();
        _ = CheckModsInBackgroundAsync();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var profile = CurrentProfileKey;
        var w = new SettingsWindow(_settings, profile) { Owner = this };
        w.ShowDialog();
    }

    private void BtnLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_logsWindow != null && _logsWindow.IsVisible)
        {
            _logsWindow.Activate();
            return;
        }
        _logsWindow = new LogsWindow { Owner = this };
        _logsWindow.Closed += (_, _) => _logsWindow = null;
        _logsWindow.Show();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // -------------------------------------------------------------------- //
    //  Фоновая проверка модов
    // -------------------------------------------------------------------- //

    private async Task CheckModsInBackgroundAsync()
    {
        int myVersion = Interlocked.Increment(ref _modCheckVersion);

        var entry = CmbProfile.SelectedItem as ProfileEntry;
        if (entry == null) return;

        if (!Profiles.HasModsManifest(entry.Key))
        {
            LblMods.Text = "";
            return;
        }

        var url = Profiles.Modpack(entry.Key)!.ManifestUrl!;

        LblMods.Text = "Моды: проверка…";
        LblMods.Foreground = (Brush)FindResource("Subtext");

        try
        {
            var result = await Task.Run(() => ModSyncService.CheckAsync(entry.Key, url, LogService.Log));

            if (Volatile.Read(ref _modCheckVersion) != myVersion) return;

            if (result.IsUpToDate)
            {
                LblMods.Text = $"Моды: актуальны ({result.TotalLocal})";
                LblMods.Foreground = (Brush)FindResource("Green");
            }
            else
            {
                var parts = new List<string>();
                if (result.Missing.Count    > 0) parts.Add($"отсутствует {result.Missing.Count}");
                if (result.Mismatched.Count > 0) parts.Add($"повреждено {result.Mismatched.Count}");
                if (result.Unresolved.Count > 0) parts.Add($"из архива {result.Unresolved.Count}");
                if (result.Extra.Count      > 0) parts.Add($"лишних {result.Extra.Count}");
                LblMods.Text = $"Моды: требуется синхронизация ({string.Join(", ", parts)})";
                LblMods.Foreground = (Brush)FindResource("Yellow");
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"[WhiteMC] Фоновая проверка модов: {ex.Message}");
            if (Volatile.Read(ref _modCheckVersion) != myVersion) return;
            LblMods.Text = "Моды: ошибка проверки (см. логи)";
            LblMods.Foreground = (Brush)FindResource("Red");
        }
    }

    // -------------------------------------------------------------------- //
    //  Полная установка / починка
    // -------------------------------------------------------------------- //

    private async Task DoFullInstallAsync(bool force)
    {
        var profile = CurrentProfileKey;
        if (profile == null || _busy) return;

        var v = Profiles.Version(profile);
        SetBusy(true);

        Prog.Value = 0;
        Prog.Visibility = Visibility.Visible;
        LblStatus.Text = force ? "Починка установки…" : "Установка…";
        LblStatus.Foreground = (Brush)FindResource("Accent");

        void Progress(int i, int total, string msg)
        {
            double pct = 100.0 * i / Math.Max(total, 1);
            Dispatcher.Invoke(() =>
            {
                Prog.Value = pct;
                LblStatus.Text = msg;
                LblStatus.Foreground = (Brush)FindResource("Subtext");
            });
        }

        try
        {
            await Task.Run(async () =>
            {
                await VersionInstaller.InstallAsync(v, Progress, LogService.Log, checkUpdates: force);

                if (Profiles.HasComponents(profile))
                {
                    LogService.Log($"[WhiteMC] Модпак: обработка компонентов для {profile}…");
                    await ModpackService.InstallAsync(profile, Progress, LogService.Log, checkUpdates: force);
                }

                if (Profiles.HasModsManifest(profile))
                {
                    var url = Profiles.Modpack(profile)!.ManifestUrl!;
                    LogService.Log($"[WhiteMC] Синхронизация модов по манифесту для {profile}…");
                    await ModSyncService.SyncAsync(profile, url, Progress, LogService.Log);
                }
            });

            LogService.Log($"[OK] {profile}: готово");
            LblStatus.Text = "Готово";
            LblStatus.Foreground = (Brush)FindResource("Green");
        }
        catch (Exception ex)
        {
            LogService.Log($"[ОШИБКА] {ex}");
            LblStatus.Text = "Ошибка";
            LblStatus.Foreground = (Brush)FindResource("Red");
            MessageBox.Show(this, ex.Message, "WhiteMC",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            Prog.Visibility = Visibility.Collapsed;
            UpdateInfo();
            RefreshState();
            _ = CheckModsInBackgroundAsync();
        }
    }

    // -------------------------------------------------------------------- //
    //  Только обновление модов
    // -------------------------------------------------------------------- //

    private async Task DoCheckUpdatesAsync()
    {
        var profile = CurrentProfileKey;
        if (profile == null || _busy) return;

        if (!Profiles.HasModsManifest(profile))
        {
            MessageBox.Show(this, "Для этого профиля не задан манифест модов.", "WhiteMC");
            return;
        }

        var url = Profiles.Modpack(profile)!.ManifestUrl!;

        SetBusy(true);
        Prog.Value = 0;
        Prog.Visibility = Visibility.Visible;
        LblStatus.Text = "Проверка обновлений модов…";
        LblStatus.Foreground = (Brush)FindResource("Accent");

        void Progress(int i, int total, string msg)
        {
            double pct = 100.0 * i / Math.Max(total, 1);
            Dispatcher.Invoke(() =>
            {
                Prog.Value = pct;
                LblStatus.Text = msg;
                LblStatus.Foreground = (Brush)FindResource("Subtext");
            });
        }

        try
        {
            await Task.Run(() => ModSyncService.SyncAsync(profile, url, Progress, LogService.Log));

            LogService.Log($"[OK] {profile}: моды синхронизированы");
            LblStatus.Text = "Моды обновлены";
            LblStatus.Foreground = (Brush)FindResource("Green");
        }
        catch (Exception ex)
        {
            LogService.Log($"[ОШИБКА] {ex}");
            LblStatus.Text = "Ошибка";
            LblStatus.Foreground = (Brush)FindResource("Red");
            MessageBox.Show(this, ex.Message, "WhiteMC",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            Prog.Visibility = Visibility.Collapsed;
            UpdateInfo();
            RefreshState();
            _ = CheckModsInBackgroundAsync();
        }
    }

    // -------------------------------------------------------------------- //
    //  Запуск
    // -------------------------------------------------------------------- //

    private async void DoLaunch()
    {
        var profile = CurrentProfileKey;
        if (profile == null) return;

        if (GameRunning())
        {
            MessageBox.Show(this, "Игра уже запущена.", "WhiteMC");
            return;
        }

        if (!ProfileReady(profile))
        {
            MessageBox.Show(this,
                $"Профиль «{profile}» не установлен. Сначала нажмите «Установить».",
                "WhiteMC", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool canLaunch = await VerifyModsBeforeLaunchAsync(profile);
        if (!canLaunch) return;

        LaunchProcess(profile);
    }

    private async Task<bool> VerifyModsBeforeLaunchAsync(string profile)
    {
        if (!Profiles.HasModsManifest(profile)) return true;

        var url = Profiles.Modpack(profile)!.ManifestUrl!;

        SetBusy(true);
        LblStatus.Text = "Проверка модов…";
        LblStatus.Foreground = (Brush)FindResource("Subtext");

        try
        {
            var result = await Task.Run(() => ModSyncService.CheckAsync(profile, url, LogService.Log));

            if (result.IsUpToDate)
            {
                LblStatus.Text = $"Моды в порядке ({result.TotalLocal})";
                LblStatus.Foreground = (Brush)FindResource("Green");
                return true;
            }

            var parts = new List<string>();
            if (result.Missing.Count    > 0) parts.Add($"отсутствует: {result.Missing.Count}");
            if (result.Mismatched.Count > 0) parts.Add($"повреждено:  {result.Mismatched.Count}");
            if (result.Unresolved.Count > 0) parts.Add($"из архива:   {result.Unresolved.Count}");
            if (result.Extra.Count      > 0) parts.Add($"лишних:      {result.Extra.Count}");

            var msg = "Моды не совпадают с серверным манифестом:\n" +
                      string.Join("\n", parts) +
                      "\n\nСинхронизировать моды сейчас?";

            var r = MessageBox.Show(this, msg, "WhiteMC",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (r != MessageBoxResult.Yes) return false;

            await Task.Run(() => ModSyncService.SyncAsync(profile, url,
                (i, t, m) => Dispatcher.Invoke(() =>
                {
                    LblStatus.Text = m;
                    LblStatus.Foreground = (Brush)FindResource("Subtext");
                }),
                LogService.Log));

            return true;
        }
        catch (Exception ex)
        {
            LogService.Log($"[WhiteMC] Ошибка проверки модов: {ex}");
            var r = MessageBox.Show(this,
                $"Не удалось проверить моды:\n{ex.Message}\n\nЗапустить игру всё равно?",
                "WhiteMC", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return r == MessageBoxResult.Yes;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LaunchProcess(string profile)
    {
        try
        {
            LogService.Log("─────────────── запуск ───────────────");
            var proc = LauncherService.Launch(profile, _settings, LogService.Log);
            lock (_procLock) { _gameProc = proc; }

            LogService.Log($"Игра запущена (pid {proc.Id})");
            LblStatus.Text = $"Игра запущена (pid {proc.Id})";
            LblStatus.Foreground = (Brush)FindResource("Green");
            RefreshState();

            _ = Task.Run(() =>
            {
                try { proc.WaitForExit(); } catch { }

                int code = -1;
                try { code = proc.ExitCode; } catch { }

                Dispatcher.Invoke(() =>
                {
                    lock (_procLock)
                    {
                        if (_gameProc == proc) _gameProc = null;
                    }
                    LogService.Log($"[WhiteMC] Java завершилась с кодом {code}");
                    LblStatus.Text = $"Java завершилась с кодом {code}";
                    LblStatus.Foreground = (Brush)FindResource(code == 0 ? "Green" : "Red");
                    RefreshState();
                });
            });
        }
        catch (Exception ex)
        {
            LogService.Log($"[ОШИБКА] {ex}");
            LblStatus.Text = "Ошибка запуска";
            LblStatus.Foreground = (Brush)FindResource("Red");
            MessageBox.Show(this, ex.Message, "WhiteMC",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DoKill()
    {
        System.Diagnostics.Process? p;
        lock (_procLock) { p = _gameProc; }

        if (p == null || p.HasExited) { RefreshState(); return; }

        LogService.Log("[WhiteMC] Завершение процесса по запросу пользователя…");
        LblStatus.Text = "Завершение процесса…";
        LblStatus.Foreground = (Brush)FindResource("Yellow");
        BtnKill.IsEnabled = false;

        Task.Run(() =>
        {
            LauncherService.Kill(p, LogService.Log);
            Dispatcher.Invoke(RefreshState);
        });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (GameRunning())
        {
            var r = MessageBox.Show(this,
                "Игра запущена. Завершить Java-процесс и выйти?",
                "WhiteMC", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            System.Diagnostics.Process? p;
            lock (_procLock) { p = _gameProc; }
            LauncherService.Kill(p, LogService.Log);
        }
        LogService.Shutdown();
        base.OnClosing(e);
    }
}