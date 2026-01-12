using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Windows.Forms;
using DadBoard.Spine.Shared;

namespace DadBoard.Updater;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var silent = HasArg(args, "--silent") || HasArg(args, "/silent");
        var autoRun = HasArg(args, "--auto") || HasArg(args, "/auto");
        if (args.Length > 0 && string.Equals(args[0], "schedule", StringComparison.OrdinalIgnoreCase))
        {
            return HandleSchedule(args.Skip(1).ToArray());
        }

        var action = ParseAction(args);
        var manifestOverride = GetArgValue(args, "--manifest");
        var channelOverride = GetArgValue(args, "--channel");
        var invocationOverride = GetArgValue(args, "--invocation");
        var triggerReason = GetArgValue(args, "--reason");
        if (silent)
        {
            return RunSilent(action, manifestOverride, channelOverride, invocationOverride, triggerReason);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new UpdaterForm(action == UpdaterAction.Repair, autoRun));
        return 0;
    }

    private static int RunSilent(UpdaterAction action, string? manifestOverride, string? channelOverride, string? invocationOverride, string? reason)
    {
        using var logger = new UpdaterLogger();
        var engine = new UpdaterEngine();
        var config = UpdateConfigStore.Load();
        if (!string.IsNullOrWhiteSpace(manifestOverride))
        {
            config.ManifestUrl = manifestOverride.Trim();
            logger.Info($"Manifest override: {config.ManifestUrl}");
        }
        if (!string.IsNullOrWhiteSpace(channelOverride) && Enum.TryParse<UpdateChannel>(channelOverride, true, out var channel))
        {
            config.UpdateChannel = channel;
            logger.Info($"Channel override: {config.UpdateChannel}");
        }
        try
        {
            var invocation = invocationOverride;
            if (string.IsNullOrWhiteSpace(invocation))
            {
                invocation = action == UpdaterAction.Trigger ? "triggered" : "silent";
            }
            if (!string.IsNullOrWhiteSpace(reason))
            {
                logger.Info($"Trigger reason: {reason}");
            }
            var result = Task.Run(() =>
                    engine.RunAsync(
                        config,
                        action == UpdaterAction.Repair,
                        action == UpdaterAction.Repair ? "repair" : "check",
                        invocation,
                        logger.LogPath,
                        CancellationToken.None,
                        msg => logger.Info(msg)))
                .GetAwaiter().GetResult();
            if (result.State == UpdaterState.Failed)
            {
                logger.Error(result.Message);
                return (int)result.ExitCode;
            }

            logger.Info(result.Message);
            return (int)result.ExitCode;
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            return (int)UpdaterExitCode.UnknownFailure;
        }
    }

    private static bool HasArg(string[] args, string name)
        => Array.Exists(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static UpdaterAction ParseAction(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(args[0], "repair", StringComparison.OrdinalIgnoreCase))
            {
                return UpdaterAction.Repair;
            }
            if (string.Equals(args[0], "check", StringComparison.OrdinalIgnoreCase))
            {
                return UpdaterAction.Check;
            }
            if (string.Equals(args[0], "trigger", StringComparison.OrdinalIgnoreCase))
            {
                return UpdaterAction.Trigger;
            }
        }

        return UpdaterAction.Check;
    }

    private static string? GetArgValue(string[] args, string name)
    {
        var direct = Array.Find(args, arg => arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct.Substring(name.Length + 1).Trim('"');
        }

        var idx = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx < args.Length - 1)
        {
            return args[idx + 1].Trim('"');
        }

        return null;
    }

    private static int HandleSchedule(string[] args)
    {
        using var logger = new UpdaterLogger();
        if (args.Length == 0)
        {
            logger.Error("Schedule command missing.");
            return (int)UpdaterExitCode.InvalidArgs;
        }

        var sub = args[0].ToLowerInvariant();
        if (sub == "install")
        {
            var channelArg = GetArgValue(args, "--channel");
            var timeArg = GetArgValue(args, "--time");
            var jitterArg = GetArgValue(args, "--jitter-min");
            var runLevel = GetArgValue(args, "--runlevel");
            return ScheduleInstall(channelArg, timeArg, jitterArg, runLevel, logger);
        }

        if (sub == "remove")
        {
            return ScheduleRemove(logger);
        }

        if (sub == "status")
        {
            return ScheduleStatus(logger);
        }

        logger.Error("Unknown schedule subcommand.");
        return (int)UpdaterExitCode.InvalidArgs;
    }

    private static int ScheduleInstall(string? channelArg, string? timeArg, string? jitterArg, string? runLevel, UpdaterLogger logger)
    {
        var channel = UpdateConfigStore.DefaultChannel;
        if (!string.IsNullOrWhiteSpace(channelArg) && Enum.TryParse<UpdateChannel>(channelArg, true, out var parsed))
        {
            channel = parsed;
        }

        var time = string.IsNullOrWhiteSpace(timeArg) ? "03:00" : timeArg;
        var jitter = 0;
        if (!string.IsNullOrWhiteSpace(jitterArg))
        {
            int.TryParse(jitterArg, out jitter);
        }
        jitter = Math.Clamp(jitter, 0, 60);

        var scheduled = ApplyJitter(time, jitter);
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? DadBoardPaths.UpdaterExePath;
        var args = $"check --silent --channel {channel.ToString().ToLowerInvariant()} --invocation scheduled";

        var taskArgs = new[]
        {
            "/Create",
            "/TN", "\"DadBoard Updater\"",
            "/SC", "DAILY",
            "/ST", scheduled,
            "/TR", $"\"\\\"{exePath}\\\" {args}\"",
            "/F"
        }.ToList();

        if (string.Equals(runLevel, "highest", StringComparison.OrdinalIgnoreCase))
        {
            taskArgs.AddRange(new[] { "/RL", "HIGHEST" });
        }
        else
        {
            taskArgs.AddRange(new[] { "/RL", "LIMITED", "/IT" });
        }

        var exit = RunSchtasks(taskArgs, logger);
        if (exit == 0)
        {
            logger.Info($"Scheduled daily update at {scheduled} (jitter {jitter} min) channel={channel}");
        }
        return exit == 0 ? 0 : (int)UpdaterExitCode.SetupInvokeFailure;
    }

    private static int ScheduleRemove(UpdaterLogger logger)
    {
        var exit = RunSchtasks(new[] { "/Delete", "/TN", "\"DadBoard Updater\"", "/F" }, logger);
        if (exit == 0)
        {
            logger.Info("Scheduled task removed.");
        }
        return exit == 0 ? 0 : (int)UpdaterExitCode.SetupInvokeFailure;
    }

    private static int ScheduleStatus(UpdaterLogger logger)
    {
        var exit = RunSchtasks(new[] { "/Query", "/TN", "\"DadBoard Updater\"", "/FO", "LIST" }, logger);
        return exit == 0 ? 0 : (int)UpdaterExitCode.SetupInvokeFailure;
    }

    private static int RunSchtasks(IEnumerable<string> args, UpdaterLogger logger)
    {
        try
        {
            var start = new ProcessStartInfo("schtasks.exe", string.Join(" ", args))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(start);
            if (proc == null)
            {
                logger.Error("Failed to start schtasks.exe.");
                return 1;
            }
            var output = proc.StandardOutput.ReadToEnd();
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (!string.IsNullOrWhiteSpace(output))
            {
                logger.Info(output.Trim());
            }
            if (!string.IsNullOrWhiteSpace(err))
            {
                logger.Warn(err.Trim());
            }
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            logger.Error($"schtasks failed: {ex.Message}");
            return 1;
        }
    }

    private static string ApplyJitter(string time, int jitterMinutes)
    {
        if (!TimeSpan.TryParse(time, out var ts))
        {
            ts = new TimeSpan(3, 0, 0);
        }
        if (jitterMinutes <= 0)
        {
            return ts.ToString(@"hh\:mm");
        }
        var rand = new Random();
        var delta = rand.Next(0, jitterMinutes + 1);
        var total = ts.Add(TimeSpan.FromMinutes(delta));
        if (total.TotalHours >= 24)
        {
            total = total.Subtract(TimeSpan.FromHours(24));
        }
        return total.ToString(@"hh\:mm");
    }
}

enum UpdaterAction
{
    Check,
    Repair,
    Trigger
}
