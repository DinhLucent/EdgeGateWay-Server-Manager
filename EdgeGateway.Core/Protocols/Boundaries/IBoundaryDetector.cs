namespace EdgeGateway.Core.Protocols.Boundaries;

public interface IBoundaryDetector
{
    BoundaryFeedResult Feed(ReadOnlySpan<byte> bytes);

    void Reset();
}
