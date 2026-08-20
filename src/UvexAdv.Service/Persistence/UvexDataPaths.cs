namespace UvexAdv.Service.Persistence;

public sealed class UvexDataPaths
{
    public UvexDataPaths()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("UVEX_ADV_DATA_DIR");
        Root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UVEX-ADV")
            : Path.GetFullPath(overrideRoot);
        Logs = Path.Combine(Root, "logs");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }

    public string Root { get; }
    public string Logs { get; }
    public string Database => Path.Combine(Root, "uvex-adv.db");
    public string Configuration => Path.Combine(Root, "config.json");
}
