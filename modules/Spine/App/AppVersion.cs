using System;
using System.Reflection;

namespace DadBoard.App;

static class AppVersion
{
    public static string GetInformationalVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            return info;
        }

        return asm.GetName().Version?.ToString() ?? "unknown";
    }

    public static string GetDisplayVersion()
    {
        var info = GetInformationalVersion();
        var plus = info.IndexOf('+');
        if (plus < 0)
        {
            return info;
        }

        var sha = info[(plus + 1)..].Trim();
        if (sha.Length > 7)
        {
            sha = sha[..7];
        }

        return $"{info[..plus]}+{sha}";
    }
}
