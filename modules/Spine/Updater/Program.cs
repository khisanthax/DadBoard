using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DadBoard.Spine.Shared;

namespace DadBoard.Updater;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var silent = HasArg(args, "--silent") || HasArg(args, "/silent");
        var action = ParseAction(args);
        var manifestOverride = GetArgValue(args, "--manifest");
        if (silent)
        {
            return RunSilent(action, manifestOverride);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new UpdaterForm(action == UpdaterAction.Repair));
        return 0;
    }

    private static int RunSilent(UpdaterAction action, string? manifestOverride)
    {
        using var logger = new UpdaterLogger();
        var engine = new UpdaterEngine();
        var config = UpdateConfigStore.Load();
        if (!string.IsNullOrWhiteSpace(manifestOverride))
        {
            config.ManifestUrl = manifestOverride.Trim();
            logger.Info($"Manifest override: {config.ManifestUrl}");
        }
        try
        {
            var result = Task.Run(() =>
                    engine.RunAsync(config, action == UpdaterAction.Repair, action == UpdaterAction.Repair ? "repair" : "check", logger.LogPath,
                        CancellationToken.None, msg => logger.Info(msg)))
                .GetAwaiter().GetResult();
            if (result.State == UpdaterState.Failed)
            {
                logger.Error(result.Message);
                return 2;
            }

            logger.Info(result.Message);
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            return 2;
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
}

enum UpdaterAction
{
    Check,
    Repair
}
