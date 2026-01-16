using System;
using System.IO;
using System.Text.Json;

namespace DadBoard.Spine.Shared;

public sealed class SetupResultStatus
{
    public string TimestampUtc { get; set; } = "";
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string VersionAfter { get; set; } = "";
}

public static class SetupResultStore
{
    public static bool Save(SetupResultStatus status)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DadBoardPaths.SetupResultPath)!);
        var json = JsonSerializer.Serialize(status, JsonUtil.Options);
        var tempPath = DadBoardPaths.SetupResultPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(DadBoardPaths.SetupResultPath))
            {
                File.Replace(tempPath, DadBoardPaths.SetupResultPath, null);
            }
            else
            {
                File.Move(tempPath, DadBoardPaths.SetupResultPath);
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

    public static SetupResultStatus? Load()
    {
        if (!File.Exists(DadBoardPaths.SetupResultPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(DadBoardPaths.SetupResultPath);
            return JsonSerializer.Deserialize<SetupResultStatus>(json, JsonUtil.Options);
        }
        catch
        {
            return null;
        }
    }

    public static void TryClear()
    {
        try
        {
            if (File.Exists(DadBoardPaths.SetupResultPath))
            {
                File.Delete(DadBoardPaths.SetupResultPath);
            }
        }
        catch
        {
        }
    }
}
