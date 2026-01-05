using System;
using System.IO;
using System.Text.Json;

namespace DadBoard.Agent;

public sealed class AgentStateWriter
{
    private readonly string _path;
    private readonly object _lock = new();

    public AgentStateWriter(string path)
    {
        _path = path;
    }

    public void Write(AgentState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, true);
        }
        catch
        {
        }
    }
}
