using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WhiteMC.Core;

public static class LogService
{
    private const int MaxBuffer = 5000;
    private static readonly object Lock = new();
    private static readonly List<string> Buffer = new();
    private static StreamWriter? _writer;

    public static event Action? BufferChanged;

    public static void RotateOnStartup()
    {
        Directory.CreateDirectory(Constants.LogDir);

        // Ротация — не критично, если не удастся.
        try
        {
            if (File.Exists(Constants.LogFile))
            {
                if (File.Exists(Constants.LogPrevFile))
                    File.Delete(Constants.LogPrevFile);
                File.Move(Constants.LogFile, Constants.LogPrevFile);
            }
        }
        catch
        {
            try { File.Delete(Constants.LogFile); } catch { }
        }

        // Открытие writer'а — критично, но не должно валить приложение.
        try
        {
            _writer = new StreamWriter(
                new FileStream(Constants.LogFile, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }
        catch
        {
            // Логгер не работает — продолжаем без него.
            // Все Log() будут складывать сообщения в Buffer и молча игнорировать запись в файл.
            _writer = null;
        }
    }

    public static void Log(string message)
    {
        lock (Lock)
        {
            Buffer.Add(message);
            if (Buffer.Count > MaxBuffer)
                Buffer.RemoveRange(0, MaxBuffer / 2);
            try { _writer?.WriteLine(message); } catch { }
        }
        BufferChanged?.Invoke();
    }

    public static string Snapshot()
    {
        lock (Lock)
            return string.Join("\n", Buffer);
    }

    public static void Shutdown()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
    }
}