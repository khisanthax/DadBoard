using System;
using System.IO;

namespace DadBoard.Agent;

public sealed class AgentLogger
{
    private readonly string _path;
    private readonly object _lock = new();

    public AgentLogger(string path)
    {
        _path = path;
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.UtcNow:O} [{level}] {message}";
            lock (_lock)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
