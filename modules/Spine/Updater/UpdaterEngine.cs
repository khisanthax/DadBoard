using System;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DadBoard.Spine.Shared;

namespace DadBoard.Updater;

sealed class UpdaterEngine
{
    private const int ManifestDownloadTimeoutSeconds = 120;
    private const int PackageDownloadOverallTimeoutMinutes = 60;
    private const int PackageDownloadStallTimeoutSeconds = 300;
    private const int PackageDownloadMaxAttempts = 5;
    private const long PackageDownloadLogIntervalBytes = 5 * 1024 * 1024;
    private readonly HttpClient _http;

    public UpdaterEngine()
    {
        _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<UpdaterResult> RunAsync(
        UpdateConfig config,
        bool forceRepair,
        string action,
        string invocation,
        bool detachSetup,
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

            if (!PackageContainsFile(packageFile, "DadBoard.exe"))
            {
                return Fail(status, UpdaterExitCode.DownloadFailure, "missing_payload", "Update package missing DadBoard.exe.", log);
            }
            if (!PackageContainsFile(packageFile, "DadBoardUpdater.exe"))
            {
                return Fail(status, UpdaterExitCode.DownloadFailure, "missing_updater", "Update package missing DadBoardUpdater.exe.", log);
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
            status.SetupLogPath = Path.Combine(DadBoardPaths.SetupLogDir, "setup.log");

            var exitCode = await LaunchSetupAsync(setupExe, packageFile, ct, log, waitForExit: !detachSetup).ConfigureAwait(false);
            status.SetupExitCode = detachSetup ? null : exitCode;
            if (!detachSetup && exitCode != 0)
            {
                return Fail(status, UpdaterExitCode.SetupFailed, "setup_failed", $"DadBoardSetup.exe exited with code {exitCode}.", log);
            }

            if (detachSetup)
            {
                status.Success = true;
                status.ExitCode = (int)UpdaterExitCode.Success;
                status.Result = "installing";
                status.Action = "invoked_setup";
                status.Message = "Setup launched.";
                status.AvailableVersion = latest;
                SaveStatus(status, log);
                return UpdaterResult.InvokedSetup(latest, UpdaterExitCode.Success);
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
            using var manifestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            manifestCts.CancelAfter(TimeSpan.FromSeconds(ManifestDownloadTimeoutSeconds));
            using var response = await _http.GetAsync(manifestUrl, manifestCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(manifestCts.Token).ConfigureAwait(false);
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

        await DownloadHttpFileWithRetriesAsync(packageUrl, destination, ct, log).ConfigureAwait(false);
    }

    private async Task DownloadHttpFileWithRetriesAsync(string url, string destination, CancellationToken ct, Action<string> log)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= PackageDownloadMaxAttempts; attempt++)
        {
            try
            {
                using var http = new HttpClient
                {
                    Timeout = Timeout.InfiniteTimeSpan
                };
                using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                overallCts.CancelAfter(TimeSpan.FromMinutes(PackageDownloadOverallTimeoutMinutes));
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, overallCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;
                await using var stream = await response.Content.ReadAsStreamAsync(overallCts.Token).ConfigureAwait(false);
                await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                await CopyWithStallTimeoutAsync(stream, file, length, overallCts.Token, log).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < PackageDownloadMaxAttempts &&
                                       ex is HttpRequestException or TaskCanceledException or IOException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                lastError = ex;
                var delaySeconds = 2 * attempt;
                log($"Package download failed (attempt {attempt}/{PackageDownloadMaxAttempts}): {ex.Message}. Retrying in {delaySeconds}s...");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
        }

        throw new IOException($"Package download failed after retries: {lastError?.Message}");
    }

    private static async Task CopyWithStallTimeoutAsync(Stream source, Stream destination, long? contentLength, CancellationToken ct, Action<string> log)
    {
        var buffer = new byte[1024 * 64];
        var stallTimeout = TimeSpan.FromSeconds(PackageDownloadStallTimeoutSeconds);
        long totalRead = 0;
        long nextLog = PackageDownloadLogIntervalBytes;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stallCts.CancelAfter(stallTimeout);
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), stallCts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                totalRead += read;
                if (totalRead >= nextLog)
                {
                    if (contentLength.HasValue && contentLength.Value > 0)
                    {
                        var percent = (int)Math.Round(totalRead / (double)contentLength.Value * 100);
                        log($"Download progress: {totalRead / (1024 * 1024)} MB ({percent}%).");
                    }
                    else
                    {
                        log($"Download progress: {totalRead / (1024 * 1024)} MB.");
                    }

                    nextLog += PackageDownloadLogIntervalBytes;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log($"Package download stalled for {PackageDownloadStallTimeoutSeconds}s.");
                throw new TimeoutException($"Package download stalled for {PackageDownloadStallTimeoutSeconds}s.");
            }
        }
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

    private static bool PackageContainsFile(string packagePath, string fileName)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            return archive.Entries.Any(entry =>
                entry.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> LaunchSetupAsync(string setupExe, string payloadPath, CancellationToken ct, Action<string> log, bool waitForExit)
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

        if (!waitForExit)
        {
            return 0;
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

                Directory.CreateDirectory(Path.GetDirectoryName(setupExe)!);
                await DownloadHttpFileWithRetriesAsync(candidate, setupExe, ct, log).ConfigureAwait(false);
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

    public static UpdaterResult InvokedSetup(string? version, UpdaterExitCode exitCode)
        => new(UpdaterState.InvokedSetup, "Setup launched.", version, exitCode);

    public static UpdaterResult Failed(string message, UpdaterExitCode exitCode)
        => new(UpdaterState.Failed, message, null, exitCode);
}

enum UpdaterState
{
    UpToDate,
    Updated,
    InvokedSetup,
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
