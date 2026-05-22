using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Protocols.Boundaries;

internal sealed class RawChunkBoundaryDetector(RawBoundaryOptions options) : IBoundaryDetector
{
    public BoundaryFeedResult Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return new BoundaryFeedResult([]);
        }

        if (bytes.Length > options.MaxMessageBytes)
        {
            return new BoundaryFeedResult([], "raw chunk exceeded max message bytes");
        }

        return new BoundaryFeedResult([bytes.ToArray()]);
    }

    public void Reset()
    {
    }
}
