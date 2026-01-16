using System;
using System.IO;
using System.Text.Json;

namespace DadBoard.Spine.Shared;

public sealed class UpdaterStatus
{
    public int SchemaVersion { get; set; } = 1;
    public string TimestampUtc { get; set; } = "";
    public string Invocation { get; set; } = "";
    public string Channel { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string LogPath { get; set; } = "";
    public string PayloadPath { get; set; } = "";
    public int? SetupExitCode { get; set; }
    public string SetupLogPath { get; set; } = "";
    public long? DurationMs { get; set; }
    public string ManifestUrl { get; set; } = "";

    // Backward-compat fields for older readers
    public string AvailableVersion { get; set; } = "";
    public string Result { get; set; } = "";
    public string Message { get; set; } = "";
}

public static class UpdaterStatusStore
{
    public static string BaseDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard", "Updater");

    public static string LogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard", "logs");

    public static string StatusPath => Path.Combine(BaseDir, "last_result.json");

    public static bool Save(UpdaterStatus status)
    {
        Directory.CreateDirectory(BaseDir);
        var json = JsonSerializer.Serialize(status, JsonUtil.Options);
        var tempPath = StatusPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(StatusPath))
            {
                File.Replace(tempPath, StatusPath, null);
            }
            else
            {
                File.Move(tempPath, StatusPath);
            }
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
            return false;
        }
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
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new UpdaterStatus
            {
                SchemaVersion = GetInt(root, "schema_version") ?? 1,
                TimestampUtc = GetString(root, "timestamp_utc", "TimestampUtc") ?? "",
                Invocation = GetString(root, "invocation", "Invocation") ?? "",
                Channel = GetString(root, "channel", "Channel") ?? "",
                InstalledVersion = GetString(root, "installed_version", "InstalledVersion") ?? "",
                LatestVersion = GetString(root, "latest_version", "LatestVersion") ?? "",
                Action = GetString(root, "action", "Action") ?? "",
                Success = GetBool(root, "success", "Success") ?? false,
                ExitCode = GetInt(root, "exit_code", "ExitCode") ?? 0,
                ErrorCode = GetString(root, "error_code", "ErrorCode") ?? "",
                ErrorMessage = GetString(root, "error_message", "ErrorMessage") ?? "",
                LogPath = GetString(root, "log_path", "LogPath") ?? "",
                PayloadPath = GetString(root, "payload_path", "PayloadPath") ?? "",
                SetupExitCode = GetInt(root, "setup_exit_code", "SetupExitCode"),
                SetupLogPath = GetString(root, "setup_log_path", "SetupLogPath") ?? "",
                DurationMs = GetLong(root, "duration_ms", "DurationMs"),
                ManifestUrl = GetString(root, "manifest_url", "ManifestUrl") ?? "",
                AvailableVersion = GetString(root, "AvailableVersion") ?? "",
                Result = GetString(root, "Result") ?? "",
                Message = GetString(root, "Message") ?? ""
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        return null;
    }

    private static int? GetInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
                {
                    return result;
                }
            }
        }
        return null;
    }

    private static long? GetLong(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result))
                {
                    return result;
                }
            }
        }
        return null;
    }

    private static bool? GetBool(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                {
                    return value.GetBoolean();
                }
            }
        }
        return null;
    }
}
