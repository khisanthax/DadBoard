using System;
using System.IO;
using System.Threading;

namespace DadBoard.App;

static class InstallHandoff
{
    public static string GetReadyPath(string sessionId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DadBoard");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"postinstall_{sessionId}.ready");
    }

    public static void SignalReady(string sessionId)
    {
        var path = GetReadyPath(sessionId);
        File.WriteAllText(path, DateTime.Now.ToString("O"));
    }

    public static bool WaitForReady(string sessionId, TimeSpan timeout)
    {
        var path = GetReadyPath(sessionId);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }
}
