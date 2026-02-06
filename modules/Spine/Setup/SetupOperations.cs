using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DadBoard.Spine.Shared;

namespace DadBoard.Setup;

public enum SetupAction
{
    Install,
    Repair,
    Uninstall,
    RegisterShortcuts,
    StopApp
}

public sealed class SetupResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Version { get; set; }
}

public static class SetupOperations
{
    private const string MutexName = "Global\\DadBoard.SingleInstance";
    private const string ShutdownEventName = "Global\\DadBoard.Shutdown";
    private const string GateTaskName = "DadBoard Gate Mode";

    public static async Task<SetupResult> RunAsync(
        SetupAction action,
        string? payloadPath,
        TimeSpan? stopWait,
        SetupLogger logger,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.Info($"Setup action={action} payload={payloadPath ?? "(none)"}");

            if (action == SetupAction.RegisterShortcuts)
            {
                CreateDesktopShortcut(logger);
                return new SetupResult { Success = true };
            }

            if (action == SetupAction.StopApp)
            {
                progress?.Report("Stopping DadBoard...");
                logger.Info("Stop-app requested.");
                SignalShutdown(logger);
                WaitForAppExit(stopWait ?? TimeSpan.FromSeconds(10), logger);
                return new SetupResult { Success = true };
            }

            if (action == SetupAction.Uninstall)
            {
                progress?.Report("Stopping DadBoard...");
                logger.Info("Uninstall requested.");
                SignalShutdown(logger);
                WaitForAppExit(TimeSpan.FromSeconds(10), logger);
                RemoveGateScheduledTask(logger);

                if (Directory.Exists(DadBoardPaths.InstallDir))
                {
                    Directory.Delete(DadBoardPaths.InstallDir, true);
                    logger.Info($"Deleted {DadBoardPaths.InstallDir}");
                }

                progress?.Report("Uninstall complete.");
                return new SetupResult { Success = true };
            }

            if (string.IsNullOrWhiteSpace(payloadPath))
            {
                return new SetupResult
                {
                    Success = false,
                    Error = "Payload path is required for install or repair."
                };
            }

            var resolvedPayload = ResolvePayloadPath(payloadPath, logger);
            if (string.IsNullOrWhiteSpace(resolvedPayload) || !File.Exists(resolvedPayload))
            {
                return new SetupResult
                {
                    Success = false,
                    Error = $"Payload file not found: {payloadPath}"
                };
            }

            progress?.Report("Applying package...");
            logger.Info("Applying payload package.");
            ApplyPackage(resolvedPayload, logger);

            progress?.Report("Install complete.");
            return new SetupResult { Success = true };
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            return new SetupResult { Success = false, Error = ex.Message };
        }
    }

    private static string? ResolvePayloadPath(string payloadPath, SetupLogger logger)
    {
        var trimmed = payloadPath.Trim().Trim('"');
        if (File.Exists(trimmed))
        {
            return trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
        {
            return uri.LocalPath;
        }

        logger.Warn($"Payload path not found: {payloadPath}");
        return null;
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

        var skipUpdater = false;
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

        skipUpdater = IsFileLocked(DadBoardPaths.UpdaterExePath);
        if (skipUpdater)
        {
            logger.Warn("DadBoardUpdater.exe is locked; proceeding without replacing updater binary.");
        }
        CopyDirectoryWithRetries(stagingDir, DadBoardPaths.InstallDir, logger, skipUpdater);
        CopySetupIntoInstallDir(logger);
        CopyUpdaterIntoInstallDir(logger);
        CreateDesktopShortcut(logger);
        EnsureGateScheduledTask(logger);
        RestartDadBoard(logger);

        try
        {
            Directory.Delete(stagingDir, true);
        }
        catch
        {
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir, bool skipUpdater, SetupLogger logger)
    {
        foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var targetDir = directory.Replace(sourceDir, destinationDir, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(targetDir);
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var targetPath = file.Replace(sourceDir, destinationDir, StringComparison.OrdinalIgnoreCase);
            if (skipUpdater && string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(DadBoardPaths.UpdaterExePath), StringComparison.OrdinalIgnoreCase))
            {
                logger.Warn($"Skipping updater replacement (locked): {targetPath}");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, true);
        }
    }

    private static void CopyDirectoryWithRetries(string sourceDir, string destinationDir, SetupLogger logger, bool skipUpdater)
    {
        const int attempts = 5;
        for (var i = 1; i <= attempts; i++)
        {
            try
            {
                logger.Info($"Replacing files (attempt {i}/{attempts})...");
                CopyDirectory(sourceDir, destinationDir, skipUpdater, logger);
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

        if (!StopUpdaterProcesses(logger, timeout))
        {
            logger.Warn("DadBoardUpdater still running; continuing without updater replacement.");
        }

        return true;
    }

    private static bool WaitForFileUnlock(string path, TimeSpan timeout, SetupLogger logger)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return true;
        }

        var fileName = Path.GetFileName(path);
        logger.Info($"Waiting for {fileName} to exit...");
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

    private static bool StopUpdaterProcesses(SetupLogger logger, TimeSpan timeout)
    {
        var processes = GetUpdaterProcesses();
        if (processes.Count == 0)
        {
            logger.Info("No running DadBoardUpdater processes found.");
            return true;
        }

        var pids = string.Join(",", processes.Select(p => p.Id));
        logger.Info($"Stopping DadBoardUpdater (PIDs: {pids})");

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
            if (GetUpdaterProcesses().Count == 0)
            {
                logger.Info("DadBoardUpdater stopped.");
                break;
            }

            Thread.Sleep(500);
        }

        if (GetUpdaterProcesses().Count > 0)
        {
            logger.Warn("DadBoardUpdater did not exit; forcing termination.");
            foreach (var process in GetUpdaterProcesses())
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

        if (!WaitForFileUnlock(DadBoardPaths.UpdaterExePath, TimeSpan.FromSeconds(5), logger))
        {
            var remaining = GetUpdaterProcesses();
            var remainingPids = remaining.Count == 0 ? "none" : string.Join(",", remaining.Select(p => p.Id));
            logger.Warn($"DadBoardUpdater.exe did not release file lock after shutdown/kill. remaining_pids={remainingPids}");
            logger.Warn("DadBoardUpdater.exe did not release file lock after shutdown/kill.");
            return false;
        }

        logger.Info("Updater file lock released.");
        return true;
    }

    private static bool IsFileLocked(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch
        {
            return true;
        }
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

    private static List<Process> GetUpdaterProcesses()
    {
        var list = new List<Process>();
        var currentPid = Process.GetCurrentProcess().Id;
        foreach (var process in Process.GetProcessesByName("DadBoardUpdater"))
        {
            try
            {
                if (process.Id == currentPid)
                {
                    continue;
                }

                var path = process.MainModule?.FileName ?? "";
                if (string.IsNullOrWhiteSpace(path) ||
                    string.Equals(Path.GetFullPath(path), Path.GetFullPath(DadBoardPaths.UpdaterExePath), StringComparison.OrdinalIgnoreCase))
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
                Arguments = "--mode agent --minimized --no-first-run",
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

    private static void EnsureGateScheduledTask(SetupLogger logger)
    {
        try
        {
            var exePath = DadBoardPaths.InstalledExePath;
            if (!File.Exists(exePath))
            {
                logger.Warn($"Gate task skipped: DadBoard.exe missing at {exePath}");
                return;
            }

            var installDir = DadBoardPaths.InstallDir;
            var quotedExe = $"\"{exePath}\"";
            var args = "--mode gate";
            var taskCommand = $"cmd.exe /c \"cd /d \\\"{installDir}\\\" && {quotedExe} {args}\"";
            var taskArgs = $"/Create /F /SC ONLOGON /TN \"{GateTaskName}\" /TR \"{taskCommand}\" /RU \"{Environment.UserName}\" /IT /RL LIMITED";
            var (ok, stdout, stderr) = RunSchtasks(taskArgs);
            if (ok)
            {
                var restartPolicyApplied = EnsureGateTaskRestartPolicy(logger);
                logger.Info($"Gate task ensured: {GateTaskName}");
                logger.Info($"Gate task action: {exePath} {args}");
                logger.Info($"Gate task restart policy: {(restartPolicyApplied ? "RestartCount=3 RestartInterval=PT1M" : "not_applied")}");
            }
            else
            {
                logger.Warn($"Gate task create/update failed: {stderr}");
                logger.Warn($"Gate task args: {taskArgs}");
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    logger.Warn($"Gate task output: {stdout}");
                }

                if (Environment.UserInteractive)
                {
                    MessageBox.Show(
                        $"Failed to create/update the gate auto-run task.{Environment.NewLine}{GateTaskName}{Environment.NewLine}{stderr}",
                        "DadBoard Setup",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Gate task creation failed: {ex.Message}");
            if (Environment.UserInteractive)
            {
                MessageBox.Show(
                    $"Failed to create/update the gate auto-run task.{Environment.NewLine}{GateTaskName}{Environment.NewLine}{ex.Message}",
                    "DadBoard Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }

    private static bool EnsureGateTaskRestartPolicy(SetupLogger logger)
    {
        var escapedTaskName = GateTaskName.Replace("'", "''", StringComparison.Ordinal);
        var script = string.Join("; ", new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"$task = Get-ScheduledTask -TaskName '{escapedTaskName}'",
            "$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)",
            $"Set-ScheduledTask -TaskName '{escapedTaskName}' -Settings $settings | Out-Null"
        });

        var (ok, stdout, stderr) = RunPowerShell(script);
        if (ok)
        {
            return true;
        }

        logger.Warn($"Gate task restart policy update failed: {stderr}");
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            logger.Warn($"Gate task restart policy output: {stdout}");
        }

        return false;
    }

    private static void RemoveGateScheduledTask(SetupLogger logger)
    {
        try
        {
            var taskArgs = $"/Delete /F /TN \"{GateTaskName}\"";
            var (ok, stdout, stderr) = RunSchtasks(taskArgs);
            if (ok)
            {
                logger.Info($"Gate task removed: {GateTaskName}");
            }
            else
            {
                logger.Warn($"Gate task removal failed: {stderr}");
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    logger.Warn($"Gate task removal output: {stdout}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Gate task removal failed: {ex.Message}");
        }
    }

    private static (bool ok, string stdout, string stderr) RunSchtasks(string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return (false, "", "Failed to start schtasks.exe");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode == 0, stdout.Trim(), stderr.Trim());
    }

    private static (bool ok, string stdout, string stderr) RunPowerShell(string script)
    {
        var encodedScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return (false, "", "Failed to start powershell.exe");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode == 0, stdout.Trim(), stderr.Trim());
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

    private static void CopyUpdaterIntoInstallDir(SetupLogger logger)
    {
        try
        {
            var updaterSource = Path.Combine(AppContext.BaseDirectory, "DadBoardUpdater.exe");
            var updaterDest = DadBoardPaths.UpdaterExePath;
            if (!File.Exists(updaterSource))
            {
                logger.Warn($"Updater not found at {updaterSource}");
                return;
            }

            if (string.Equals(Path.GetFullPath(updaterSource), Path.GetFullPath(updaterDest), StringComparison.OrdinalIgnoreCase))
            {
                logger.Info("Updater already in install dir.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(updaterDest)!);
            File.Copy(updaterSource, updaterDest, true);
            logger.Info($"Copied updater into install dir: {updaterDest}");
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to copy updater into install dir: {ex.Message}");
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
