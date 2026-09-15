using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WhiteMC.Core;

namespace WhiteMC;

public partial class SettingsWindow : Window
{
    private readonly LauncherSettings _settings;
    private readonly string? _currentProfile;
    private readonly string _originalTheme;

    public SettingsWindow(LauncherSettings settings, string? currentProfile)
    {
        InitializeComponent();
        _settings = settings;
        _currentProfile = currentProfile;
        _originalTheme = ThemeService.Normalize(settings.Theme);

        TxtUser.Text = settings.Username;
        TxtXms.Text  = settings.Xms;
        TxtXmx.Text  = settings.Xmx;
        TxtJvm.Text  = settings.ExtraJvmArgs;

        // Заполняем ComboBox темами.
        foreach (var (id, name) in ThemeService.Available)
            CmbTheme.Items.Add(new ThemeEntry(id, name));

        for (int i = 0; i < CmbTheme.Items.Count; i++)
        {
            if (CmbTheme.Items[i] is ThemeEntry te && te.Id == _originalTheme)
            {
                CmbTheme.SelectedIndex = i;
                break;
            }
        }
        if (CmbTheme.SelectedIndex < 0 && CmbTheme.Items.Count > 0)
            CmbTheme.SelectedIndex = 0;

        LblLogPath.Text = $"Лог:  {Constants.LogFile}";
        LblCache.Text   = $"Кэш модпаков:  {Constants.ModpackCacheDir}";

        BtnOpenLauncher.Click += (_, _) => OpenFolder(Constants.LauncherDir);
        BtnOpenInstance.Click += (_, _) =>
        {
            if (_currentProfile != null) OpenFolder(InstanceManager.GetDir(_currentProfile));
        };
        BtnOpenLogs.Click += (_, _) => OpenFolder(Constants.LogDir);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Log($"[WhiteMC] Не удалось открыть папку {path}: {ex.Message}");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.Username     = string.IsNullOrWhiteSpace(TxtUser.Text) ? "Player" : TxtUser.Text.Trim();
        _settings.Xms          = string.IsNullOrWhiteSpace(TxtXms.Text)  ? "512M"   : TxtXms.Text.Trim();
        _settings.Xmx          = string.IsNullOrWhiteSpace(TxtXmx.Text)  ? "2G"     : TxtXmx.Text.Trim();
        _settings.ExtraJvmArgs = TxtJvm.Text.Trim();

        var newTheme = (CmbTheme.SelectedItem as ThemeEntry)?.Id ?? ThemeService.DefaultTheme;
        _settings.Theme = newTheme;

        SettingsService.Save(_settings);
        LogService.Log($"[WhiteMC] Настройки сохранены: Xms={_settings.Xms}, Xmx={_settings.Xmx}, " +
                       $"JVM='{_settings.ExtraJvmArgs}', Theme={_settings.Theme}");

        bool themeChanged = !string.Equals(newTheme, _originalTheme, StringComparison.Ordinal);

        if (themeChanged)
        {
            var r = MessageBox.Show(this,
                "Тема изменится после перезапуска лаунчера.\n\nПерезапустить сейчас?",
                "WhiteMC", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (r == MessageBoxResult.Yes)
            {
                RestartApp();
                return;
            }
        }

        Close();
    }

    private static void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath
                   ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
                throw new Exception("Не удалось определить путь к исполняемому файлу");

            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            LogService.Log($"[WhiteMC] Не удалось перезапустить: {ex}");
            MessageBox.Show(
                $"Не удалось перезапустить лаунчер:\n{ex.Message}\n\n" +
                "Перезапустите вручную.",
                "WhiteMC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Обёртка для ComboBox.Item.</summary>
    private sealed class ThemeEntry
    {
        public string Id   { get; }
        public string Name { get; }
        public ThemeEntry(string id, string name) { Id = id; Name = name; }
        public override string ToString() => Name;
    }
}