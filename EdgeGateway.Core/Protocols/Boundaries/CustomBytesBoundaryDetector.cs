using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Protocols.Boundaries;

internal sealed class CustomBytesBoundaryDetector(RawBoundaryOptions options) : IBoundaryDetector
{
    private readonly List<byte> buffer = [];
    private readonly byte[] start = BoundaryByteParser.ParseToken(options.StartBytes, "<STX>");
    private readonly byte[] end = BoundaryByteParser.ParseToken(options.EndBytes, "<ETX>");

    public BoundaryFeedResult Feed(ReadOnlySpan<byte> bytes)
    {
        buffer.AddRange(bytes.ToArray());

        List<ReadOnlyMemory<byte>> messages = [];
        while (true)
        {
            int startIndex = IndexOf(buffer, start);
            if (startIndex < 0)
            {
                buffer.Clear();
                break;
            }

            if (startIndex > 0)
            {
                buffer.RemoveRange(0, startIndex);
            }

            int endIndex = IndexOf(buffer, end, start.Length);
            if (endIndex < 0)
            {
                break;
            }

            int removeLength = endIndex + end.Length;
            byte[] framed = buffer.Take(removeLength).ToArray();
            byte[] message = options.IncludeBoundary
                ? framed
                : framed[start.Length..endIndex];
            if (message.Length > options.MaxMessageBytes)
            {
                Reset();
                return new BoundaryFeedResult(
                    messages,
                    "custom-bytes raw message exceeded max message bytes"
                );
            }

            messages.Add(message);
            buffer.RemoveRange(0, removeLength);
        }

        if (buffer.Count > options.MaxMessageBytes)
        {
            Reset();
            return new BoundaryFeedResult(
                messages,
                "custom-bytes raw message exceeded max message bytes"
            );
        }

        return new BoundaryFeedResult(messages);
    }

    public void Reset()
    {
        buffer.Clear();
    }

    private static int IndexOf(List<byte> source, byte[] pattern, int startIndex = 0)
    {
        if (pattern.Length == 0 || source.Count < pattern.Length)
        {
            return -1;
        }

        for (int index = startIndex; index <= source.Count - pattern.Length; index++)
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
