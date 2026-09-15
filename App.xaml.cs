using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WhiteMC.Core;

namespace WhiteMC;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 1) Логгер.
        try { LogService.RotateOnStartup(); }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось инициализировать логгер:\n{ex}",
                "WhiteMC — ошибка запуска",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // 2) Настройки (нужны для темы).
        LauncherSettings settings;
        try { settings = SettingsService.Load(); }
        catch { settings = new LauncherSettings(); }

        // 3) Тема — до создания окон.
        try { ThemeService.Apply(settings.Theme); }
        catch (Exception ex)
        {
            try { LogService.Log($"[FATAL] Не удалось применить тему: {ex}"); } catch { }
        }

        // 4) Bootstrap: links.json → profiles.json.
        try
        {
            LinksService.Initialize();
            Profiles.Initialize();
        }
        catch (Exception ex)
        {
            try { LogService.Log($"[FATAL] Конфигурация не загружена: {ex}"); } catch { }
            MessageBox.Show(
                $"Не удалось загрузить конфигурацию лаунчера:\n{ex.Message}",
                "WhiteMC — ошибка запуска",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);

        // 5) Главное окно.
        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { LogService.Shutdown(); } catch { }
        base.OnExit(e);
    }

    // ---------------------------------------------------------------------- //

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { LogService.Log($"[FATAL][UI] {e.Exception}"); } catch { }
        MessageBox.Show(e.Exception.ToString(),
            "WhiteMC — необработанное исключение",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        try { LogService.Log($"[FATAL][Domain] {ex}"); } catch { }
        MessageBox.Show(ex?.ToString() ?? "Неизвестная ошибка",
            "WhiteMC — критическая ошибка",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { LogService.Log($"[FATAL][Task] {e.Exception}"); } catch { }
        e.SetObserved();
    }
}