namespace UvexAdv.Nina.Plugin.UiHarness;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!HarnessOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(HarnessOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(HarnessOptions.Usage);
            return 0;
        }

        try
        {
            var scenarios = ScenarioCatalog.Select(options.Scenario);
            var results = ScreenshotRenderer.RenderAll(scenarios, options.OutputDirectory);
            ScreenshotManifest.Write(options.OutputDirectory, results);

            foreach (var result in results)
            {
                Console.WriteLine($"Rendered {result.ScenarioName}: {result.Width}x{result.Height} -> {result.AbsolutePath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Offline UI render failed: {ex}");
            return 1;
        }
    }
}
