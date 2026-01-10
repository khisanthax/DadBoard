using System;
using System.IO;
using System.Text.Json;

namespace DadBoard.Spine.Shared;

public static class UpdateConfigStore
{
    public static string DefaultStableManifestUrl =>
        "https://github.com/khisanthax/DadBoard/releases/latest/download/latest.json";

    public static string DefaultNightlyManifestUrl =>
        "https://github.com/khisanthax/DadBoard/releases/download/nightly/latest.json";

    public static UpdateChannel DefaultChannel => UpdateChannel.Nightly;

    public static string GetDefaultManifestUrl(UpdateChannel channel)
        => channel == UpdateChannel.Stable ? DefaultStableManifestUrl : DefaultNightlyManifestUrl;

    public static string ResolveManifestUrl(UpdateConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ManifestUrl))
        {
            return config.ManifestUrl;
        }

        return GetDefaultManifestUrl(config.UpdateChannel);
    }

    public static bool IsDefaultManifestUrl(UpdateConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ManifestUrl))
        {
            return true;
        }

        var defaultUrl = GetDefaultManifestUrl(config.UpdateChannel);
        return string.Equals(config.ManifestUrl.Trim(), defaultUrl, StringComparison.OrdinalIgnoreCase);
    }

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
        if (!Enum.IsDefined(typeof(UpdateChannel), config.UpdateChannel))
        {
            config.UpdateChannel = DefaultChannel;
        }

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
            config.Source = "github_mirror";
        }

        if (!config.MirrorEnabled)
        {
            config.MirrorEnabled = true;
        }

        return config;
    }
}
