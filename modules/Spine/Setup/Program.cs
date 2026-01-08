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
        var manifestUrl = GetArgValue(args, "--manifest");

        if (silent && action.HasValue)
        {
            SetupLogger logger;
            try
            {
                logger = new SetupLogger();
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
                var result = Task.Run(() =>
                    SetupOperations.RunAsync(action.Value, manifestUrl, logger, null, default)).GetAwaiter().GetResult();

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
        if (HasArg(args, "--install") || HasArg(args, "/install"))
        {
            return SetupAction.Install;
        }

        if (HasArg(args, "--update") || HasArg(args, "/update"))
        {
            return SetupAction.Update;
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
