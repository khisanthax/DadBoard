using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DadBoard.Spine.Shared;

namespace DadBoard.Setup;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var action = GetAction(args);
        var silent = HasArg(args, "--silent") || HasArg(args, "/silent");
        var payloadPath = GetArgValue(args, "--payload");
        var logPath = GetArgValue(args, "--log");
        var waitMsValue = GetArgValue(args, "--wait-ms");
        var stopWait = TryParseWait(waitMsValue);

        if (action.HasValue)
        {
            SetupLogger logger;
            try
            {
                logger = new SetupLogger(logPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Setup logging failed: {ex.Message}",
                    "DadBoard Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 3;
            }

            using (logger)
            {
                if ((action.Value == SetupAction.Install || action.Value == SetupAction.Repair) &&
                    string.IsNullOrWhiteSpace(payloadPath))
                {
                    logger.Error("Payload path is required for install or repair.");
                    return 4;
                }

                if (!silent)
                {
                    logger.Info("Running Setup in headless mode (no UI).");
                }

                var result = Task.Run(() =>
                    SetupOperations.RunAsync(action.Value, payloadPath, stopWait, logger, null, default)).GetAwaiter().GetResult();

                if (action.Value == SetupAction.Install || action.Value == SetupAction.Repair)
                {
                    var errorCode = MapSetupErrorCode(result.Error ?? "");
                    var versionAfter = VersionUtil.Normalize(VersionUtil.GetVersionFromFile(DadBoardPaths.InstalledExePath));
                    var setupStatus = new SetupResultStatus
                    {
                        TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                        Success = result.Success,
                        ExitCode = result.Success ? 0 : 2,
                        ErrorCode = errorCode,
                        ErrorMessage = result.Error ?? "",
                        VersionAfter = versionAfter
                    };
                    if (!SetupResultStore.Save(setupStatus))
                    {
                        logger.Warn("Failed to write setup_result.json.");
                    }

                    logger.Info($"Setup exit_reason={(result.Success ? "success" : "failed")} error_code={errorCode} error={result.Error ?? ""}");
                }

                if (result.Success && action.Value != SetupAction.Uninstall)
                {
                    LaunchInstalledApp();
                }

                return result.Success ? 0 : 2;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
        return 0;
    }

    private static SetupAction? GetAction(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.OrdinalIgnoreCase))
        {
            return ParseVerb(args[0]);
        }

        if (HasArg(args, "--install") || HasArg(args, "/install"))
        {
            return SetupAction.Install;
        }

        if (HasArg(args, "--update") || HasArg(args, "/update"))
        {
            return SetupAction.Repair;
        }

        if (HasArg(args, "--uninstall") || HasArg(args, "/uninstall"))
        {
            return SetupAction.Uninstall;
        }

        return null;
    }

    private static bool HasArg(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetArgValue(string[] args, string name)
    {
        var direct = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct.Substring(name.Length + 1).Trim('"');
        }

        var idx = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx < args.Length - 1)
        {
            return args[idx + 1].Trim('"');
        }

        return null;
    }

    private static SetupAction? ParseVerb(string verb)
    {
        if (string.Equals(verb, "install", StringComparison.OrdinalIgnoreCase))
        {
            return SetupAction.Install;
        }

        if (string.Equals(verb, "repair", StringComparison.OrdinalIgnoreCase))
        {
            return SetupAction.Repair;
        }

        if (string.Equals(verb, "uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return SetupAction.Uninstall;
        }

        if (string.Equals(verb, "register-shortcuts", StringComparison.OrdinalIgnoreCase))
        {
            return SetupAction.RegisterShortcuts;
        }

        if (string.Equals(verb, "stop-app", StringComparison.OrdinalIgnoreCase))
        {
            return SetupAction.StopApp;
        }

        return null;
    }

    private static string MapSetupErrorCode(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "";
        }

        var lowered = message.ToLowerInvariant();
        if (lowered.Contains("payload"))
        {
            return "payload_missing";
        }
        if (lowered.Contains("file lock") || lowered.Contains("locked"))
        {
            return "file_lock";
        }
        if (lowered.Contains("permission") || lowered.Contains("access"))
        {
            return "access_denied";
        }

        return "setup_failed";
    }

    private static TimeSpan? TryParseWait(string? waitMsValue)
    {
        if (string.IsNullOrWhiteSpace(waitMsValue))
        {
            return null;
        }

        return int.TryParse(waitMsValue, out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : null;
    }

    private static void LaunchInstalledApp()
    {
        if (!File.Exists(DadBoardPaths.InstalledExePath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo(
            DadBoardPaths.InstalledExePath,
            "--mode agent --minimized")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(DadBoardPaths.InstalledExePath)
        };
        Process.Start(startInfo);
    }
}
