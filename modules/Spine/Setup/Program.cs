using System;
using System.Linq;
using System.Windows.Forms;

namespace DadBoard.Setup;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var options = SetupOptions.Parse(args);
        var logger = new SetupLogger();

        if (options.Mode != SetupMode.None && options.Silent)
        {
            try
            {
                var result = SetupOperations.Run(options, logger).GetAwaiter().GetResult();
                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                logger.Error($"Silent setup failed: {ex}");
                return 2;
            }
        }

        ApplicationConfiguration.Initialize();
        using var form = new SetupForm(options);
        Application.Run(form);
        return 0;
    }
}

enum SetupMode
{
    None = 0,
    Install = 1,
    Update = 2,
    Uninstall = 3
}

sealed class SetupOptions
{
    public SetupMode Mode { get; init; }
    public bool Silent { get; init; }
    public string? ManifestUrl { get; init; }

    public static SetupOptions Parse(string[] args)
    {
        var mode = SetupMode.None;
        if (args.Any(a => string.Equals(a, "/install", StringComparison.OrdinalIgnoreCase)))
        {
            mode = SetupMode.Install;
        }
        else if (args.Any(a => string.Equals(a, "/update", StringComparison.OrdinalIgnoreCase)))
        {
            mode = SetupMode.Update;
        }
        else if (args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            mode = SetupMode.Uninstall;
        }

        var manifest = GetArgValue(args, "--manifest");
        return new SetupOptions
        {
            Mode = mode,
            Silent = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)),
            ManifestUrl = manifest
        };
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
