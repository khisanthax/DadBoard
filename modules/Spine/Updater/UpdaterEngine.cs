using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DadBoard.Spine.Shared;

namespace DadBoard.Updater;

sealed class UpdaterEngine
{
    private readonly HttpClient _http;

    public UpdaterEngine()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<UpdaterResult> RunAsync(UpdateConfig config, CancellationToken ct, Action<string> log)
    {
        var manifestUrl = UpdateConfigStore.ResolveManifestUrl(config);
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return UpdaterResult.Failed("Update source not configured.");
        }

        log($"Channel={config.UpdateChannel} manifest={manifestUrl}");
        var installedVersion = VersionUtil.GetVersionFromFile(DadBoardPaths.InstalledExePath);
        log($"Installed version={installedVersion}");

        var manifest = await TryLoadManifestAsync(manifestUrl, ct, log).ConfigureAwait(false);
        if (manifest == null)
        {
            return UpdaterResult.Failed("Failed to load update manifest.");
        }

        var latest = VersionUtil.Normalize(manifest.LatestVersion);
        log($"Available version={latest}");

        if (VersionUtil.Compare(latest, installedVersion) <= 0)
        {
            return UpdaterResult.UpToDate(latest);
        }

        if (string.IsNullOrWhiteSpace(manifest.PackageUrl))
        {
            return UpdaterResult.Failed("Update manifest is missing package_url.");
        }

        var packageFile = GetPackagePath(manifest.PackageUrl, latest);
        log($"Downloading package to {packageFile}");
        await DownloadPackageAsync(manifest.PackageUrl, packageFile, ct, log).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            var expected = manifest.Sha256.Trim();
            var actual = HashUtil.ComputeSha256(packageFile);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return UpdaterResult.Failed("SHA256 mismatch for update package.");
            }
        }

        var setupExe = DadBoardPaths.SetupExePath;
        if (!File.Exists(setupExe))
        {
            log("DadBoardSetup.exe missing; downloading updater.");
            var ok = await EnsureSetupPresentAsync(manifestUrl, manifest.PackageUrl, setupExe, ct, log).ConfigureAwait(false);
            if (!ok || !File.Exists(setupExe))
            {
                return UpdaterResult.Failed("DadBoardSetup.exe not found.");
            }
        }

        var localManifestPath = WriteLocalManifest(manifest, packageFile, log);
        var exitCode = await LaunchSetupAsync(setupExe, localManifestPath, ct, log).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return UpdaterResult.Failed($"DadBoardSetup.exe exited with code {exitCode}.");
        }

        return UpdaterResult.Updated(latest);
    }

    private static string GetPackagePath(string packageUrl, string version)
    {
        var safeVersion = string.IsNullOrWhiteSpace(version) ? "latest" : version;
        var fileName = $"DadBoard-{safeVersion}.zip";
        if (TryResolveLocalPath(packageUrl, out var localPath))
        {
            fileName = Path.GetFileName(localPath);
        }

        Directory.CreateDirectory(DadBoardPaths.UpdateSourceDir);
        return Path.Combine(DadBoardPaths.UpdateSourceDir, fileName);
    }

    private async Task<UpdateManifest?> TryLoadManifestAsync(string manifestUrl, CancellationToken ct, Action<string> log)
    {
        try
        {
            if (TryResolveLocalPath(manifestUrl, out var localPath))
            {
                log($"Reading manifest from {localPath}");
                var json = await File.ReadAllTextAsync(localPath, ct).ConfigureAwait(false);
                return JsonSerializer.Deserialize<UpdateManifest>(json);
            }

            log($"Fetching manifest from {manifestUrl}");
            using var response = await _http.GetAsync(manifestUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<UpdateManifest>(content);
        }
        catch (Exception ex)
        {
            log($"Manifest load failed: {ex.Message}");
            return null;
        }
    }

    private async Task DownloadPackageAsync(string packageUrl, string destination, CancellationToken ct, Action<string> log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (TryResolveLocalPath(packageUrl, out var localPath))
        {
            log($"Copying package from {localPath}");
            File.Copy(localPath, destination, true);
            return;
        }

        using var response = await _http.GetAsync(packageUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    private static bool TryResolveLocalPath(string source, out string localPath)
    {
        localPath = "";
        if (File.Exists(source))
        {
            localPath = source;
            return true;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            localPath = uri.LocalPath;
            return File.Exists(localPath);
        }

        return false;
    }

    private static string? WriteLocalManifest(UpdateManifest manifest, string packagePath, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(DadBoardPaths.UpdateSourceDir);
            var localManifest = new UpdateManifest
            {
                LatestVersion = manifest.LatestVersion,
                PackageUrl = packagePath,
                Sha256 = manifest.Sha256,
                ForceCheckToken = manifest.ForceCheckToken,
                MinSupportedVersion = manifest.MinSupportedVersion
            };

            var path = Path.Combine(DadBoardPaths.UpdateSourceDir, "latest.local.json");
            var json = JsonSerializer.Serialize(localManifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            log($"Wrote local manifest {path}");
            return path;
        }
        catch (Exception ex)
        {
            log($"Failed to write local manifest: {ex.Message}");
            return null;
        }
    }

    private static async Task<int> LaunchSetupAsync(string setupExe, string? localManifestPath, CancellationToken ct, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(localManifestPath))
        {
            return 2;
        }

        var args = $"--update --silent --manifest \"{localManifestPath}\"";
        log($"Launching setup: {setupExe} {args}");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = setupExe,
            Arguments = args,
            WorkingDirectory = DadBoardPaths.InstallDir,
            UseShellExecute = true
        };

        var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null)
        {
            return 2;
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }

    private async Task<bool> EnsureSetupPresentAsync(
        string manifestUrl,
        string packageUrl,
        string setupExe,
        CancellationToken ct,
        Action<string> log)
    {
        var candidates = new[]
        {
            BuildSetupUrlFromManifest(manifestUrl),
            BuildSetupUrlFromManifest(packageUrl)
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                if (TryResolveLocalPath(candidate, out var localPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(setupExe)!);
                    File.Copy(localPath, setupExe, true);
                    log($"Copied updater from {localPath}");
                    return true;
                }

                using var response = await _http.GetAsync(candidate, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(setupExe)!);
                await File.WriteAllBytesAsync(setupExe, bytes, ct).ConfigureAwait(false);
                log($"Downloaded updater from {candidate}");
                return true;
            }
            catch (Exception ex)
            {
                log($"Failed to download updater from {candidate}: {ex.Message}");
            }
        }

        return false;
    }

    private static string BuildSetupUrlFromManifest(string? manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return "";
        }

        if (manifestUrl.EndsWith("latest.json", StringComparison.OrdinalIgnoreCase))
        {
            return manifestUrl.Substring(0, manifestUrl.Length - "latest.json".Length) + "DadBoardSetup.exe";
        }

        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var baseUrl = manifestUrl.Substring(0, manifestUrl.LastIndexOf('/') + 1);
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                return baseUrl + "DadBoardSetup.exe";
            }
        }

        return "";
    }
}

sealed class UpdaterResult
{
    public UpdaterState State { get; }
    public string Message { get; }
    public string? Version { get; }

    private UpdaterResult(UpdaterState state, string message, string? version)
    {
        State = state;
        Message = message;
        Version = version;
    }

    public static UpdaterResult UpToDate(string? version)
        => new(UpdaterState.UpToDate, "Already up to date.", version);

    public static UpdaterResult Updated(string? version)
        => new(UpdaterState.Updated, "Update applied.", version);

    public static UpdaterResult Failed(string message)
        => new(UpdaterState.Failed, message, null);
}

enum UpdaterState
{
    UpToDate,
    Updated,
    Failed
}
