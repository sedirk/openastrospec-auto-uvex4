namespace UvexAdv.Qhy.Service;

public sealed record QhyServiceOptions
{
    public bool Simulator { get; init; } = true;
    public string SimulatorMode { get; init; } = "Synthetic";
    public string ExpectedStableId { get; init; } = "SIM-QHYMINICAM8M-001";
    public string ExpectedModel { get; init; } = "QHYminiCam8M";
    public string DataRoot { get; init; } = string.Empty;
    public int Port { get; init; } = 47_845;
    public bool AutoConnect { get; init; }
    public string ReplayDirectory { get; init; } = string.Empty;
    public int SyntheticWidth { get; init; } = 1_024;
    public int SyntheticHeight { get; init; } = 576;
    public int SyntheticStars { get; init; } = 80;
    public int SimulationDelayMilliseconds { get; init; } = 80;
    public string NativeSdkPath { get; init; } = string.Empty;
    public string NativeSdkSha256 { get; init; } = string.Empty;
    public int NativeReadoutMode { get; init; } = 1;
    public Dictionary<string, int> NativeFilterPositions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
