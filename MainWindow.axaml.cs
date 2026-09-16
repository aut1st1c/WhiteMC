using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var (key, prof) in Profiles.All)
            CmbProfile.Items.Add(new ProfileEntry(key, prof.DisplayName ?? key));

        if (CmbProfile.ItemCount > 0)
            CmbProfile.SelectedIndex = 0;

        LblLogFile.Text    = Constants.LogFile;
        LblAppVersion.Text = $"v{AppVersion.Current}";

        BtnInstall.Click += (_, _) => _ = DoFullInstallAsync(force: false);
        BtnRepair.Click  += (_, _) => _ = DoFullInstallAsync(force: true);
        BtnUpdate.Click  += (_, _) => _ = DoCheckUpdatesAsync();
        BtnLaunch.Click  += (_, _) => DoLaunch();
        BtnKill.Click    += (_, _) => DoKill();

        Loaded += (_, _) => _ = CheckLauncherUpdateAsync();

        LogService.Log($"[WhiteMC] Старт лаунчера v{AppVersion.Current}. Логи: {Constants.LogFile}");
        LogService.Log($"[WhiteMC] Лаунчер-папка: {Constants.LauncherDir}");
        LogService.Log($"[WhiteMC] ОС: {Constants.OsName}, {Constants.OsArchBits}-bit");

        UpdateInfo();
        RefreshState();
    }

    private IBrush Brush(string key) =>
        (IBrush)this.FindResource(key)!;

    // -------------------------------------------------------------------- //
    //  Проверка обновления лаунчера
    // -------------------------------------------------------------------- //

    private async Task CheckLauncherUpdateAsync()
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

        bool open;
        if (hasUrl)
            open = await Dialogs.ConfirmAsync(this, "WhiteMC — обновление", msg);
        else
        {
            await Dialogs.InfoAsync(this, "WhiteMC — обновление", msg);
            open = false;
        }

        if (!open) return;

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
            await Dialogs.WarnAsync(this, "WhiteMC",
                $"Не удалось открыть браузер:\n{ex.Message}\n\n" +
                $"Скачайте вручную:\n{links.LauncherDownloadUrl}");
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
        Dispatcher.UIThread.Post(RefreshState);
    }

    private void RefreshState()
    {
        var profile = CurrentProfileKey ?? "";
        bool ready   = profile.Length > 0 && ProfileReady(profile);
        bool running = GameRunning();

        BtnInstall.IsVisible = false;
        BtnUpdate.IsVisible  = false;
        BtnRepair.IsVisible  = false;
        BtnLaunch.IsVisible  = false;
        BtnKill.IsVisible    = false;

        if (_busy)
        {
            if (ready) { BtnUpdate.IsVisible = true; BtnLaunch.IsVisible = true; }
            else       { BtnInstall.IsVisible = true; }
        }
        else
        {
            if (!ready)
            {
                BtnInstall.IsVisible = true;
            }
            else
            {
                BtnUpdate.IsVisible = true;
                BtnRepair.IsVisible = true;
                if (running) BtnKill.IsVisible = true;
                else         BtnLaunch.IsVisible = true;
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
            LblJava.Foreground = Brush("Subtext");
        }
        else
        {
            var local = JavaService.InstalledJavaPath(major.Value);
            if (local != null)
            {
                LblJava.Text = $"Java {major}:  локальная сборка";
                LblJava.Foreground = Brush("Green");
            }
            else if (HasJavaInPath())
            {
                LblJava.Text = $"Java {major}:  системная (из PATH)";
                LblJava.Foreground = Brush("Yellow");
            }
            else
            {
                LblJava.Text = $"Java {major}:  будет скачана при установке";
                LblJava.Foreground = Brush("Red");
            }
        }

        if (nf)
        {
            var nfId = NeoForgeService.Installed(v);
            if (nfId != null)
            {
                LblNeoForge.Text = $"NeoForge:  установлен ({nfId})";
                LblNeoForge.Foreground = Brush("Green");
            }
            else
            {
                LblNeoForge.Text = "NeoForge:  будет установлен автоматически";
                LblNeoForge.Foreground = Brush("Yellow");
            }
        }
        else
        {
            LblNeoForge.Text = "NeoForge:  не требуется";
            LblNeoForge.Foreground = Brush("Subtext");
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
                        LblModpack.Foreground = Brush("Green");
                        modpackShown = true;
                    }
                }
                catch { }
            }

            if (!modpackShown)
            {
                LblModpack.Text = "Модпак:  будет установлен при нажатии «Установить»";
                LblModpack.Foreground = Brush("Yellow");
            }
        }
        else
        {
            LblModpack.Text = "Модпак:  не задан для этого профиля";
            LblModpack.Foreground = Brush("Subtext");
        }

        if (Profiles.HasModsManifest(profile))
        {
            LblMods.Text = "Моды: проверка…";
            LblMods.Foreground = Brush("Subtext");
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

    private void CmbProfile_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateInfo();
        RefreshState();
        _ = CheckModsInBackgroundAsync();
    }

    private void BtnSettings_Click(object? sender, RoutedEventArgs e)
    {
        var profile = CurrentProfileKey;
        var w = new SettingsWindow(_settings, profile);
        // Владелец задаётся перегрузкой ShowDialog(owner),
        // а не через { Owner = this }.
        _ = w.ShowDialog(this);
    }

    private void BtnLogs_Click(object? sender, RoutedEventArgs e)
    {
        if (_logsWindow != null && _logsWindow.IsVisible)
        {
            _logsWindow.Activate();
            return;
        }
        _logsWindow = new LogsWindow();
        _logsWindow.Closed += (_, _) => _logsWindow = null;
        // Show(owner) — аналог WPF-овского owner-ownership.
        _logsWindow.Show(this);
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            try { BeginMoveDrag(e); } catch { }
        }
    }

    private void BtnMinimize_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    // -------------------------------------------------------------------- //
    //  Фоновая проверка модов при старте / смене профиля
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
        LblMods.Foreground = Brush("Subtext");

        try
        {
            var result = await Task.Run(() => ModSyncService.CheckAsync(entry.Key, url, LogService.Log));

            if (Volatile.Read(ref _modCheckVersion) != myVersion) return;

            if (result.IsUpToDate)
            {
                LblMods.Text = $"Моды: актуальны ({result.TotalLocal})";
                LblMods.Foreground = Brush("Green");
            }
            else
            {
                var parts = new List<string>();
                if (result.Missing.Count    > 0) parts.Add($"отсутствует {result.Missing.Count}");
                if (result.Mismatched.Count > 0) parts.Add($"повреждено {result.Mismatched.Count}");
                if (result.Unresolved.Count > 0) parts.Add($"из архива {result.Unresolved.Count}");
                if (result.Extra.Count      > 0) parts.Add($"лишних {result.Extra.Count}");
                LblMods.Text = $"Моды: требуется синхронизация ({string.Join(", ", parts)})";
                LblMods.Foreground = Brush("Yellow");
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"[WhiteMC] Фоновая проверка модов: {ex.Message}");
            if (Volatile.Read(ref _modCheckVersion) != myVersion) return;
            LblMods.Text = "Моды: ошибка проверки (см. логи)";
            LblMods.Foreground = Brush("Red");
        }
    }

    // -------------------------------------------------------------------- //
    //  ПОЛНАЯ УСТАНОВКА / ПОЧИНКА
    // -------------------------------------------------------------------- //

    private async Task DoFullInstallAsync(bool force)
    {
        var profile = CurrentProfileKey;
        if (profile == null || _busy) return;

        var v = Profiles.Version(profile);
        SetBusy(true);

        Prog.Value = 0;
        Prog.IsVisible = true;
        LblStatus.Text = force ? "Починка установки…" : "Установка…";
        LblStatus.Foreground = Brush("Accent");

        void Progress(int i, int total, string msg)
        {
            double pct = 100.0 * i / Math.Max(total, 1);
            Dispatcher.UIThread.Post(() =>
            {
                Prog.Value = pct;
                LblStatus.Text = msg;
                LblStatus.Foreground = Brush("Subtext");
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
            LblStatus.Foreground = Brush("Green");
        }
        catch (Exception ex)
        {
            LogService.Log($"[ОШИБКА] {ex}");
            LblStatus.Text = "Ошибка";
            LblStatus.Foreground = Brush("Red");
            await Dialogs.ErrorAsync(this, "WhiteMC", ex.Message);
        }
        finally
        {
            SetBusy(false);
            Prog.IsVisible = false;
            UpdateInfo();
            RefreshState();
            _ = CheckModsInBackgroundAsync();
        }
    }

    // -------------------------------------------------------------------- //
    //  ТОЛЬКО ОБНОВЛЕНИЕ МОДОВ
    // -------------------------------------------------------------------- //

    private async Task DoCheckUpdatesAsync()
    {
        var profile = CurrentProfileKey;
        if (profile == null || _busy) return;

        if (!Profiles.HasModsManifest(profile))
        {
            await Dialogs.InfoAsync(this, "WhiteMC", "Для этого профиля не задан манифест модов.");
            return;
        }

        var url = Profiles.Modpack(profile)!.ManifestUrl!;

        SetBusy(true);
        Prog.Value = 0;
        Prog.IsVisible = true;
        LblStatus.Text = "Проверка обновлений модов…";
        LblStatus.Foreground = Brush("Accent");

        void Progress(int i, int total, string msg)
        {
            double pct = 100.0 * i / Math.Max(total, 1);
            Dispatcher.UIThread.Post(() =>
            {
                Prog.Value = pct;
                LblStatus.Text = msg;
                LblStatus.Foreground = Brush("Subtext");
            });
        }

        try
        {
            await Task.Run(() => ModSyncService.SyncAsync(profile, url, Progress, LogService.Log));

            LogService.Log($"[OK] {profile}: моды синхронизированы");
            LblStatus.Text = "Моды обновлены";
            LblStatus.Foreground = Brush("Green");
        }
        catch (Exception ex)
        {
            LogService.Log($"[ОШИБКА] {ex}");
            LblStatus.Text = "Ошибка";
            LblStatus.Foreground = Brush("Red");
            await Dialogs.ErrorAsync(this, "WhiteMC", ex.Message);
        }
        finally
        {
            SetBusy(false);
            Prog.IsVisible = false;
            UpdateInfo();
            RefreshState();
            _ = CheckModsInBackgroundAsync();
        }
    }

    // -------------------------------------------------------------------- //
    //  ЗАПУСК
    // -------------------------------------------------------------------- //

    private async void DoLaunch()
    {
        var profile = CurrentProfileKey;
        if (profile == null) return;

        if (GameRunning())
        {
            await Dialogs.InfoAsync(this, "WhiteMC", "Игра уже запущена.");
            return;
        }

        if (!ProfileReady(profile))
        {
            await Dialogs.WarnAsync(this, "WhiteMC",
                $"Профиль «{profile}» не установлен. Сначала нажмите «Установить».");
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
        LblStatus.Foreground = Brush("Subtext");

        try
        {
            var result = await Task.Run(() => ModSyncService.CheckAsync(profile, url, LogService.Log));

            if (result.IsUpToDate)
            {
                LblStatus.Text = $"Моды в порядке ({result.TotalLocal})";
                LblStatus.Foreground = Brush("Green");
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

            bool sync = await Dialogs.ConfirmAsync(this, "WhiteMC", msg);
            if (!sync) return false;

            await Task.Run(() => ModSyncService.SyncAsync(profile, url,
                (i, t, m) => Dispatcher.UIThread.Post(() =>
                {
                    LblStatus.Text = m;
                    LblStatus.Foreground = Brush("Subtext");
                }),
                LogService.Log));

            return true;
        }
        catch (Exception ex)
        {
            LogService.Log($"[WhiteMC] Ошибка проверки модов: {ex}");
            bool cont = await Dialogs.ConfirmAsync(this, "WhiteMC",
                $"Не удалось проверить моды:\n{ex.Message}\n\nЗапустить игру всё равно?");
            return cont;
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
            LblStatus.Foreground = Brush("Green");
            RefreshState();

            _ = Task.Run(() =>
            {
                try { proc.WaitForExit(); } catch { }

                int code = -1;
                try { code = proc.ExitCode; } catch { }

                Dispatcher.UIThread.Post(() =>
                {
                    lock (_procLock)
                    {
                        if (_gameProc == proc) _gameProc = null;
                    }
                    LogService.Log($"[WhiteMC] Java завершилась с кодом {code}");
                    LblStatus.Text = $"Java завершилась с кодом {code}";
                    LblStatus.Foreground = Brush(code == 0 ? "Green" : "Red");
                    RefreshState();
                });
            });
        }
        catch (Exception ex)
        {
            LogService.Log($"[ОШИБКА] {ex}");
            LblStatus.Text = "Ошибка запуска";
            LblStatus.Foreground = Brush("Red");
            _ = Dialogs.ErrorAsync(this, "WhiteMC", ex.Message);
        }
    }

    private void DoKill()
    {
        System.Diagnostics.Process? p;
        lock (_procLock) { p = _gameProc; }

        if (p == null || p.HasExited) { RefreshState(); return; }

        LogService.Log("[WhiteMC] Завершение процесса по запросу пользователя…");
        LblStatus.Text = "Завершение процесса…";
        LblStatus.Foreground = Brush("Yellow");
        BtnKill.IsEnabled = false;

        Task.Run(() =>
        {
            LauncherService.Kill(p, LogService.Log);
            Dispatcher.UIThread.Post(RefreshState);
        });
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (GameRunning() && !_closeConfirmed)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync();
            return;
        }

        LogService.Shutdown();
        base.OnClosing(e);
    }

    private async Task ConfirmCloseAsync()
    {
        bool ok = await Dialogs.ConfirmAsync(this,
            "WhiteMC",
            "Игра запущена. Завершить Java-процесс и выйти?");

        if (!ok) return;

        System.Diagnostics.Process? p;
        lock (_procLock) { p = _gameProc; }
        LauncherService.Kill(p, LogService.Log);

        _closeConfirmed = true;
        Close();
    }
}