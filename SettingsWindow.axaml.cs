using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WhiteMC.Core;

namespace WhiteMC;

public partial class SettingsWindow : Window
{
    private readonly LauncherSettings _settings;
    private readonly string? _currentProfile;
    private readonly string _originalTheme;
    private bool _suppressThemeChange;

    // Конструктор без параметров — только для Avalonia XAML loader / дизайнера.
    // В рантайме вызывается перегрузка с параметрами.
    public SettingsWindow() : this(new LauncherSettings(), null) { }

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

        _suppressThemeChange = true;
        foreach (var (id, name) in ThemeService.Available)
            CmbTheme.Items.Add(new ThemeEntry(id, name));

        for (int i = 0; i < CmbTheme.ItemCount; i++)
        {
            if (CmbTheme.Items[i] is ThemeEntry te && te.Id == _originalTheme)
            {
                CmbTheme.SelectedIndex = i;
                break;
            }
        }
        if (CmbTheme.SelectedIndex < 0 && CmbTheme.ItemCount > 0)
            CmbTheme.SelectedIndex = 0;
        _suppressThemeChange = false;

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

    private void CmbTheme_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeChange) return;
        if (CmbTheme.SelectedItem is not ThemeEntry te) return;

        // Закрываем дропдаун: Popup может не перекраситься во время смены темы.
        CmbTheme.IsDropDownOpen = false;

        ThemeService.Apply(te.Id);
    }

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        _settings.Username     = string.IsNullOrWhiteSpace(TxtUser.Text) ? "Player" : TxtUser.Text.Trim();
        _settings.Xms          = string.IsNullOrWhiteSpace(TxtXms.Text)  ? "512M"   : TxtXms.Text.Trim();
        _settings.Xmx          = string.IsNullOrWhiteSpace(TxtXmx.Text)  ? "2G"     : TxtXmx.Text.Trim();
        _settings.ExtraJvmArgs = TxtJvm.Text?.Trim() ?? "";
        _settings.Theme        = (CmbTheme.SelectedItem as ThemeEntry)?.Id ?? ThemeService.DefaultTheme;

        SettingsService.Save(_settings);
        LogService.Log($"[WhiteMC] Настройки сохранены: Xms={_settings.Xms}, Xmx={_settings.Xmx}, " +
                       $"JVM='{_settings.ExtraJvmArgs}', Theme={_settings.Theme}");

        Close();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.Equals(ThemeService.CurrentId, _originalTheme, StringComparison.Ordinal))
            ThemeService.Apply(_originalTheme);

        Close();
    }

    private sealed class ThemeEntry
    {
        public string Id   { get; }
        public string Name { get; }
        public ThemeEntry(string id, string name) { Id = id; Name = name; }
        public override string ToString() => Name;
    }
}