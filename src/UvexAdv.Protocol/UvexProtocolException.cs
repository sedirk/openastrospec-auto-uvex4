namespace UvexAdv.Protocol;

public sealed class UvexProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
