using System;

namespace DadBoard.App;

sealed class InstallSession
{
    public InstallSession(string logPath, string statusPath, DateTime startedAt)
    {
        LogPath = logPath;
        StatusPath = statusPath;
        StartedAt = startedAt;
    }

    public string LogPath { get; }
    public string StatusPath { get; }
    public DateTime StartedAt { get; }
}
