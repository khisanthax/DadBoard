using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DadBoard.Spine.Shared;

namespace DadBoard.Gate;

sealed class MicLevelsStore
{
    private readonly string _path;

    public MicLevelsStore(string path)
    {
        _path = path;
    }

    public Dictionary<string, float> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, float>>(json, JsonUtil.Options);
            return data ?? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(Dictionary<string, float> levels)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(levels, JsonUtil.Options);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, _path, true);
            File.Delete(tmp);
        }
        catch
        {
        }
    }
}
