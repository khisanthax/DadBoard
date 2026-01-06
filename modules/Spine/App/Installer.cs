using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        var installExe = GetInstalledExePath();
        return File.Exists(installExe);
    }

    public static bool RequestElevation(bool addFirewall)
    {
        if (IsElevated())
        {
            PerformInstall(addFirewall);
            return true;
        }

        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }

            var args = "--install-elevated";
            if (addFirewall)
            {
                args += " --add-firewall";
            }

            var startInfo = new ProcessStartInfo(exePath, args)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void PerformInstall(bool addFirewall)
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

        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(Path.Combine(baseDir, "Agent"));
        Directory.CreateDirectory(Path.Combine(baseDir, "Leader"));
        Directory.CreateDirectory(Path.Combine(baseDir, "logs"));
        Directory.CreateDirectory(Path.Combine(baseDir, "diag"));

        var config = LoadExistingConfig(baseDir) ?? new AgentConfig
        {
            PcId = Guid.NewGuid().ToString("N"),
            DisplayName = Environment.MachineName
        };

        SaveAgentConfig(Path.Combine(baseDir, "Agent", "agent.config.json"), config);
        EnsureLeaderConfig(Path.Combine(baseDir, "Leader", "leader.config.json"));

        RegisterTask(installExe);

        if (addFirewall)
        {
            AddFirewallRules(config.UdpPort, config.WsPort);
        }

        Process.Start(new ProcessStartInfo(installExe) { UseShellExecute = true });
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string GetInstalledExePath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DadBoard", "DadBoard.exe");
    }

    private static AgentConfig? LoadExistingConfig(string baseDir)
    {
        var programDataConfig = Path.Combine(baseDir, "Agent", "agent.config.json");
        var localConfig = Path.Combine(AppContext.BaseDirectory, "Agent", "agent.config.json");

        foreach (var path in new[] { programDataConfig, localConfig })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<AgentConfig>(json, JsonUtil.Options);
                if (config != null)
                {
                    return config;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static void SaveAgentConfig(string path, AgentConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonUtil.Options);
        File.WriteAllText(path, json);
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
}
