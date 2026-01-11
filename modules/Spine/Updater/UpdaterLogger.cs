using System;
using System.IO;
using System.Text;

namespace DadBoard.Updater;

sealed class UpdaterLogger : IDisposable
{
    private readonly string _path;
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public UpdaterLogger()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard", "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "updater.log");
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true
        };
        Info("Updater started.");
    }

    public string LogPath => _path;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            _writer?.WriteLine($"{DateTime.UtcNow:O} [{level}] {message}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
