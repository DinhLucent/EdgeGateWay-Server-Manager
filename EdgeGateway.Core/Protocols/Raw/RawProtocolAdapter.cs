using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Protocols.Boundaries;

namespace EdgeGateway.Core.Protocols.Raw;

public sealed class RawProtocolAdapter(DeviceProfile profile)
{
    private readonly IBoundaryDetector boundaryDetector =
        BoundaryDetectorFactory.Create(profile.RawBoundary);

    public RawCaptureResult Capture(ReadOnlySpan<byte> payload)
    {
        BoundaryFeedResult result = boundaryDetector.Feed(payload);
        return new RawCaptureResult(
            result
                .Messages
                .Select(CreateDraft)
                .ToArray(),
            result.Error
        );
    }

    public RawMessageDraft CreateDraft(ReadOnlyMemory<byte> payload)
    {
        return new RawMessageDraft(
            profile.DeviceId,
            profile.Transport.ToString(),
            profile.Protocol.ToString(),
            payload,
            Status: "received"
        );
    }
}
