using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DadBoard.Spine.Shared;

namespace DadBoard.Leader;

sealed class KnownAgentsStore
{
    private readonly string _path;

    public KnownAgentsStore(string path)
    {
        _path = path;
    }

    public void Save(IEnumerable<KnownAgentRecord> agents)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(agents, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            File.WriteAllText(_path, json);
        }
        catch
        {
        }
    }
}
