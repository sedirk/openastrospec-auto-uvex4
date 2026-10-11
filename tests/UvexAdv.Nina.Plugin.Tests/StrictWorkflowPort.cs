using System.Reflection;

namespace UvexAdv.Nina.Plugin.Tests;

// Fail on every unexpected operation. These proxies contain no device clients,
// so executing a production workflow cannot silently touch real equipment.
public class StrictWorkflowPort : DispatchProxy
{
    internal readonly List<string> Calls = [];
    internal readonly Dictionary<string, Func<object?[], object?>> Handlers = new();
    internal static T Create<T>(out StrictWorkflowPort trace) where T : class
    {
        var port = Create<T, StrictWorkflowPort>();
        trace = (StrictWorkflowPort)(object)port;
        return port;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var name = method!.Name;
        Calls.Add(name);
        return Handlers.TryGetValue(name, out var handle) ? handle(args ?? [])
            : throw new InvalidOperationException($"Unexpected workflow operation: {name}");
    }
}
