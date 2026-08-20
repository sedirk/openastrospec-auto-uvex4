using System.Text;

namespace UvexAdv.Protocol;

/// <summary>
/// Incremental parser for the UVEX protocol. It tolerates noise before ':' and
/// fragmented or coalesced reads, but never fabricates a frame without '#'.
/// </summary>
public sealed class UvexFrameParser
{
    private readonly StringBuilder buffer = new();
    private readonly int maximumFrameLength;

    public UvexFrameParser(int maximumFrameLength = 4096)
    {
        if (maximumFrameLength < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrameLength));
        }

        this.maximumFrameLength = maximumFrameLength;
    }

    public IReadOnlyList<UvexFrame> Append(ReadOnlySpan<char> chunk)
    {
        if (!chunk.IsEmpty)
        {
            buffer.Append(chunk);
        }

        var frames = new List<UvexFrame>();
        while (true)
        {
            var text = buffer.ToString();
            var start = text.IndexOf(':');
            if (start < 0)
            {
                if (buffer.Length > maximumFrameLength)
                {
                    buffer.Clear();
                }

                break;
            }

            if (start > 0)
            {
                buffer.Remove(0, start);
                text = buffer.ToString();
            }

            var end = text.IndexOf('#');
            if (end < 0)
            {
                if (buffer.Length > maximumFrameLength)
                {
                    buffer.Remove(0, 1);
                }

                break;
            }

            var raw = text[..(end + 1)];
            buffer.Remove(0, end + 1);
            if (TryParse(raw, out var frame))
            {
                frames.Add(frame);
            }
        }

        return frames;
    }

    public static bool TryParse(string raw, out UvexFrame frame)
    {
        frame = null!;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim();
        if (!normalized.StartsWith(':') || !normalized.EndsWith('#'))
        {
            return false;
        }

        var body = normalized[1..^1].Trim();
        var tokens = body.Split(';', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens[0].Length != 4 || !tokens[0].All(char.IsLetterOrDigit))
        {
            return false;
        }

        var arguments = tokens.Skip(1).Where(static token => token.Length > 0).ToArray();
        frame = new UvexFrame(tokens[0].ToUpperInvariant(), arguments, normalized);
        return true;
    }
}
