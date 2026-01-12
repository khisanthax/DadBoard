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

    public async Task<UpdaterResult> RunAsync(
        UpdateConfig config,
        bool forceRepair,
        string action,
        string invocation,
        string logPath,
        CancellationToken ct,
        Action<string> log)
    {
        var status = new UpdaterStatus
        {
            SchemaVersion = 1,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            Action = action,
            Invocation = invocation,
            Channel = config.UpdateChannel.ToString().ToLowerInvariant(),
            LogPath = logPath ?? ""
        };

        try
        {
            var manifestUrl = UpdateConfigStore.ResolveManifestUrl(config);
            status.ManifestUrl = manifestUrl ?? "";
            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                return Fail(status, UpdaterExitCode.NetworkFailure, "network_failure", "Update source not configured.", log);
            }

            log($"Channel={config.UpdateChannel} manifest={manifestUrl}");
            var installedVersion = VersionUtil.GetVersionFromFile(DadBoardPaths.InstalledExePath);
            installedVersion = VersionUtil.Normalize(installedVersion);
            status.InstalledVersion = installedVersion;
            log($"Installed version={installedVersion}");

            var manifest = await TryLoadManifestAsync(manifestUrl, ct, log).ConfigureAwait(false);
            if (manifest == null)
            {
                return Fail(status, UpdaterExitCode.NetworkFailure, "network_failure", "Failed to load update manifest.", log);
            }

            var latest = VersionUtil.Normalize(manifest.LatestVersion);
            status.LatestVersion = latest;
            log($"Available version={latest}");

            if (!forceRepair && VersionUtil.Compare(latest, installedVersion) <= 0)
            {
                status.Success = true;
                status.ExitCode = (int)UpdaterExitCode.Success;
                status.Result = "up-to-date";
                status.Action = "checked";
                status.Message = "Already up to date.";
                status.AvailableVersion = latest;
                SaveStatus(status, log);
                return UpdaterResult.UpToDate(latest, UpdaterExitCode.Success);
            }

            if (string.IsNullOrWhiteSpace(manifest.PackageUrl))
            {
                return Fail(status, UpdaterExitCode.NetworkFailure, "network_failure", "Update manifest is missing package_url.", log);
            }

            if (forceRepair)
            {
                log("Force repair enabled: applying latest package.");
            }

            var packageFile = GetPackagePath(manifest.PackageUrl, latest);
            status.PayloadPath = packageFile;
            log($"Downloading package to {packageFile}");
            try
            {
                await DownloadPackageAsync(manifest.PackageUrl, packageFile, ct, log).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Fail(status, UpdaterExitCode.DownloadFailure, "download_failure", ex.Message, log);
            }

            if (!string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                var expected = manifest.Sha256.Trim();
                var actual = HashUtil.ComputeSha256(packageFile);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(status, UpdaterExitCode.DownloadFailure, "download_failure", "SHA256 mismatch for update package.", log);
                }
            }

            var stagedSetup = Path.Combine(DadBoardPaths.UpdateSourceDir, "DadBoardSetup.exe");
            log("Ensuring latest DadBoardSetup.exe is available...");
            var ok = await EnsureSetupPresentAsync(manifestUrl, manifest.PackageUrl, stagedSetup, ct, log).ConfigureAwait(false);
            var setupExe = ok && File.Exists(stagedSetup) ? stagedSetup : DadBoardPaths.SetupExePath;
            if (!File.Exists(setupExe))
            {
                return Fail(status, UpdaterExitCode.SetupInvokeFailure, "setup_not_found", "DadBoardSetup.exe not found.", log);
            }
            if (!string.Equals(setupExe, DadBoardPaths.SetupExePath, StringComparison.OrdinalIgnoreCase))
            {
                log($"Using staged setup: {setupExe}");
            }

            status.Action = forceRepair ? "repair" : "invoked_setup";
            var exitCode = await LaunchSetupAsync(setupExe, packageFile, ct, log).ConfigureAwait(false);
            status.SetupExitCode = exitCode;
            if (exitCode != 0)
            {
                return Fail(status, UpdaterExitCode.SetupFailed, "setup_failed", $"DadBoardSetup.exe exited with code {exitCode}.", log);
            }

            status.Success = true;
            status.ExitCode = (int)UpdaterExitCode.Success;
            status.Result = "updated";
            status.Action = forceRepair ? "repair" : "updated";
            status.Message = forceRepair ? "Repair applied." : "Update applied.";
            status.AvailableVersion = latest;
            SaveStatus(status, log);
            return UpdaterResult.Updated(latest, UpdaterExitCode.Success);
        }
        catch (Exception ex)
        {
            return Fail(status, UpdaterExitCode.UnknownFailure, "unknown_failure", ex.Message, log, ex);
        }
    }

    private static UpdaterResult Fail(UpdaterStatus status, UpdaterExitCode code, string errorCode, string message, Action<string> log, Exception? ex = null)
    {
        status.Success = false;
        status.ExitCode = (int)code;
        status.ErrorCode = errorCode;
        status.ErrorMessage = message;
        status.Result = "failed";
        status.Action = "failed";
        status.Message = message;
        SaveStatus(status, log);
        log(ex == null ? message : $"Updater failed: {ex}");
        return UpdaterResult.Failed(message, code);
    }

    private static void SaveStatus(UpdaterStatus status, Action<string> log)
    {
        if (!UpdaterStatusStore.Save(status))
        {
            log("Failed to write last_result.json.");
        }
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

    private static async Task<int> LaunchSetupAsync(string setupExe, string payloadPath, CancellationToken ct, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            return 2;
        }

        var args = $"repair --payload \"{payloadPath}\" --silent";
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
                    FileUnblocker.TryUnblock(setupExe, log);
                    log($"Copied setup from {localPath}");
                    return true;
                }

                using var response = await _http.GetAsync(candidate, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(setupExe)!);
                await File.WriteAllBytesAsync(setupExe, bytes, ct).ConfigureAwait(false);
                FileUnblocker.TryUnblock(setupExe, log);
                log($"Downloaded setup from {candidate}");
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
    public UpdaterExitCode ExitCode { get; }

    private UpdaterResult(UpdaterState state, string message, string? version, UpdaterExitCode exitCode)
    {
        State = state;
        Message = message;
        Version = version;
        ExitCode = exitCode;
    }

    public static UpdaterResult UpToDate(string? version, UpdaterExitCode exitCode)
        => new(UpdaterState.UpToDate, "Already up to date.", version, exitCode);

    public static UpdaterResult Updated(string? version, UpdaterExitCode exitCode)
        => new(UpdaterState.Updated, "Update applied.", version, exitCode);

    public static UpdaterResult Failed(string message, UpdaterExitCode exitCode)
        => new(UpdaterState.Failed, message, null, exitCode);
}

enum UpdaterState
{
    UpToDate,
    Updated,
    Failed
}

enum UpdaterExitCode
{
    Success = 0,
    InvalidArgs = 2,
    NetworkFailure = 3,
    DownloadFailure = 4,
    SetupInvokeFailure = 5,
    SetupFailed = 6,
    StatusWriteFailure = 7,
    UnknownFailure = 8
}
