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
        // 1) Логгер — как можно раньше.
        try { LogService.RotateOnStartup(); }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось инициализировать логгер:\n{ex}",
                "WhiteMC — предупреждение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 2) Настройки (нужны для темы).
        LauncherSettings settings;
        try { settings = SettingsService.Load(); }
        catch { settings = new LauncherSettings(); }

        // 3) Тема — строго ДО создания окна.
        //    Если упадёт — ThemeService сам откатится на Mocha.
        try { ThemeService.Apply(settings.Theme); }
        catch (Exception ex)
        {
            try { LogService.Log($"[WARN] Не удалось применить тему: {ex}"); } catch { }
        }

        // 4) Bootstrap: links.json → profiles.json. НЕ ФАТАЛЬНО.
        try
        {
            LinksService.Initialize();
            Profiles.Initialize();
        }
        catch (Exception ex)
        {
            try { LogService.Log($"[WARN] Bootstrap не удался: {ex}"); } catch { }

            MessageBox.Show(
                "Не удалось загрузить конфигурацию лаунчера:\n" +
                $"  {ex.Message}\n\n" +
                "Лаунчер запустится, но список профилей может быть пуст.\n" +
                "Проверьте links.json на сервере и наличие сети.",
                "WhiteMC — предупреждение",
                MessageBoxButton.OK, MessageBoxImage.Warning);

            // НЕ вызываем Shutdown — пусть окно всё равно откроется.
        }

        // 5) Отдаём управление WPF: он создаст MainWindow из StartupUri.
        base.OnStartup(e);
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