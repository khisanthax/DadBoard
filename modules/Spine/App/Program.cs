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
            if (args.Any(arg => string.Equals(arg, "--install", StringComparison.OrdinalIgnoreCase)))
            {
                Installer.RequestElevation(addFirewall: args.Any(a => string.Equals(a, "--add-firewall", StringComparison.OrdinalIgnoreCase)));
                return;
            }

            if (args.Any(arg => string.Equals(arg, "--install-elevated", StringComparison.OrdinalIgnoreCase)))
            {
                Installer.PerformInstall(addFirewall: args.Any(a => string.Equals(a, "--add-firewall", StringComparison.OrdinalIgnoreCase)));
                return;
            }

            var launchOptions = AppLaunchOptions.Parse(args);

            if (!Installer.IsInstalled() && !launchOptions.SkipFirstRunPrompt)
            {
                var choice = FirstRunForm.ShowChoice();
                if (choice == FirstRunChoice.Install)
                {
                    Installer.RequestElevation(addFirewall: false);
                    return;
                }
            }

            ApplicationConfiguration.Initialize();
            using var context = new TrayAppContext(launchOptions);
            Application.Run(context);
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
}
