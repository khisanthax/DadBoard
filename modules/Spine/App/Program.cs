using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DadBoard.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            if (args.Any(arg => string.Equals(arg, "--install-elevated", StringComparison.OrdinalIgnoreCase)))
            {
                var logPath = GetArgValue(args, "--install-log");
                var statusPath = GetArgValue(args, "--install-status");
                var parentPid = GetArgValue(args, "--installer-parent");
                var postInstallSession = GetArgValue(args, "--postinstall-session");
                int? parsedParentPid = null;
                if (int.TryParse(parentPid, out var pid))
                {
                    parsedParentPid = pid;
                }
                Installer.PerformInstall(
                    addFirewall: args.Any(a => string.Equals(a, "--add-firewall", StringComparison.OrdinalIgnoreCase)),
                    logPath: logPath,
                    statusPath: statusPath,
                    installerParentPid: parsedParentPid,
                    postInstallId: postInstallSession);
                return;
            }

            ApplicationConfiguration.Initialize();

            if (args.Any(arg => string.Equals(arg, "--install", StringComparison.OrdinalIgnoreCase)))
            {
                using var installForm = new InstallProgressForm(addFirewall: args.Any(a =>
                    string.Equals(a, "--add-firewall", StringComparison.OrdinalIgnoreCase)));
                installForm.ShowDialog();
                return;
            }

            var launchOptions = AppLaunchOptions.Parse(args);
            var postInstallId = launchOptions.PostInstallId;

            if (!Installer.IsInstalled() && !launchOptions.SkipFirstRunPrompt)
            {
                var choice = FirstRunForm.ShowChoice();
                if (choice == FirstRunChoice.Install)
                {
                    using var installForm = new InstallProgressForm(addFirewall: false);
                    installForm.ShowDialog();
                    return;
                }
            }

            var singleInstance = SingleInstanceManager.TryAcquire();
            if (singleInstance == null)
            {
                if (!string.IsNullOrWhiteSpace(postInstallId))
                {
                    SingleInstanceManager.SignalShutdown();
                    singleInstance = SingleInstanceManager.TryAcquireWithRetry(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250));
                    if (singleInstance == null)
                    {
                        return;
                    }
                }
                else
                {
                    SingleInstanceManager.SignalActivate();
                    return;
                }
            }

            using (singleInstance)
            {
                using var context = new TrayAppContext(launchOptions);
                singleInstance.BeginListen(context.HandleActivateSignal, context.HandleShutdownSignal);
                Application.Run(context);
            }
        }
        catch (Exception ex)
        {
            var fallbackDir = Path.Combine(Path.GetTempPath(), "DadBoard");
            Directory.CreateDirectory(fallbackDir);
            var logPath = Path.Combine(fallbackDir, "dadboard_boot.log");
            File.AppendAllText(logPath, $"{DateTime.UtcNow:O} {ex}{Environment.NewLine}");
            MessageBox.Show(
                $"DadBoard failed to start. Details written to:{Environment.NewLine}{logPath}",
                "DadBoard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

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
}
