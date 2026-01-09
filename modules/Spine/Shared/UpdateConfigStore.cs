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
            return Normalize(new UpdateConfig());
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<UpdateConfig>(json, JsonUtil.Options);
            return Normalize(config ?? new UpdateConfig());
        }
        catch
        {
            return Normalize(new UpdateConfig());
        }
    }

    public static void Save(UpdateConfig config)
    {
        var path = GetConfigPath();
        var json = JsonSerializer.Serialize(Normalize(config), JsonUtil.Options);
        File.WriteAllText(path, json);
    }

    private static UpdateConfig Normalize(UpdateConfig config)
    {
        if (config.MirrorPollMinutes <= 0)
        {
            config.MirrorPollMinutes = 10;
        }

        if (config.LocalHostPort <= 0)
        {
            config.LocalHostPort = 45555;
        }

        if (string.IsNullOrWhiteSpace(config.Source))
        {
            if (config.MirrorEnabled)
            {
                config.Source = "github_mirror";
            }
            else if (!string.IsNullOrWhiteSpace(config.ManifestUrl))
            {
                config.Source = "github_direct";
            }
        }

        return config;
    }
}
