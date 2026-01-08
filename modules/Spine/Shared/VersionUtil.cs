using System;
using System.Diagnostics;
using System.Reflection;

namespace DadBoard.Spine.Shared;

public static class VersionUtil
{
    public static string Normalize(string? version)
    {
        var parts = ParseParts(version);
        return $"{parts[0]}.{parts[1]}.{parts[2]}";
    }

    public static int Compare(string? left, string? right)
    {
        var a = ParseParts(left);
        var b = ParseParts(right);
        for (var i = 0; i < 3; i++)
        {
            var cmp = a[i].CompareTo(b[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }
        return 0;
    }

    public static string GetCurrentVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly();
            var infoVersion = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVersion))
            {
                return Normalize(infoVersion);
            }
        }
        catch
        {
        }

        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                {
                    return Normalize(info.FileVersion);
                }
            }
        }
        catch
        {
        }

        return "0.0.0";
    }

    private static int[] ParseParts(string? version)
    {
        var parts = new int[3];
        if (string.IsNullOrWhiteSpace(version))
        {
            return parts;
        }

        var tokens = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length && i < 3; i++)
        {
            var token = tokens[i];
            var digits = "";
            foreach (var ch in token)
            {
                if (char.IsDigit(ch))
                {
                    digits += ch;
                }
                else
                {
                    break;
                }
            }

            if (int.TryParse(digits, out var value))
            {
                parts[i] = value;
            }
        }

        return parts;
    }
}
