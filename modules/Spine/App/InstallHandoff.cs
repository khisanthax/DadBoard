using System;
using System.IO;
using System.Threading;

namespace DadBoard.App;

static class InstallHandoff
{
    public static string GetTrayReadyPath(string sessionId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DadBoard");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"trayready_{sessionId}.ready");
    }

    public static void SignalTrayReady(string sessionId)
    {
        var path = GetTrayReadyPath(sessionId);
        File.WriteAllText(path, DateTime.Now.ToString("O"));
    }

    public static bool WaitForTrayReady(string sessionId, TimeSpan timeout)
    {
        var path = GetTrayReadyPath(sessionId);
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
