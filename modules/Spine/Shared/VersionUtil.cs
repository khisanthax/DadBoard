using System;
using System.Reflection;

namespace DadBoard.Spine.Shared;

public static class VersionUtil
{
    public static string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "0.0.0";
        return Normalize(version);
    }

    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "0.0.0";
        }

        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var values = new int[3];
        for (var i = 0; i < values.Length; i++)
        {
            if (i < parts.Length && int.TryParse(parts[i], out var parsed))
            {
                values[i] = parsed;
            }
            else
            {
                values[i] = 0;
            }
        }

        return $"{values[0]}.{values[1]}.{values[2]}";
    }

    public static int Compare(string? left, string? right)
    {
        var leftParts = ParseParts(Normalize(left));
        var rightParts = ParseParts(Normalize(right));

        for (var i = 0; i < 3; i++)
        {
            var diff = leftParts[i].CompareTo(rightParts[i]);
            if (diff != 0)
            {
                return diff;
            }
        }

        return 0;
    }

    private static int[] ParseParts(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var values = new int[3];
        for (var i = 0; i < values.Length; i++)
        {
            if (i < parts.Length && int.TryParse(parts[i], out var parsed))
            {
                values[i] = parsed;
            }
            else
            {
                values[i] = 0;
            }
        }

        return values;
    }
}
