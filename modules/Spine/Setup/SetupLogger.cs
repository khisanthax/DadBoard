using System;
using System.IO;
using DadBoard.Spine.Shared;

namespace DadBoard.Setup;

sealed class SetupLogger
{
    private readonly string _path;
    private readonly object _lock = new();

    public SetupLogger()
    {
        Directory.CreateDirectory(DadBoardPaths.SetupLogDir);
        _path = Path.Combine(DadBoardPaths.SetupLogDir, "setup.log");
    }

    public string Path => _path;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{level}] {message}";
        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }
}
