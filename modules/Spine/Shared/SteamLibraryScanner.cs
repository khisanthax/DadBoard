using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DadBoard.Spine.Shared;

public sealed class SteamScanResult
{
    public string? SteamPath { get; set; }
    public string[] LibraryPaths { get; set; } = Array.Empty<string>();
    public SteamGameEntry[] Games { get; set; } = Array.Empty<SteamGameEntry>();
    public int ManifestCount { get; set; }
    public string? Error { get; set; }
}

public static class SteamLibraryScanner
{
    private static readonly Regex KeyValueRegex = new("\"(?<key>[^\"]+)\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex PathRegex = new("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OldPathRegex = new("\"\\d+\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.Compiled);

    public static SteamScanResult ScanInstalledGames()
    {
        var steamPath = FindSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return new SteamScanResult
            {
                SteamPath = null,
                LibraryPaths = Array.Empty<string>(),
                Games = Array.Empty<SteamGameEntry>(),
                ManifestCount = 0,
                Error = "Steam path not found."
            };
        }
        var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(steamPath) && Directory.Exists(steamPath))
        {
            libraryPaths.Add(steamPath);
        }

        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            foreach (var path in ReadLibraryPaths(libraryFile))
            {
                if (Directory.Exists(path))
                {
                    libraryPaths.Add(path);
                }
            }
        }

        var games = new Dictionary<int, SteamGameEntry>();
        var manifestCount = 0;
        foreach (var library in libraryPaths)
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps))
            {
                continue;
            }

            try
            {
                foreach (var manifest in Directory.GetFiles(steamApps, "appmanifest_*.acf"))
                {
                    manifestCount++;
                    if (!TryParseManifest(manifest, out var appId, out var name, out var installDir))
                    {
                        continue;
                    }

                    if (!games.TryGetValue(appId, out var entry))
                    {
                        var resolvedInstall = string.IsNullOrWhiteSpace(installDir)
                            ? null
                            : Path.Combine(library, "steamapps", "common", installDir);
                        entry = new SteamGameEntry { AppId = appId, Name = name, InstallDir = resolvedInstall };
                        games[appId] = entry;
                    }
                    else if (string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(name))
                    {
                        entry.Name = name;
                    }
                    else if (string.IsNullOrWhiteSpace(entry.InstallDir) && !string.IsNullOrWhiteSpace(installDir))
                    {
                        entry.InstallDir = Path.Combine(library, "steamapps", "common", installDir);
                    }
                }
            }
            catch
            {
                continue;
            }
        }

        return new SteamScanResult
        {
            SteamPath = steamPath,
            LibraryPaths = new List<string>(libraryPaths).ToArray(),
            Games = new List<SteamGameEntry>(games.Values).ToArray(),
            ManifestCount = manifestCount
        };
    }

    public static string? GetSteamPath()
    {
        return FindSteamPath();
    }

    private static string? FindSteamPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var path = ReadRegistryPath(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (IsValidSteamPath(path))
        {
            return path;
        }

        path = ReadRegistryPath(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (IsValidSteamPath(path))
        {
            return path;
        }

        path = ReadRegistryPath(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (IsValidSteamPath(path))
        {
            return path;
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            var defaultPath = Path.Combine(programFilesX86, "Steam");
            if (IsValidSteamPath(defaultPath))
            {
                return defaultPath;
            }
        }

        return null;
    }

    private static bool IsValidSteamPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(path, "steamapps"));
    }

    private static string? ReadRegistryPath(RegistryKey root, string subKey, string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(value)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadLibraryPaths(string libraryFile)
    {
        if (!File.Exists(libraryFile))
        {
            yield break;
        }

        foreach (var line in File.ReadAllLines(libraryFile))
        {
            var match = PathRegex.Match(line);
            if (match.Success)
            {
                yield return NormalizePath(match.Groups["path"].Value);
                continue;
            }

            var oldMatch = OldPathRegex.Match(line);
            if (oldMatch.Success && oldMatch.Groups["path"].Value.Contains("\\"))
            {
                yield return NormalizePath(oldMatch.Groups["path"].Value);
            }
        }
    }

    private static string NormalizePath(string path)
    {
        return path.Replace("\\\\", "\\");
    }

    private static bool TryParseManifest(string manifestPath, out int appId, out string? name, out string? installDir)
    {
        appId = 0;
        name = null;
        installDir = null;

        var fileName = Path.GetFileNameWithoutExtension(manifestPath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var parts = fileName.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[1], out var parsed))
            {
                appId = parsed;
            }
        }

        try
        {
            foreach (var line in File.ReadAllLines(manifestPath))
            {
                var match = KeyValueRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var key = match.Groups["key"].Value;
                var value = match.Groups["value"].Value;

                if (string.Equals(key, "appid", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, out var parsedId))
                {
                    appId = parsedId;
                }

                if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                {
                    name = value;
                }

                if (string.Equals(key, "installdir", StringComparison.OrdinalIgnoreCase))
                {
                    installDir = value;
                }
            }
        }
        catch
        {
            return false;
        }

        return appId > 0;
    }

    public static bool TryGetInstallDir(int appId, out string? installDir)
    {
        installDir = null;
        var steamPath = FindSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return false;
        }

        var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(steamPath))
        {
            libraryPaths.Add(steamPath);
        }

        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        foreach (var path in ReadLibraryPaths(libraryFile))
        {
            if (Directory.Exists(path))
            {
                libraryPaths.Add(path);
            }
        }

        foreach (var library in libraryPaths)
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps))
            {
                continue;
            }

            var manifest = Path.Combine(steamApps, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest))
            {
                continue;
            }

            if (!TryParseManifest(manifest, out var foundId, out _, out var foundInstall))
            {
                continue;
            }

            if (foundId != appId || string.IsNullOrWhiteSpace(foundInstall))
            {
                continue;
            }

            installDir = Path.Combine(library, "steamapps", "common", foundInstall);
            return true;
        }

        return false;
    }
}
