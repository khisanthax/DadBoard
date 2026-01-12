using System;
using System.IO;

namespace DadBoard.App;

sealed class UpdateLogger
{
    private readonly string _path;
    private readonly object _lock = new();

    public UpdateLogger()
    {
        var baseDir = DataPaths.ResolveBaseDir();
        var logDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(logDir);
        _path = Path.Combine(logDir, "update.log");
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}";
        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }
}
