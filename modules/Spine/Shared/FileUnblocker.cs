using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DadBoard.Spine.Shared;

public static class FileUnblocker
{
    public static void TryUnblock(string path, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var adsPath = path + ":Zone.Identifier";
            if (File.Exists(adsPath))
            {
                File.Delete(adsPath);
                log?.Invoke($"Unblocked file: {path}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed to unblock {path}: {ex.Message}");
        }
    }
}
