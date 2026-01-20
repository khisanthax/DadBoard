using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DadBoard.Spine.Shared;

namespace DadBoard.Agent;

public sealed class LaunchSession
{
    public int AppId { get; set; }
    public int? RootPid { get; set; }
    public string? GameName { get; set; }
    public string? InstallDir { get; set; }
    public string? ExePath { get; set; }
    public string? LaunchMethod { get; set; }
    public string StartedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string? LastSeenRunningUtc { get; set; }
    public bool ResolvedPid { get; set; }
}

public static class LaunchSessionStore
{
    private sealed class LaunchSessionStoreData
    {
        public List<LaunchSession> Sessions { get; set; } = new();
    }

    public static Dictionary<int, LaunchSession> Load(string path, Action<string> log)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<int, LaunchSession>();
            }

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<LaunchSessionStoreData>(json, JsonUtil.Options);
            if (data?.Sessions == null)
            {
                return new Dictionary<int, LaunchSession>();
            }

            var map = new Dictionary<int, LaunchSession>();
            foreach (var session in data.Sessions)
            {
                map[session.AppId] = session;
            }

            return map;
        }
        catch (Exception ex)
        {
            log($"LaunchSessionStore load failed: {ex.Message}");
            return new Dictionary<int, LaunchSession>();
        }
    }

    public static void Save(string path, IDictionary<int, LaunchSession> sessions, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var data = new LaunchSessionStoreData { Sessions = new List<LaunchSession>(sessions.Values) };
            var json = JsonSerializer.Serialize(data, JsonUtil.Options);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            log($"LaunchSessionStore save failed: {ex.Message}");
        }
    }
}
