using System;
using System.Linq;

namespace DadBoard.App;

enum AppMode
{
    Default = 0,
    Agent = 1,
    Leader = 2
}

sealed class AppLaunchOptions
{
    public AppMode Mode { get; init; } = AppMode.Default;
    public bool SkipFirstRunPrompt { get; init; }

    public static AppLaunchOptions Parse(string[] args)
    {
        var modeArg = args.FirstOrDefault(a => a.StartsWith("--mode", StringComparison.OrdinalIgnoreCase));
        var mode = AppMode.Default;
        if (!string.IsNullOrWhiteSpace(modeArg))
        {
            var parts = modeArg.Split('=', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                mode = ParseMode(parts[1]);
            }
        }

        if (args.Any(a => string.Equals(a, "--mode", StringComparison.OrdinalIgnoreCase)))
        {
            var idx = Array.FindIndex(args, a => string.Equals(a, "--mode", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx < args.Length - 1)
            {
                mode = ParseMode(args[idx + 1]);
            }
        }

        return new AppLaunchOptions
        {
            Mode = mode,
            SkipFirstRunPrompt = args.Any(a => string.Equals(a, "--no-first-run", StringComparison.OrdinalIgnoreCase))
        };
    }

    private static AppMode ParseMode(string value)
    {
        if (string.Equals(value, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return AppMode.Agent;
        }

        if (string.Equals(value, "leader", StringComparison.OrdinalIgnoreCase))
        {
            return AppMode.Leader;
        }

        return AppMode.Default;
    }
}
