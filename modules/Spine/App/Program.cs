using System;
using System.IO;
using System.Windows.Forms;

namespace DadBoard.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            using var context = new TrayAppContext(args);
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
