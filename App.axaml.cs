using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using WhiteMC.Core;
using Avalonia.Markup.Xaml;

namespace WhiteMC;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try { LogService.RotateOnStartup(); }
        catch (Exception ex)
        {
            _ = Dialogs.WarnAsync(null,
                "WhiteMC — предупреждение",
                $"Не удалось инициализировать логгер:\n{ex}");
        }

        LauncherSettings settings;
        try { settings = SettingsService.Load(); }
        catch { settings = new LauncherSettings(); }

        try { ThemeService.Apply(settings.Theme); }
        catch (Exception ex)
        {
            try { LogService.Log($"[WARN] Не удалось применить тему: {ex}"); } catch { }
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Подписываемся на событие Exit — аналог WPF OnExit.
            desktop.Exit += OnApplicationExit;

            _ = InitializeAndShowAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnApplicationExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try { LogService.Shutdown(); } catch { }
    }

    private async Task InitializeAndShowAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Exception? bootstrapError = null;

        try
        {
            await LinksService.InitializeAsync();
            await Profiles.InitializeAsync();
        }
        catch (Exception ex)
        {
            bootstrapError = ex;
            try { LogService.Log($"[WARN] Bootstrap не удался: {ex}"); } catch { }
        }

        // После await мы снова на UI-потоке.
        var win = new MainWindow();
        desktop.MainWindow = win;
        win.Show();

        if (bootstrapError != null)
        {
            await Dialogs.WarnAsync(win,
                "WhiteMC — предупреждение",
                "Не удалось загрузить конфигурацию лаунчера:\n" +
                $"  {bootstrapError.Message}\n\n" +
                "Лаунчер запустится, но список профилей может быть пуст.\n" +
                "Проверьте links.json и наличие сети.");
        }
    }

    // ---------------------------------------------------------------------- //

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { LogService.Log($"[FATAL][UI] {e.Exception}"); } catch { }
        _ = Dialogs.ErrorAsync(null, "WhiteMC — необработанное исключение", e.Exception.ToString());
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        try { LogService.Log($"[FATAL][Domain] {ex}"); } catch { }
        _ = Dialogs.ErrorAsync(null,
            "WhiteMC — критическая ошибка",
            ex?.ToString() ?? "Неизвестная ошибка");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { LogService.Log($"[FATAL][Task] {e.Exception}"); } catch { }
        e.SetObserved();
    }
}