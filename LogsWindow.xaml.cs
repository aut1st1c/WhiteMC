using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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
        try { Dispatcher.BeginInvoke(RefreshLog); } catch { }
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

    private void RefreshLog()
    {
        var text = LogService.Snapshot();
        if (TxtLog.Text != text)
        {
            TxtLog.Text = text;
            TxtLog.ScrollToEnd();
        }
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Constants.LogDir);
            Process.Start(new ProcessStartInfo { FileName = Constants.LogDir, UseShellExecute = true });
        }
        catch { }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}