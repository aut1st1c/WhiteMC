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
        base.OnStartup(e);

        // ─── Синхронная часть: быстрые операции, дедлока не будет ─────
        try { LogService.RotateOnStartup(); }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось инициализировать логгер:\n{ex}",
                "WhiteMC — предупреждение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        LauncherSettings settings;
        try { settings = SettingsService.Load(); }
        catch { settings = new LauncherSettings(); }

        try { ThemeService.Apply(settings.Theme); }
        catch (Exception ex)
        {
            try { LogService.Log($"[WARN] Не удалось применить тему: {ex}"); } catch { }
        }

        // ─── Асинхронная часть: bootstrap + показ окна ───────────────
        // Намеренно fire-and-forget: OnStartup не может быть async,
        // а блокировать UI-поток через .GetResult() нельзя (дедлок).
        _ = InitializeAndShowAsync();
    }

    private async Task InitializeAndShowAsync()
    {
        Exception? bootstrapError = null;

        try
        {
            // ConfigureAwait(false) здесь не нужен: мы хотим вернуться
            // на UI-поток, чтобы создать окно. Внутри LinksService/Profiles
            // стоит .ConfigureAwait(false), так что UI-поток во время
            // HTTP-запросов полностью свободен.
            await LinksService.InitializeAsync();
            await Profiles.InitializeAsync();
        }
        catch (Exception ex)
        {
            bootstrapError = ex;
            try { LogService.Log($"[WARN] Bootstrap не удался: {ex}"); } catch { }
        }

        // Создаём окно на UI-потоке (после await мы снова на нём).
        var win = new MainWindow();
        MainWindow = win;
        win.Show();

        if (bootstrapError != null)
        {
            MessageBox.Show(win,
                "Не удалось загрузить конфигурацию лаунчера:\n" +
                $"  {bootstrapError.Message}\n\n" +
                "Лаунчер запустится, но список профилей может быть пуст.\n" +
                "Проверьте links.json и наличие сети.",
                "WhiteMC — предупреждение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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