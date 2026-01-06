using System;
using System.IO;

namespace DadBoard.App;

sealed class InstallLogger
{
    private readonly string _path;

    public InstallLogger(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Error(string message)
    {
        Write("ERROR", message);
    }

    public void Write(string level, string message)
    {
        var line = $"{DateTime.Now:O} [{level}] {message}{Environment.NewLine}";
        File.AppendAllText(_path, line);
    }
}
