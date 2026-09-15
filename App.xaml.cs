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
        // Исключения в UI-потоке
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Исключения в фоновых потоках (Task.Run, Thread, ...)
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Исключения в Task без await
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // ВАЖНО: инициализируем логгер ДО создания MainWindow,
        // иначе ранние сообщения уйдут в никуда, а повторное открытие
        // файла перезатрёт уже созданный latest.log.
        try
        {
            LogService.RotateOnStartup();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось инициализировать логгер:\n{ex}",
                "WhiteMC — ошибка запуска",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

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

        MessageBox.Show(
            e.Exception.ToString(),
            "WhiteMC — необработанное исключение",
            MessageBoxButton.OK, MessageBoxImage.Error);

        // Не даём приложению упасть — продолжаем работу.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        try { LogService.Log($"[FATAL][Domain] {ex}"); } catch { }

        // Здесь уже нельзя "спасти" процесс — CLR завершит его.
        MessageBox.Show(
            ex?.ToString() ?? "Неизвестная ошибка",
            "WhiteMC — критическая ошибка",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { LogService.Log($"[FATAL][Task] {e.Exception}"); } catch { }
        e.SetObserved();
    }
}