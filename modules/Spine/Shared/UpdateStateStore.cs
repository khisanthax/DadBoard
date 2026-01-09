using System;
using System.IO;
using System.Text.Json;

namespace DadBoard.Spine.Shared;

public static class UpdateStateStore
{
    public static string GetStatePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "state.json");
    }

    public static UpdateState Load()
    {
        var path = GetStatePath();
        if (!File.Exists(path))
        {
            return new UpdateState();
        }

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<UpdateState>(json, JsonUtil.Options);
            return state ?? new UpdateState();
        }
        catch
        {
            return new UpdateState();
        }
    }

    public static void Save(UpdateState state)
    {
        var path = GetStatePath();
        var json = JsonSerializer.Serialize(state, JsonUtil.Options);
        File.WriteAllText(path, json);
    }
}
