using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Protocols.Boundaries;

internal sealed class DelimitedTextBoundaryDetector(RawBoundaryOptions options) : IBoundaryDetector
{
    private readonly List<byte> buffer = [];
    private readonly byte[] delimiter = BoundaryByteParser.ParseToken(
        options.CustomDelimiter,
        options.Delimiter
    );

    public BoundaryFeedResult Feed(ReadOnlySpan<byte> bytes)
    {
        buffer.AddRange(bytes.ToArray());

        List<ReadOnlyMemory<byte>> messages = [];
        while (true)
        {
            int delimiterIndex = IndexOf(buffer, delimiter);
            if (delimiterIndex < 0)
            {
                break;
            }

            int messageLength = options.IncludeBoundary
                ? delimiterIndex + delimiter.Length
                : delimiterIndex;
            if (messageLength > options.MaxMessageBytes)
            {
                Reset();
                return new BoundaryFeedResult(
                    messages,
                    "delimited raw message exceeded max message bytes"
                );
            }

            messages.Add(buffer.Take(messageLength).ToArray());
            buffer.RemoveRange(0, delimiterIndex + delimiter.Length);
        }

        if (buffer.Count > options.MaxMessageBytes)
        {
            Reset();
            return new BoundaryFeedResult(
                messages,
                "delimited raw message exceeded max message bytes"
            );
        }

        return new BoundaryFeedResult(messages);
    }

    public void Reset()
    {
        buffer.Clear();
    }

    private static int IndexOf(List<byte> source, byte[] pattern)
    {
        if (pattern.Length == 0 || source.Count < pattern.Length)
        {
            return -1;
        }

        for (int index = 0; index <= source.Count - pattern.Length; index++)
        {
            bool matched = true;
            for (int patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
            {
                if (source[index + patternIndex] != pattern[patternIndex])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }
}
