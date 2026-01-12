using System;
using System.IO;
using System.Text.Json;

namespace DadBoard.Spine.Shared;

public sealed class UpdaterStatus
{
    public string TimestampUtc { get; set; } = "";
    public string Action { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string AvailableVersion { get; set; } = "";
    public string Result { get; set; } = "";
    public string Message { get; set; } = "";
    public string ManifestUrl { get; set; } = "";
    public string LogPath { get; set; } = "";
}

public static class UpdaterStatusStore
{
    public static string BaseDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard", "Updater");

    public static string LogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard", "logs");

    public static string StatusPath => Path.Combine(BaseDir, "last_result.json");

    public static void Save(UpdaterStatus status)
    {
        Directory.CreateDirectory(BaseDir);
        var json = JsonSerializer.Serialize(status, JsonUtil.Options);
        File.WriteAllText(StatusPath, json);
    }

    public static UpdaterStatus? Load()
    {
        if (!File.Exists(StatusPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(StatusPath);
            return JsonSerializer.Deserialize<UpdaterStatus>(json, JsonUtil.Options);
        }
        catch
        {
            return null;
        }
    }
}
