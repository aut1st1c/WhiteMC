using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WhiteMC.Core;

namespace WhiteMC;

public partial class LogsWindow : Window
{
    private readonly DispatcherTimer _timer;

    public LogsWindow()
    {
        InitializeComponent();
        LblPath.Text = Constants.LogFile;

        RefreshLog();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => RefreshLog();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
        LogService.BufferChanged += OnBufferChanged;
        Closed += (_, _) => LogService.BufferChanged -= OnBufferChanged;
    }

    private void OnBufferChanged()
    {
        try { Dispatcher.UIThread.Post(RefreshLog); } catch { }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            try { BeginMoveDrag(e); } catch { }
        }
    }

    private void BtnMinimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void RefreshLog()
    {
        var text = LogService.Snapshot();
        if (TxtLog.Text != text)
        {
            TxtLog.Text = text;
            // ScrollToEnd доступен только после лэйаута.
            Dispatcher.UIThread.Post(() => TxtLog.CaretIndex = TxtLog.Text?.Length ?? 0);
        }
    }

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Constants.LogDir);
            Process.Start(new ProcessStartInfo { FileName = Constants.LogDir, UseShellExecute = true });
        }
        catch { }
    }

    private void BtnClear_Click(object? sender, RoutedEventArgs e) => TxtLog.Text = "";

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}