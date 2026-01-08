using System;
using System.IO;
using DadBoard.Spine.Shared;

namespace DadBoard.Setup;

public sealed class SetupLogger
{
    private readonly string _logPath;
    private readonly object _lock = new();

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
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
    }
}
