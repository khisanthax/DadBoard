using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DadBoard.Spine.Shared;

namespace DadBoard.Setup;

public enum SetupAction
{
    Install,
    Update,
    Uninstall
}

public sealed class SetupResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Version { get; set; }
}

public static class SetupOperations
{
    private static readonly HttpClient Http = new();
    private const string MutexName = "Global\\DadBoard.SingleInstance";
    private const string ShutdownEventName = "Global\\DadBoard.Shutdown";

    public static async Task<SetupResult> RunAsync(
        SetupAction action,
        string? manifestUrl,
        SetupLogger logger,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (action == SetupAction.Uninstall)
            {
                progress?.Report("Stopping DadBoard...");
                logger.Info("Uninstall requested.");
                SignalShutdown(logger);
                WaitForAppExit(TimeSpan.FromSeconds(10), logger);

                if (Directory.Exists(DadBoardPaths.InstallDir))
                {
                    Directory.Delete(DadBoardPaths.InstallDir, true);
                    logger.Info($"Deleted {DadBoardPaths.InstallDir}");
                }

                progress?.Report("Uninstall complete.");
                return new SetupResult { Success = true };
            }

            var resolvedManifestUrl = ResolveManifestUrl(manifestUrl, logger);
            if (string.IsNullOrWhiteSpace(resolvedManifestUrl))
            {
                return new SetupResult
                {
                    Success = false,
                    Error = "Update source not configured."
                };
            }

            progress?.Report("Fetching update manifest...");
            var manifest = await LoadManifestAsync(resolvedManifestUrl, logger, cancellationToken).ConfigureAwait(false);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.PackageUrl))
            {
                return new SetupResult
                {
                    Success = false,
                    Error = "Invalid update manifest."
                };
            }

            var packageUrl = manifest.PackageUrl;
            var packageFile = GetPackagePath(packageUrl, manifest.LatestVersion);

            progress?.Report("Downloading package...");
            logger.Info($"Downloading package {packageUrl}");
            await DownloadPackageAsync(packageUrl, packageFile, logger, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                progress?.Report("Verifying package integrity...");
                var expected = manifest.Sha256.Trim();
                logger.Info($"Verifying package SHA256 expected={expected}");
                var actual = HashUtil.ComputeSha256(packageFile);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    logger.Error($"SHA256 mismatch expected={expected} actual={actual}");
                    return new SetupResult
                    {
                        Success = false,
                        Error = "SHA256 mismatch for update package."
                    };
                }
            }

            progress?.Report("Applying update...");
            logger.Info("Applying package.");
            ApplyPackage(packageFile, logger);

            var config = UpdateConfigStore.Load();
            config.ManifestUrl = resolvedManifestUrl;
            config.Source = string.IsNullOrWhiteSpace(resolvedManifestUrl) ? "" : "github_mirror";
            config.MirrorEnabled = !string.IsNullOrWhiteSpace(resolvedManifestUrl);
            UpdateConfigStore.Save(config);

            progress?.Report("Install complete.");
            return new SetupResult { Success = true, Version = manifest.LatestVersion };
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            return new SetupResult { Success = false, Error = ex.Message };
        }
    }

    private static string ResolveManifestUrl(string? manifestUrl, SetupLogger logger)
    {
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            return manifestUrl;
        }

        var config = UpdateConfigStore.Load();
        var resolved = UpdateConfigStore.ResolveManifestUrl(config);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            logger.Info($"Resolved manifest URL: {resolved}");
            return resolved;
        }

        var localManifest = Path.Combine(AppContext.BaseDirectory, "latest.json");
        if (File.Exists(localManifest))
        {
            logger.Info($"Using local manifest {localManifest}");
            return localManifest;
        }

        return "";
    }

    private static async Task<UpdateManifest?> LoadManifestAsync(string manifestUrl, SetupLogger logger, CancellationToken ct)
    {
        if (TryReadLocalFile(manifestUrl, out var json))
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json, JsonUtil.Options);
        }

        logger.Info($"Fetching manifest from {manifestUrl}");
        var response = await Http.GetAsync(manifestUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<UpdateManifest>(json, JsonUtil.Options);
    }

    private static async Task DownloadPackageAsync(string packageUrl, string destination, SetupLogger logger, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (TryResolveLocalPath(packageUrl, out var localPath))
        {
            File.Copy(localPath, destination, true);
            return;
        }

        using var response = await Http.GetAsync(packageUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    private static void ApplyPackage(string packagePath, SetupLogger logger)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("Package not found.", packagePath);
        }

        SignalShutdown(logger);
        WaitForAppExit(TimeSpan.FromSeconds(10), logger);

        var stagingDir = Path.Combine(DadBoardPaths.UpdateSourceDir, "staging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        ZipFile.ExtractToDirectory(packagePath, stagingDir, true);

        var stagedExe = Directory.GetFiles(stagingDir, "DadBoard.exe", SearchOption.AllDirectories);
        if (stagedExe.Length == 0)
        {
            throw new InvalidOperationException("Package does not contain DadBoard.exe");
        }

        Directory.CreateDirectory(DadBoardPaths.InstallDir);
        var backup = Path.Combine(DadBoardPaths.InstallDir, "DadBoard.old.exe");
        var runtimeExe = DadBoardPaths.InstalledExePath;
        if (File.Exists(runtimeExe))
        {
            File.Copy(runtimeExe, backup, true);
            logger.Info($"Backed up existing exe to {backup}");
        }

        CopyDirectory(stagingDir, DadBoardPaths.InstallDir);

        try
        {
            Directory.Delete(stagingDir, true);
        }
        catch
        {
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

        var updateDir = DadBoardPaths.UpdateSourceDir;
        Directory.CreateDirectory(updateDir);
        return Path.Combine(updateDir, fileName);
    }

    private static bool TryReadLocalFile(string source, out string content)
    {
        content = "";
        if (TryResolveLocalPath(source, out var localPath) && File.Exists(localPath))
        {
            content = File.ReadAllText(localPath);
            return true;
        }

        return false;
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
            return true;
        }

        return false;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var targetDir = directory.Replace(sourceDir, destinationDir, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(targetDir);
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var targetPath = file.Replace(sourceDir, destinationDir, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, true);
        }
    }

    private static void SignalShutdown(SetupLogger logger)
    {
        try
        {
            using var shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
            shutdownEvent.Set();
            logger.Info("Signaled running DadBoard to shut down.");
        }
        catch (Exception ex)
        {
            logger.Warn($"Shutdown signal failed: {ex.Message}");
        }
    }

    private static void WaitForAppExit(TimeSpan timeout, SetupLogger logger)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (!Mutex.TryOpenExisting(MutexName, out var mutex))
                {
                    return;
                }

                mutex.Dispose();
            }
            catch
            {
                return;
            }

            Thread.Sleep(250);
        }

        logger.Warn("DadBoard process still running after timeout.");
    }
}
