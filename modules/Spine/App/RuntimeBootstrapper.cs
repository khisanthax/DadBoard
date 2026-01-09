using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DadBoard.Spine.Shared;

namespace DadBoard.App;

static class RuntimeBootstrapper
{
    public static bool IsRunningFromInstalledLocation()
    {
        var path = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(DadBoardPaths.InstalledExePath),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLegacyBootstrapperPath()
    {
        var path = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "DadBoard",
            "DadBoard.exe");

        return string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(legacyPath),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryLaunchRuntime(string[] args, UpdateLogger logger, out string? error)
    {
        error = null;
        try
        {
            var sourcePath = Process.GetCurrentProcess().MainModule?.FileName;
            EnsureRuntimeExists(logger, sourcePath);
            var runtimeExe = DadBoardPaths.RuntimeExePath;
            if (!File.Exists(runtimeExe))
            {
                error = "Runtime executable not found.";
                logger.Error(error);
                return false;
            }

            var argString = BuildArgString(FilterBootstrapperArgs(args));
            logger.Info($"Launching runtime: {runtimeExe} {argString}");

            var startInfo = new ProcessStartInfo(runtimeExe, argString)
            {
                UseShellExecute = true,
                WorkingDirectory = DadBoardPaths.RuntimeDir
            };
            var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "Failed to launch runtime process.";
                logger.Error(error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.Error($"Bootstrapper launch failed: {ex}");
            return false;
        }
    }

    public static bool ApplyUpdate(string newExePath, string? waitPid, string[] args, UpdateLogger logger, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(newExePath) || !File.Exists(newExePath))
        {
            error = "Update payload missing.";
            logger.Error(error);
            return false;
        }

        WaitForParentExit(waitPid, logger);

        try
        {
            Directory.CreateDirectory(DadBoardPaths.RuntimeDir);
            var runtimeExe = DadBoardPaths.RuntimeExePath;
            var backupExe = DadBoardPaths.UpdateOldExePath;

            if (File.Exists(backupExe))
            {
                File.Delete(backupExe);
            }

            if (File.Exists(runtimeExe))
            {
                File.Move(runtimeExe, backupExe, true);
                logger.Info($"Backed up runtime to {backupExe}");
            }

            File.Move(newExePath, runtimeExe, true);
            logger.Info($"Applied update to {runtimeExe}");

            var argString = BuildArgString(FilterBootstrapperArgs(args));
            logger.Info($"Launching updated runtime: {runtimeExe} {argString}");
            var startInfo = new ProcessStartInfo(runtimeExe, argString)
            {
                UseShellExecute = true,
                WorkingDirectory = DadBoardPaths.RuntimeDir
            };
            var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "Failed to launch updated runtime.";
                logger.Error(error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.Error($"Apply update failed: {ex}");
            TryRestoreBackup(logger);
            return false;
        }
    }

    private static void EnsureRuntimeExists(UpdateLogger logger, string? sourcePath)
    {
        Directory.CreateDirectory(DadBoardPaths.RuntimeDir);
        if (!File.Exists(DadBoardPaths.RuntimeExePath) && File.Exists(DadBoardPaths.InstalledExePath))
        {
            File.Copy(DadBoardPaths.InstalledExePath, DadBoardPaths.RuntimeExePath, true);
            logger.Info($"Copied runtime from {DadBoardPaths.InstalledExePath}.");
        }

        if (!File.Exists(DadBoardPaths.RuntimeExePath) &&
            !string.IsNullOrWhiteSpace(sourcePath) &&
            File.Exists(sourcePath))
        {
            File.Copy(sourcePath, DadBoardPaths.RuntimeExePath, true);
            logger.Info($"Copied runtime from {sourcePath}.");
        }
    }

    private static void WaitForParentExit(string? waitPid, UpdateLogger logger)
    {
        if (!int.TryParse(waitPid, out var pid))
        {
            return;
        }

        try
        {
            var process = Process.GetProcessById(pid);
            logger.Info($"Waiting for PID {pid} to exit.");
            if (!process.WaitForExit(10000))
            {
                logger.Warn($"PID {pid} still running after timeout.");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Wait for PID failed: {ex.Message}");
        }
    }

    private static void TryRestoreBackup(UpdateLogger logger)
    {
        try
        {
            var runtimeExe = DadBoardPaths.RuntimeExePath;
            var backupExe = DadBoardPaths.UpdateOldExePath;
            if (File.Exists(backupExe))
            {
                File.Move(backupExe, runtimeExe, true);
                logger.Info("Restored runtime from backup.");
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to restore backup: {ex.Message}");
        }
    }

    private static IEnumerable<string> FilterBootstrapperArgs(IEnumerable<string> args)
    {
        var skipNext = false;
        foreach (var arg in args)
        {
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (string.Equals(arg, "--apply-update", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--wait-pid", StringComparison.OrdinalIgnoreCase))
            {
                skipNext = true;
                continue;
            }

            if (arg.StartsWith("--apply-update=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--wait-pid=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return arg;
        }
    }

    private static string BuildArgString(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(QuoteArg));
    }

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return "\"\"";
        }

        if (arg.Contains(' ') || arg.Contains('"'))
        {
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        return arg;
    }
}
