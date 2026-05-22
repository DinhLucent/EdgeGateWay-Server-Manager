namespace EdgeGateway.Core.Protocols.Astm;

public static class AstmOutboundFrameBuilder
{
    public static IReadOnlyList<byte[]> BuildFrames(
        ReadOnlySpan<byte> payload,
        int maxPayloadBytes
    )
    {
        int chunkSize = Math.Max(1, maxPayloadBytes);
        if (payload.Length == 0)
        {
            return [AstmFrameCodec.BuildFrame(1, ReadOnlySpan<byte>.Empty, isLast: true)];
        }

        List<byte[]> frames = [];
        int offset = 0;
        int frameNumber = 1;
        while (offset < payload.Length)
        {
            int count = Math.Min(chunkSize, payload.Length - offset);
            bool isLast = offset + count >= payload.Length;
            frames.Add(AstmFrameCodec.BuildFrame(
                frameNumber,
                payload.Slice(offset, count),
                isLast
            ));
            offset += count;
            frameNumber = frameNumber == 7 ? 0 : frameNumber + 1;
        }

        return frames;
    }
}
