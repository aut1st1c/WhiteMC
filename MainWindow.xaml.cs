using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WhiteMC.Core;

namespace WhiteMC;

public partial class MainWindow : Window
{
    private readonly LauncherSettings _settings = SettingsService.Load();
    private readonly object _procLock = new();
    private System.Diagnostics.Process? _gameProc;
    private bool _busy;

    private LogsWindow? _logsWindow;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var k in Profiles.All.Keys)
        {
            CmbProfile.Items.Add(k);
        }
        if (CmbProfile.Items.Count > 0)
        {
            CmbProfile.SelectedIndex = 0;
        }

        LblLogFile.Text = Constants.LogFile;

        BtnInstall.Click += (_, _) => _ = DoInstallAsync(false, false);
        BtnUpdate.Click  += (_, _) => _ = DoInstallAsync(true, false);
        BtnLaunch.Click  += (_, _) => DoLaunch();
        BtnKill.Click    += (_, _) => DoKill();

        LogService.Log($"[WhiteMC] Старт лаунчера. Логи: {Constants.LogFile}");
        LogService.Log($"[WhiteMC] Лаунчер-папка: {Constants.LauncherDir}");
        LogService.Log($"[WhiteMC] ОС: {Constants.OsName}, {Constants.OsArchBits}-bit");

        UpdateInfo();
        RefreshState();
    }

    // -------------------------------------------------------------------- //
    //  State
    // -------------------------------------------------------------------- //

    private bool ProfileReady(string profile)
    {
        var v = Profiles.Version(profile);
        var vdir = Path.Combine(Constants.VersionsDir, v, $"{v}.json");
        if (!File.Exists(vdir))
        {
            return false;
        }
        if (Profiles.UsesNeoForge(profile))
        {
            return NeoForgeService.Installed(v) != null;
        }
        return true;
    }

    private bool GameRunning()
    {
        lock (_procLock)
        {
            return _gameProc != null && !_gameProc.HasExited;
        }
    }

    private void SetBusy(bool b)
    {
        _busy = b;
        Dispatcher.Invoke(RefreshState);
    }

    private void RefreshState()
    {
        if (CmbProfile.SelectedItem is not string profile)
        {
            profile = "";
        }

        bool ready = profile.Length > 0 && ProfileReady(profile);
        bool running = GameRunning();

        BtnInstall.Visibility = Visibility.Collapsed;
        BtnUpdate.Visibility = Visibility.Collapsed;
        BtnLaunch.Visibility = Visibility.Collapsed;
        BtnKill.Visibility = Visibility.Collapsed;

        if (_busy)
        {
            if (ready)
            {
                BtnUpdate.Visibility = Visibility.Visible;
                BtnLaunch.Visibility = Visibility.Visible;
            }
            else
            {
                BtnInstall.Visibility = Visibility.Visible;
            }
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
                if (running)
                {
                    BtnKill.Visibility = Visibility.Visible;
                }
                else
                {
                    BtnLaunch.Visibility = Visibility.Visible;
                }
            }
        }

        BtnInstall.IsEnabled = !_busy;
        BtnUpdate.IsEnabled = !_busy && !running;
        BtnLaunch.IsEnabled = !_busy && !running;
        BtnKill.IsEnabled = !_busy && running;
        BtnSettings.IsEnabled = !_busy;
    }

    private void UpdateInfo()
    {
        if (CmbProfile.SelectedItem is not string profile)
        {
            return;
        }

        var (_, v, nf) = Profiles.Resolve(profile);

        LblVersion.Text = $"MC {v}" + (nf ? "  •  NeoForge" : "  •  vanilla");

        var vjsonPath = Path.Combine(Constants.VersionsDir, v, $"{v}.json");
        int? major = null;
        if (File.Exists(vjsonPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(vjsonPath)) is JsonObject obj)
                {
                    major = VersionInstaller.RequiredJavaMajor(obj);
                }
            }
            catch
            {
                // ignore
            }
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
        if (Profiles.HasModpack(profile))
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
                catch
                {
                    // ignore
                }
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
                {
                    return true;
                }
            }
            catch
            {
                // ignore
            }
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
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* игнорируем */ }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close(); // пройдёт через OnClosing с проверкой запущенной игры
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var profile = CmbProfile.SelectedItem as string;
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

    private async Task DoInstallAsync(bool checkUpdates, bool thenLaunch)
    {
        if (CmbProfile.SelectedItem is not string profile || _busy)
        {
            return;
        }

        var v = Profiles.Version(profile);
        SetBusy(true);
        Dispatcher.Invoke(() =>
        {
            Prog.Value = 0;
            Prog.Visibility = Visibility.Visible;
            LblStatus.Text = "Установка…";
            LblStatus.Foreground = (Brush)FindResource("Accent");
        });

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
                await VersionInstaller.InstallAsync(v, Progress, LogService.Log, checkUpdates);
                if (Profiles.HasModpack(profile))
                {
                    LogService.Log($"[WhiteMC] Модпак: обработка для {profile}…");
                    await ModpackService.InstallAsync(profile, Progress, LogService.Log, checkUpdates);
                }
            });

            LogService.Log($"[OK] {profile}: готово");
            Dispatcher.Invoke(() =>
            {
                LblStatus.Text = "Готово";
                LblStatus.Foreground = (Brush)FindResource("Green");
            });

            if (thenLaunch)
            {
                Dispatcher.Invoke(DoLaunch);
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"[ОШИБКА] {ex}");
            Dispatcher.Invoke(() =>
            {
                LblStatus.Text = "Ошибка";
                LblStatus.Foreground = (Brush)FindResource("Red");
                MessageBox.Show(this, ex.Message, "WhiteMC",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
        finally
        {
            SetBusy(false);
            Dispatcher.Invoke(() =>
            {
                Prog.Visibility = Visibility.Collapsed;
                UpdateInfo();
                RefreshState();
            });
        }
    }

    private void DoLaunch()
    {
        if (CmbProfile.SelectedItem is not string profile)
        {
            return;
        }

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

        try
        {
            LogService.Log("─────────────── запуск ───────────────");
            var proc = LauncherService.Launch(profile, _settings, LogService.Log);
            lock (_procLock)
            {
                _gameProc = proc;
            }

            LogService.Log($"Игра запущена (pid {proc.Id})");
            LblStatus.Text = $"Игра запущена (pid {proc.Id})";
            LblStatus.Foreground = (Brush)FindResource("Green");
            RefreshState();

            _ = Task.Run(() =>
            {
                try
                {
                    proc.WaitForExit();
                }
                catch
                {
                    // ignore
                }

                int code = -1;
                try
                {
                    code = proc.ExitCode;
                }
                catch
                {
                    // ignore
                }

                Dispatcher.Invoke(() =>
                {
                    lock (_procLock)
                    {
                        if (_gameProc == proc)
                        {
                            _gameProc = null;
                        }
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
        lock (_procLock)
        {
            p = _gameProc;
        }

        if (p == null || p.HasExited)
        {
            RefreshState();
            return;
        }

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
            lock (_procLock)
            {
                p = _gameProc;
            }
            LauncherService.Kill(p, LogService.Log);
        }
        LogService.Shutdown();
        base.OnClosing(e);
    }
}