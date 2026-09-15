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

    public SettingsWindow(LauncherSettings settings, string? currentProfile)
    {
        InitializeComponent();
        _settings = settings;
        _currentProfile = currentProfile;

        TxtUser.Text = settings.Username;
        TxtXms.Text = settings.Xms;
        TxtXmx.Text = settings.Xmx;
        TxtJvm.Text = settings.ExtraJvmArgs;

        LblLogPath.Text = $"Лог:  {Constants.LogFile}";
        LblCache.Text = $"Кэш модпаков:  {Constants.ModpackCacheDir}";

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
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };
            Process.Start(psi);
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
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.Username = string.IsNullOrWhiteSpace(TxtUser.Text) ? "Player" : TxtUser.Text.Trim();
        _settings.Xms = string.IsNullOrWhiteSpace(TxtXms.Text) ? "512M" : TxtXms.Text.Trim();
        _settings.Xmx = string.IsNullOrWhiteSpace(TxtXmx.Text) ? "2G" : TxtXmx.Text.Trim();
        _settings.ExtraJvmArgs = TxtJvm.Text.Trim();
        SettingsService.Save(_settings);

        LogService.Log($"[WhiteMC] Настройки сохранены: Xms={_settings.Xms}, Xmx={_settings.Xmx}, JVM='{_settings.ExtraJvmArgs}'");
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
}