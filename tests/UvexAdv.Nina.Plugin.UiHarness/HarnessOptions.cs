using System.IO;

namespace UvexAdv.Nina.Plugin.UiHarness;

public sealed record HarnessOptions(
    bool Render,
    bool ShowHelp,
    string OutputDirectory,
    string? Scenario)
{
    public const string Usage =
        "Usage: UvexAdv.Nina.Plugin.UiHarness --render [--output <directory>] " +
        "[--scenario idle|startup-requirements|running|failure|phd2-degraded|phd2-direct-target|ghost-assistance|qhy-g3-fast-pair|narrow|advanced]\n" +
        "The --render switch is mandatory. The harness is offline and never instantiates the production dockable view model.";

    public static bool TryParse(
        IReadOnlyList<string> args,
        out HarnessOptions options,
        out string error)
    {
        var render = false;
        var showHelp = false;
        string? output = null;
        string? scenario = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--render":
                    render = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--output":
                    if (!TryTakeValue(args, ref index, out output))
                    {
                        options = Empty;
                        error = "--output requires a directory.";
                        return false;
                    }

                    break;
                case "--scenario":
                    if (!TryTakeValue(args, ref index, out scenario))
                    {
                        options = Empty;
                        error = "--scenario requires a name.";
                        return false;
                    }

                    break;
                default:
                    options = Empty;
                    error = $"Unknown argument: {argument}";
                    return false;
            }
        }

        if (showHelp)
        {
            options = new(false, true, ResolveOutputDirectory(output), scenario);
            error = string.Empty;
            return true;
        }

        if (!render)
        {
            options = Empty;
            error = "Refusing to create a WPF surface without the explicit --render switch.";
            return false;
        }

        if (scenario is not null && !ScenarioCatalog.Names.Contains(scenario, StringComparer.OrdinalIgnoreCase))
        {
            options = Empty;
            error = $"Unknown scenario '{scenario}'.";
            return false;
        }

        options = new(true, false, ResolveOutputDirectory(output), scenario);
        error = string.Empty;
        return true;
    }

    private static HarnessOptions Empty => new(false, false, string.Empty, null);

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int index, out string? value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static string ResolveOutputDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var root = FindRepositoryRoot(Directory.GetCurrentDirectory())
            ?? FindRepositoryRoot(AppContext.BaseDirectory)
            ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, "tmp", "ui-screenshots");
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "UVEX-ADV.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
