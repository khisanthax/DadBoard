using System;
using System.IO;

namespace DadBoard.Gate;

static class Logger
{
    private static readonly object LogLock = new();
    private static string? _logPath;

    public static void Initialize(string logPath)
    {
        _logPath = logPath;
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message)
    {
        Write("ERROR", message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            if (string.IsNullOrEmpty(_logPath))
            {
                return;
            }

            var line = $"{DateTime.UtcNow:O} [{level}] {message}";
            lock (LogLock)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
