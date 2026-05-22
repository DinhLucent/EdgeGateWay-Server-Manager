using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Protocols.Boundaries;

internal sealed class FixedLengthBoundaryDetector(RawBoundaryOptions options) : IBoundaryDetector
{
    private readonly List<byte> buffer = [];
    private readonly int fixedLength = options.FixedLength ?? 1;

    public BoundaryFeedResult Feed(ReadOnlySpan<byte> bytes)
    {
        buffer.AddRange(bytes.ToArray());
        if (fixedLength > options.MaxMessageBytes)
        {
            Reset();
            return new BoundaryFeedResult([], "fixed-length raw buffer exceeded max message bytes");
        }

        List<ReadOnlyMemory<byte>> messages = [];
        while (buffer.Count >= fixedLength)
        {
            messages.Add(buffer.Take(fixedLength).ToArray());
            buffer.RemoveRange(0, fixedLength);
        }

        if (buffer.Count > options.MaxMessageBytes)
        {
            Reset();
            return new BoundaryFeedResult(
                messages,
                "fixed-length raw buffer exceeded max message bytes"
            );
        }

        return new BoundaryFeedResult(messages);
    }

    public void Reset()
    {
        buffer.Clear();
    }
}
