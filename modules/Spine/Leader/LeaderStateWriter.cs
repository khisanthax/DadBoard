using System;
using System.IO;
using System.Text.Json;

namespace DadBoard.Leader;

sealed class LeaderStateWriter
{
    private readonly string _path;

    public LeaderStateWriter(string path)
    {
        _path = path;
    }

    public void Write(LeaderStateSnapshot snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
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
