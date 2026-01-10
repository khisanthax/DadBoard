using System;
using System.Diagnostics;
using System.Reflection;

namespace DadBoard.Spine.Shared;

public static class VersionUtil
{
    public static string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            return Normalize(info);
        }

        var version = assembly.GetName().Version?.ToString() ?? "0.0.0";
        return Normalize(version);
    }

    public static string Normalize(string? version)
    {
        if (!TryParse(version, out var semver))
        {
            return "0.0.0";
        }

        return semver.ToString();
    }

    public static int Compare(string? left, string? right)
    {
        TryParse(left, out var leftSemver);
        TryParse(right, out var rightSemver);
        return leftSemver.CompareTo(rightSemver);
    }

    public static string GetVersionFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return "0.0.0";
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var raw = info.ProductVersion;
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = info.FileVersion;
            }

            return Normalize(raw);
        }
        catch
        {
            return "0.0.0";
        }
    }

    private static bool TryParse(string? version, out SemVer semver)
    {
        semver = default;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var input = version.Trim();
        if (input.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            input = input.Substring(1);
        }

        var build = "";
        var plusIndex = input.IndexOf('+');
        if (plusIndex >= 0)
        {
            build = input.Substring(plusIndex + 1);
            input = input.Substring(0, plusIndex);
        }

        var prerelease = "";
        var dashIndex = input.IndexOf('-');
        if (dashIndex >= 0)
        {
            prerelease = input.Substring(dashIndex + 1);
            input = input.Substring(0, dashIndex);
        }

        var parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major))
        {
            return false;
        }

        var minor = parts.Length > 1 && int.TryParse(parts[1], out var parsedMinor) ? parsedMinor : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var parsedPatch) ? parsedPatch : 0;

        var identifiers = string.IsNullOrWhiteSpace(prerelease)
            ? Array.Empty<string>()
            : prerelease.Split('.', StringSplitOptions.RemoveEmptyEntries);

        semver = new SemVer(major, minor, patch, identifiers, build);
        return true;
    }

    private readonly struct SemVer
    {
        public readonly int Major;
        public readonly int Minor;
        public readonly int Patch;
        public readonly string[] PreRelease;
        public readonly string Build;

        public SemVer(int major, int minor, int patch, string[] preRelease, string build)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            PreRelease = preRelease;
            Build = build;
        }

        public int CompareTo(SemVer other)
        {
            var diff = Major.CompareTo(other.Major);
            if (diff != 0)
            {
                return diff;
            }

            diff = Minor.CompareTo(other.Minor);
            if (diff != 0)
            {
                return diff;
            }

            diff = Patch.CompareTo(other.Patch);
            if (diff != 0)
            {
                return diff;
            }

            var thisHasPre = PreRelease.Length > 0;
            var otherHasPre = other.PreRelease.Length > 0;
            if (!thisHasPre && !otherHasPre)
            {
                return 0;
            }

            if (thisHasPre && !otherHasPre)
            {
                return -1;
            }

            if (!thisHasPre && otherHasPre)
            {
                return 1;
            }

            var max = Math.Max(PreRelease.Length, other.PreRelease.Length);
            for (var i = 0; i < max; i++)
            {
                if (i >= PreRelease.Length)
                {
                    return -1;
                }

                if (i >= other.PreRelease.Length)
                {
                    return 1;
                }

                var left = PreRelease[i];
                var right = other.PreRelease[i];
                var leftIsNumeric = int.TryParse(left, out var leftNum);
                var rightIsNumeric = int.TryParse(right, out var rightNum);

                if (leftIsNumeric && rightIsNumeric)
                {
                    diff = leftNum.CompareTo(rightNum);
                }
                else if (leftIsNumeric && !rightIsNumeric)
                {
                    diff = -1;
                }
                else if (!leftIsNumeric && rightIsNumeric)
                {
                    diff = 1;
                }
                else
                {
                    diff = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
                }

                if (diff != 0)
                {
                    return diff;
                }
            }

            return 0;
        }

        public override string ToString()
        {
            var core = $"{Major}.{Minor}.{Patch}";
            if (PreRelease.Length == 0)
            {
                return core;
            }

            return $"{core}-{string.Join('.', PreRelease)}";
        }
    }
}
