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
        if (silent)
        {
            return RunSilent();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new UpdaterForm());
        return 0;
    }

    private static int RunSilent()
    {
        using var logger = new UpdaterLogger();
        var engine = new UpdaterEngine();
        var config = UpdateConfigStore.Load();
        try
        {
            var result = Task.Run(() => engine.RunAsync(config, CancellationToken.None, msg => logger.Info(msg)))
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
}
