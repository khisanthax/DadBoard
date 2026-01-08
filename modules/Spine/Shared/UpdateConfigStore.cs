using System;
using System.IO;
using System.Text.Json;

namespace DadBoard.Spine.Shared;

public static class UpdateConfigStore
{
    public static string GetConfigPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "update.config.json");
    }

    public static UpdateConfig Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path))
        {
            return new UpdateConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<UpdateConfig>(json, JsonUtil.Options);
            return config ?? new UpdateConfig();
        }
        catch
        {
            return new UpdateConfig();
        }
    }

    public static void Save(UpdateConfig config)
    {
        var path = GetConfigPath();
        var json = JsonSerializer.Serialize(config, JsonUtil.Options);
        File.WriteAllText(path, json);
    }
}
