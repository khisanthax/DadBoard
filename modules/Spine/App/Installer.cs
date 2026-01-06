using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using DadBoard.Spine.Shared;

namespace DadBoard.App;

static class Installer
{
    private const string TaskName = "DadBoardAgent";

    public static bool IsInstalled()
    {
        return File.Exists(GetInstalledExePath());
    }

    public static string GetInstalledExePath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DadBoard", "DadBoard.exe");
    }

    public static InstallSession CreateInstallSession()
    {
        var timestamp = DateTime.Now;
        var id = Guid.NewGuid().ToString("N");
        var logPath = GetInstallLogPath(timestamp);
        var statusPath = GetInstallStatusPath(timestamp);
        var snapshot = InstallStatusFactory.CreateDefault();
        snapshot.GetOrAddStep(InstallSteps.Elevate).Status = InstallStepStatus.Running;
        InstallStatusIo.Write(statusPath, snapshot);
        return new InstallSession(id, logPath, statusPath, timestamp);
    }

    public static Process? StartElevatedInstall(InstallSession session, bool addFirewall)
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                return null;
            }

            var args = $"--install-elevated --install-log \"{session.LogPath}\" --install-status \"{session.StatusPath}\" --installer-parent {Process.GetCurrentProcess().Id} --postinstall-session {session.Id}";
            if (addFirewall)
            {
                args += " --add-firewall";
            }

            var startInfo = new ProcessStartInfo(exePath, args)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            return Process.Start(startInfo);
        }
        catch
        {
            return null;
        }
    }

    public static bool PerformInstall(bool addFirewall, string? logPath, string? statusPath, int? installerParentPid, string? postInstallId)
    {
        var logger = new InstallLogger(logPath ?? GetInstallLogPath(DateTime.Now));
        var tracker = new InstallStatusTracker(statusPath ?? GetInstallStatusPath(DateTime.Now));

        tracker.UpdateStep(InstallSteps.Elevate, InstallStepStatus.Success, "Elevated.");
        logger.Info("Installer elevated.");
        logger.Info($"Installer parent pid: {(installerParentPid.HasValue ? installerParentPid.Value.ToString() : "unknown")}");

        var agentConfig = new AgentConfig();
        if (!RunStep(tracker, logger, InstallSteps.CopyExe, "Copying DadBoard.exe", () =>
        {
            StopOtherInstances(logger, installerParentPid);
            CopySelf();
        }))
        {
            return false;
        }

        if (!RunStep(tracker, logger, InstallSteps.CreateData, "Creating ProgramData folders/configs", () =>
            EnsureDataDirsAndConfigs(logger)))
        {
            return false;
        }

        if (!RunStep(tracker, logger, InstallSteps.CreateTask, "Creating scheduled task DadBoardAgent", () =>
            RegisterTask(GetInstalledExePath())))
        {
            return false;
        }

        if (addFirewall)
        {
            if (!RunStep(tracker, logger, InstallSteps.Firewall, "Adding firewall rules", () =>
                AddFirewallRules(agentConfig.UdpPort, agentConfig.WsPort)))
            {
                return false;
            }
        }
        else
        {
            tracker.UpdateStep(InstallSteps.Firewall, InstallStepStatus.Success, "Skipped.");
            logger.Info("Firewall rules skipped.");
        }

        tracker.UpdateStep(InstallSteps.Launch, InstallStepStatus.Pending, "Waiting to launch.");
        logger.Info("Elevated install steps complete.");
        if (!string.IsNullOrWhiteSpace(postInstallId))
        {
            LaunchInstalledTrayApp(logger, postInstallId);
        }
        if (installerParentPid.HasValue && !IsProcessRunning(installerParentPid.Value))
        {
            LaunchInstalledTrayFallback(logger);
        }
        return true;
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool RunStep(InstallStatusTracker tracker, InstallLogger logger, string stepName, string logMessage, Action action)
    {
        tracker.UpdateStep(stepName, InstallStepStatus.Running, "Running...");
        logger.Info(logMessage);
        try
        {
            action();
            tracker.UpdateStep(stepName, InstallStepStatus.Success, "Success.");
            logger.Info($"{stepName} completed.");
            return true;
        }
        catch (Exception ex)
        {
            tracker.UpdateStep(stepName, InstallStepStatus.Failed, ex.Message);
            tracker.Complete(false, $"{stepName} failed: {ex}");
            logger.Error($"{stepName} failed: {ex}");
            return false;
        }
    }

    private static void CopySelf()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            throw new InvalidOperationException("Unable to determine executable path.");
        }

        var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DadBoard");
        Directory.CreateDirectory(installDir);
        var installExe = Path.Combine(installDir, "DadBoard.exe");
        File.Copy(exePath, installExe, true);
    }

    private static void StopOtherInstances(InstallLogger logger, int? installerParentPid)
    {
        try
        {
            var currentId = Process.GetCurrentProcess().Id;
            foreach (var proc in Process.GetProcessesByName("DadBoard"))
            {
                if (proc.Id == currentId)
                {
                    continue;
                }

                if (installerParentPid.HasValue && proc.Id == installerParentPid.Value)
                {
                    continue;
                }

                logger.Info($"Stopping running DadBoard.exe (PID {proc.Id}).");
                try
                {
                    if (proc.CloseMainWindow())
                    {
                        if (!proc.WaitForExit(3000))
                        {
                            proc.Kill();
                            proc.WaitForExit(3000);
                        }
                    }
                    else
                    {
                        proc.Kill();
                        proc.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error($"Failed to stop DadBoard.exe PID {proc.Id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to enumerate running DadBoard.exe: {ex.Message}");
        }
    }

    private static void EnsureDataDirsAndConfigs(InstallLogger logger)
    {
        var baseDir = GetProgramDataBaseDir();
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(Path.Combine(baseDir, "Agent"));
        Directory.CreateDirectory(Path.Combine(baseDir, "Leader"));
        Directory.CreateDirectory(Path.Combine(baseDir, "logs"));
        Directory.CreateDirectory(Path.Combine(baseDir, "diag"));
        ApplyProgramDataAcl(baseDir, logger);

        EnsureLeaderConfig(Path.Combine(baseDir, "Leader", "leader.config.json"));
        ApplyProgramDataAcl(baseDir, logger);
    }

    private static string GetProgramDataBaseDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard");
    }

    private static string GetInstallLogPath(DateTime timestamp)
    {
        var logsDir = Path.Combine(GetProgramDataBaseDir(), "logs");
        return Path.Combine(logsDir, $"install_{timestamp:yyyyMMdd_HHmmss}.log");
    }

    private static string GetInstallStatusPath(DateTime timestamp)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DadBoard");
        return Path.Combine(tempDir, $"install_status_{timestamp:yyyyMMdd_HHmmss}.json");
    }

    private static void EnsureLeaderConfig(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        var config = new LeaderConfig
        {
            Games = new[]
            {
                new GameDefinition
                {
                    Id = "drg",
                    Name = "Deep Rock Galactic",
                    LaunchUrl = "steam://run/548430",
                    ProcessNames = new[] { "FSD.exe" },
                    ReadyTimeoutSec = 120
                }
            }
        };

        var json = JsonSerializer.Serialize(config, JsonUtil.Options);
        File.WriteAllText(path, json);
    }

    private static void RegisterTask(string exePath)
    {
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType == null)
            {
                throw new InvalidOperationException("Task Scheduler COM not available.");
            }

            dynamic service = Activator.CreateInstance(serviceType)!;
            service.Connect();

            dynamic root = service.GetFolder("\\");
            dynamic task = service.NewTask(0);

            task.RegistrationInfo.Description = "DadBoard auto-start";
            task.Settings.Enabled = true;
            task.Settings.StartWhenAvailable = true;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;

            const int TaskLogonInteractiveToken = 3;
            const int TaskRunLevelLua = 0;
            const int TaskTriggerLogon = 9;
            const int TaskActionExec = 0;
            const int TaskCreateOrUpdate = 6;

            task.Principal.LogonType = TaskLogonInteractiveToken;
            task.Principal.RunLevel = TaskRunLevelLua;
            task.Principal.UserId = WindowsIdentity.GetCurrent().Name;

            dynamic trigger = task.Triggers.Create(TaskTriggerLogon);
            trigger.UserId = WindowsIdentity.GetCurrent().Name;

            dynamic action = task.Actions.Create(TaskActionExec);
            action.Path = exePath;
            action.Arguments = "--mode agent";

            root.RegisterTaskDefinition(TaskName, task, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);

            ReleaseComObject(trigger);
            ReleaseComObject(action);
            ReleaseComObject(task);
            ReleaseComObject(root);
            ReleaseComObject(service);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to register scheduled task: {ex.Message}");
        }
    }

    private static void AddFirewallRules(int udpPort, int wsPort)
    {
        RunNetsh($"advfirewall firewall add rule name=\"DadBoard UDP {udpPort}\" dir=in action=allow protocol=UDP localport={udpPort}");
        RunNetsh($"advfirewall firewall add rule name=\"DadBoard WS {wsPort}\" dir=in action=allow protocol=TCP localport={wsPort}");
    }

    private static void ApplyProgramDataAcl(string baseDir, InstallLogger logger)
    {
        try
        {
            logger.Info("Applying ProgramData ACLs via icacls.");
            var args = $"\"{baseDir}\" /inheritance:e /grant *S-1-5-32-545:(OI)(CI)M /grant *S-1-5-32-544:(OI)(CI)F /T";
            var start = new ProcessStartInfo("icacls", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(start);
            if (proc == null)
            {
                throw new InvalidOperationException("Failed to start icacls.");
            }

            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!string.IsNullOrWhiteSpace(output))
            {
                logger.Info($"icacls output: {output.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                logger.Error($"icacls error: {error.Trim()}");
            }

            if (proc.ExitCode != 0)
            {
                logger.Error($"icacls failed with exit code {proc.ExitCode}: {error}");
                throw new InvalidOperationException($"icacls failed with exit code {proc.ExitCode}: {error}");
            }

            logger.Info("ProgramData ACLs applied successfully.");
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to apply ProgramData ACLs: {ex}");
            throw new InvalidOperationException($"Failed to apply ProgramData ACLs: {ex.Message}");
        }
    }

    private static void RunNetsh(string args)
    {
        var start = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(start);
        proc?.WaitForExit(5000);
    }

    private static void ReleaseComObject(object? comObj)
    {
        if (comObj == null)
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(comObj);
        }
        catch
        {
        }
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void LaunchInstalledTrayFallback(InstallLogger logger)
    {
        try
        {
            var exePath = GetInstalledExePath();
            if (!File.Exists(exePath))
            {
                logger.Error("Fallback launch skipped: installed DadBoard.exe not found.");
                return;
            }

            var args = "--mode agent --minimized";
            logger.Info($"Fallback launching installed tray app: \"{exePath}\" {args}");
            Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.Error($"Fallback launch failed: {ex.Message}");
        }
    }

    private static void LaunchInstalledTrayApp(InstallLogger logger, string postInstallId)
    {
        try
        {
            var exePath = GetInstalledExePath();
            if (!File.Exists(exePath))
            {
                logger.Error("Launch skipped: installed DadBoard.exe not found.");
                return;
            }

            var args = $"--mode agent --minimized --postinstall {postInstallId}";
            logger.Info($"Launching installed tray app: \"{exePath}\" {args}");
            Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.Error($"Launch failed: {ex.Message}");
        }
    }
}
