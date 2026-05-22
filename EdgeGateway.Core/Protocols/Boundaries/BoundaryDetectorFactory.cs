using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Protocols.Boundaries;

public static class BoundaryDetectorFactory
{
    public static IBoundaryDetector Create(RawBoundaryOptions options)
    {
        return options.Mode switch
        {
            RawBoundaryMode.RawChunk => new RawChunkBoundaryDetector(options),
            RawBoundaryMode.DelimitedText => new DelimitedTextBoundaryDetector(options),
            RawBoundaryMode.FixedLength => new FixedLengthBoundaryDetector(options),
            RawBoundaryMode.CustomBytes => new CustomBytesBoundaryDetector(options),
            _ => new RawChunkBoundaryDetector(options),
        };
    }
}
