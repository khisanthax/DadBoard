using System;
using System.IO;
using DadBoard.Spine.Shared;

namespace DadBoard.Setup;

public sealed class SetupLogger : IDisposable
{
    private readonly string _logPath;
    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public SetupLogger(string? logPath = null)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            var dir = DadBoardPaths.SetupLogDir;
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "setup.log");
        }
        else
        {
            _logPath = logPath;
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        }

        var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _writer.WriteLine($"{DateTime.UtcNow:O} [INFO] Setup started.");
    }

    public string LogPath => _logPath;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{level}] {message}";
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}
}
