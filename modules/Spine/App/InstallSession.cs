using System;

namespace DadBoard.App;

sealed class InstallSession
{
    public InstallSession(string id, string logPath, string statusPath, DateTime startedAt)
    {
        Id = id;
        LogPath = logPath;
        StatusPath = statusPath;
        StartedAt = startedAt;
    }

    public string Id { get; }
    public string LogPath { get; }
    public string StatusPath { get; }
    public DateTime StartedAt { get; }
}
