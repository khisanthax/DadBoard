using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
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

            var config = UpdateConfigStore.Load();
            var (primaryManifestUrl, fallbackManifestUrl) = ResolveManifestUrls(manifestUrl, config, logger);
            if (string.IsNullOrWhiteSpace(primaryManifestUrl) && string.IsNullOrWhiteSpace(fallbackManifestUrl))
            {
                return new SetupResult
                {
                    Success = false,
                    Error = "Update source not configured."
                };
            }

            progress?.Report("Fetching update manifest...");
            var (manifest, manifestError, usedManifestUrl) =
                await LoadManifestWithFallbackAsync(primaryManifestUrl, fallbackManifestUrl, logger, cancellationToken)
                    .ConfigureAwait(false);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.PackageUrl))
            {
                return new SetupResult
                {
                    Success = false,
                    Error = manifestError ?? "Invalid update manifest."
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

            var defaultUrl = UpdateConfigStore.GetDefaultManifestUrl(config.UpdateChannel);
            config.ManifestUrl = string.Equals(usedManifestUrl ?? primaryManifestUrl, defaultUrl, StringComparison.OrdinalIgnoreCase)
                ? ""
                : usedManifestUrl ?? primaryManifestUrl;
            config.Source = "github_mirror";
            config.MirrorEnabled = true;
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

    private static (string Primary, string Fallback) ResolveManifestUrls(
        string? manifestUrl,
        UpdateConfig config,
        SetupLogger logger)
    {
        var resolved = UpdateConfigStore.ResolveManifestUrl(config);
        if (config.UpdateSourceMode == UpdateSourceMode.GithubOnly)
        {
            logger.Info("Update source mode: GithubOnly");
            return (resolved, "");
        }

        var primary = !string.IsNullOrWhiteSpace(manifestUrl)
            ? manifestUrl
            : BuildLocalLeaderManifestUrl(config);
        var fallback = resolved;
        if (string.Equals(primary, fallback, StringComparison.OrdinalIgnoreCase))
        {
            fallback = "";
        }

        logger.Info($"Update source mode: Auto primary={primary} fallback={fallback}");
        return (primary, fallback);
    }

    private static string BuildLocalLeaderManifestUrl(UpdateConfig config)
    {
        var host = string.IsNullOrWhiteSpace(config.LocalHostIp) ? "127.0.0.1" : config.LocalHostIp;
        var port = config.LocalHostPort > 0 ? config.LocalHostPort : 45555;
        return $"http://{host}:{port}/updates/latest.json";
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

    private static async Task<(UpdateManifest? Manifest, string? Error, string? UsedUrl)> LoadManifestWithFallbackAsync(
        string primaryUrl,
        string fallbackUrl,
        SetupLogger logger,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(primaryUrl))
        {
            var (manifest, error) = await TryLoadManifestAsync(primaryUrl, logger, ct).ConfigureAwait(false);
            if (manifest != null)
            {
                logger.Info($"Manifest loaded from {primaryUrl}");
                return (manifest, null, primaryUrl);
            }

            logger.Warn($"Manifest fetch failed ({primaryUrl}): {error}");
            if (IsLocalLeaderManifest(primaryUrl, out var host, out var port))
            {
                var leaderReady = await EnsureLeaderRunningAsync(host, port, logger, ct).ConfigureAwait(false);
                if (leaderReady)
                {
                    var (retryManifest, retryError) = await TryLoadManifestAsync(primaryUrl, logger, ct).ConfigureAwait(false);
                    if (retryManifest != null)
                    {
                        logger.Info("Manifest loaded after starting DadBoard.");
                        return (retryManifest, null, primaryUrl);
                    }

                    logger.Warn($"Manifest retry failed: {retryError}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackUrl) &&
            !string.Equals(primaryUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
        {
            var (fallbackManifest, fallbackError) = await TryLoadManifestAsync(fallbackUrl, logger, ct).ConfigureAwait(false);
            if (fallbackManifest != null)
            {
                logger.Info($"Manifest loaded from fallback {fallbackUrl}");
                return (fallbackManifest, null, fallbackUrl);
            }

            logger.Warn($"Fallback manifest fetch failed ({fallbackUrl}): {fallbackError}");
            return (null, fallbackError ?? "Fallback manifest unavailable.", null);
        }

        return (null, "Manifest unavailable.", null);
    }

    private static async Task<(UpdateManifest? Manifest, string? Error)> TryLoadManifestAsync(
        string manifestUrl,
        SetupLogger logger,
        CancellationToken ct)
    {
        try
        {
            var manifest = await LoadManifestAsync(manifestUrl, logger, ct).ConfigureAwait(false);
            return (manifest, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static bool IsLocalLeaderManifest(string manifestUrl, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!uri.AbsolutePath.EndsWith("/updates/latest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        host = uri.Host;
        port = uri.Port;
        return IsLocalHost(host);
    }

    private static bool IsLocalHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
            return hostEntry.AddressList.Any(ip => ip.AddressFamily == AddressFamily.InterNetwork &&
                                                   string.Equals(ip.ToString(), host, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> EnsureLeaderRunningAsync(string host, int port, SetupLogger logger, CancellationToken ct)
    {
        if (await IsPortOpenAsync(host, port, ct).ConfigureAwait(false))
        {
            logger.Info($"Leader already listening on {host}:{port}");
            return true;
        }

        var started = TryStartDadBoard(logger);
        if (!started)
        {
            logger.Warn("Failed to start DadBoard; falling back to GitHub.");
            return false;
        }

        logger.Info("Waiting for Leader to start...");
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await IsPortOpenAsync(host, port, ct).ConfigureAwait(false))
            {
                logger.Info("Leader is now listening.");
                return true;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        logger.Warn("Leader did not start within timeout.");
        return false;
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(500, ct)).ConfigureAwait(false);
            if (completed != connectTask)
            {
                return false;
            }

            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStartDadBoard(SetupLogger logger)
    {
        var exePath = DadBoardPaths.InstalledExePath;
        if (!File.Exists(exePath))
        {
            logger.Warn($"DadBoard.exe not found at {exePath}");
            return false;
        }

        if (Process.GetProcessesByName("DadBoard").Length > 0)
        {
            logger.Info("DadBoard process already running.");
            return true;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = DadBoardPaths.InstallDir,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            logger.Info("DadBoard started to bring up Leader mirror.");
            return true;
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to start DadBoard: {ex.Message}");
            return false;
        }
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

        if (!StopDadBoardProcesses(logger, TimeSpan.FromSeconds(30)))
        {
            throw new IOException("DadBoard.exe did not release file lock after shutdown/kill.");
        }

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

        CopyDirectoryWithRetries(stagingDir, DadBoardPaths.InstallDir, logger);
        CopySetupIntoInstallDir(logger);
        CreateDesktopShortcut(logger);
        RestartDadBoard(logger);

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

    private static void CopyDirectoryWithRetries(string sourceDir, string destinationDir, SetupLogger logger)
    {
        const int attempts = 5;
        for (var i = 1; i <= attempts; i++)
        {
            try
            {
                logger.Info($"Replacing files (attempt {i}/{attempts})...");
                CopyDirectory(sourceDir, destinationDir);
                logger.Info("Replace succeeded.");
                return;
            }
            catch (Exception ex)
            {
                logger.Warn($"Replace failed (attempt {i}/{attempts}): {ex.Message}");
                Thread.Sleep(300);
            }
        }

        throw new IOException("DadBoard files are still locked. Ensure the app is closed and retry.");
    }

    private static bool StopDadBoardProcesses(SetupLogger logger, TimeSpan timeout)
    {
        logger.Info("Stopping DadBoard...");
        var processes = GetDadBoardProcesses();
        if (processes.Count == 0)
        {
            logger.Info("No running DadBoard processes found.");
            return true;
        }

        var pids = string.Join(",", processes.Select(p => p.Id));
        logger.Info($"DadBoard PIDs: {pids}");

        SignalShutdown(logger);
        logger.Info("Graceful shutdown requested.");
        foreach (var process in processes)
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    process.CloseMainWindow();
                }
            }
            catch
            {
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetDadBoardProcesses().Count == 0)
            {
                logger.Info("DadBoard stopped gracefully.");
                break;
            }

            Thread.Sleep(500);
        }

        if (GetDadBoardProcesses().Count > 0)
        {
            logger.Warn("Graceful shutdown timed out; forcing termination.");
            foreach (var process in GetDadBoardProcesses())
            {
                try
                {
                    process.Kill(true);
                    logger.Warn($"Force kill applied pid={process.Id}");
                }
                catch (Exception ex)
                {
                    logger.Warn($"Force kill failed pid={process.Id}: {ex.Message}");
                }
            }
        }

        if (!WaitForFileUnlock(DadBoardPaths.InstalledExePath, TimeSpan.FromSeconds(5), logger))
        {
            logger.Warn("DadBoard.exe did not release file lock after shutdown/kill.");
            return false;
        }

        logger.Info("File lock released.");
        return true;
    }

    private static bool WaitForFileUnlock(string path, TimeSpan timeout, SetupLogger logger)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return true;
        }

        logger.Info("Waiting for DadBoard.exe to exit...");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }

        return false;
    }

    private static List<Process> GetDadBoardProcesses()
    {
        var list = new List<Process>();
        foreach (var process in Process.GetProcessesByName("DadBoard"))
        {
            try
            {
                var path = process.MainModule?.FileName ?? "";
                if (string.IsNullOrWhiteSpace(path) ||
                    string.Equals(Path.GetFullPath(path), Path.GetFullPath(DadBoardPaths.InstalledExePath), StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(process);
                }
            }
            catch
            {
                list.Add(process);
            }
        }

        return list;
    }

    private static void RestartDadBoard(SetupLogger logger)
    {
        var exePath = DadBoardPaths.InstalledExePath;
        if (!File.Exists(exePath))
        {
            logger.Warn($"Restart skipped: {exePath} missing.");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = DadBoardPaths.InstallDir,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            logger.Info("DadBoard restarted.");
        }
        catch (Exception ex)
        {
            logger.Warn($"DadBoard restart failed: {ex.Message}");
        }
    }

    private static void CopySetupIntoInstallDir(SetupLogger logger)
    {
        try
        {
            var setupSource = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrWhiteSpace(setupSource) || !File.Exists(setupSource))
            {
                logger.Warn("Setup copy skipped: current process path unavailable.");
                return;
            }

            var setupDest = DadBoardPaths.SetupExePath;
            if (string.Equals(Path.GetFullPath(setupSource), Path.GetFullPath(setupDest), StringComparison.OrdinalIgnoreCase))
            {
                logger.Info("Setup already in install dir.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(setupDest)!);
            File.Copy(setupSource, setupDest, true);
            logger.Info($"Copied setup into install dir: {setupDest}");
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to copy setup into install dir: {ex.Message}");
        }
    }

    private static void CreateDesktopShortcut(SetupLogger logger)
    {
        try
        {
            var exePath = DadBoardPaths.InstalledExePath;
            const string shortcutArgs = "--mode agent --minimized --no-first-run";
            if (!File.Exists(exePath))
            {
                logger.Warn($"Desktop shortcut skipped: target missing {exePath}");
                return;
            }

            var locations = ResolveDesktopDirectories();
            if (locations.Length == 0)
            {
                logger.Warn("Desktop shortcut skipped: no desktop directory found.");
                return;
            }

            if (HasValidShortcut(locations, exePath, shortcutArgs, logger))
            {
                logger.Info("Desktop shortcut already valid.");
                return;
            }

            var preferred = locations[0];
            Directory.CreateDirectory(preferred);
            var shortcutPath = Path.Combine(preferred, "DadBoard.lnk");
            WriteShortcut(shortcutPath, exePath, shortcutArgs, logger);
            logger.Info($"Desktop shortcut created/updated at {shortcutPath}");
        }
        catch (Exception ex)
        {
            logger.Warn($"Desktop shortcut creation failed: {ex.Message}");
        }
    }

    private static string[] ResolveDesktopDirectories()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var localDesktop = Path.Combine(userProfile, "Desktop");
            return new[] { localDesktop };
        }

        return Array.Empty<string>();
    }

    private static bool HasValidShortcut(IEnumerable<string> locations, string exePath, string expectedArgs, SetupLogger logger)
    {
        foreach (var location in locations)
        {
            var shortcutPath = Path.Combine(location, "DadBoard.lnk");
            if (!File.Exists(shortcutPath))
            {
                continue;
            }

            if (TryReadShortcutDetails(shortcutPath, out var target, out var args))
            {
                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(args ?? "", expectedArgs, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(target))
                {
                    return true;
                }

                logger.Warn($"Desktop shortcut invalid at {shortcutPath}; target={target} args={args}");
            }
            else
            {
                logger.Warn($"Desktop shortcut unreadable at {shortcutPath}");
            }
        }

        return false;
    }

    private static void WriteShortcut(string shortcutPath, string exePath, string args, SetupLogger logger)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            logger.Warn("Desktop shortcut skipped: WScript.Shell unavailable.");
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = exePath;
        shortcut.WorkingDirectory = DadBoardPaths.InstallDir;
        shortcut.Arguments = args;
        shortcut.IconLocation = exePath;
        shortcut.Save();
    }

    private static bool TryReadShortcutDetails(string shortcutPath, out string targetPath, out string args)
    {
        targetPath = "";
        args = "";
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            targetPath = shortcut.TargetPath as string ?? "";
            args = shortcut.Arguments as string ?? "";
            return !string.IsNullOrWhiteSpace(targetPath);
        }
        catch
        {
            return false;
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
