namespace UvexAdv.Nina.Plugin.Tests;

// Source-order guards supplement (not replace) executable workflow tests.
// Read the implementation that owns the invariant, not an obsolete runner body.
internal static class WorkflowSource
{
    internal static string Read(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Sources", name + ".cs")).Replace("\r\n", "\n");
    internal static string Probe => Read("AtrProbeWorkflow");
    internal static string Science => Read("AtrScienceWorkflow");
    internal static string Acquisition => Read("G3AcquisitionWorkflow");
    internal static string Return => Read("G3AcquisitionReturnWorkflow");
    internal static string Convergence => Read("Phd2SlitConvergenceWorkflow");
    internal static string Ports => Read("RealObservationStageRunner.WorkflowPorts");
}
