using System.Text;
using EdgeGateway.Core.Protocols.Astm;

namespace EdgeGateway.Tests;

public sealed class AstmFrameCodecTests
{
    [Fact]
    public void BuildFrame_round_trips_with_valid_checksum()
    {
        byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rL|1|N\r");
        byte[] frameBytes = AstmFrameCodec.BuildFrame(1, payload, isLast: true);

        bool ok = AstmFrameCodec.TryDecodeFrame(
            frameBytes,
            out AstmFrame frame,
            out string error
        );

        Assert.True(ok, error);
        Assert.True(frame.ChecksumOk);
        Assert.True(frame.IsLastFrame);
        Assert.Equal(1, frame.FrameNumber);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public void TryDecodeFrame_detects_bad_checksum()
    {
        byte[] frameBytes = AstmFrameCodec.BuildFrame(
            1,
            Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r"),
            isLast: true
        );
        frameBytes[^4] = frameBytes[^4] == (byte)'0' ? (byte)'1' : (byte)'0';

        bool ok = AstmFrameCodec.TryDecodeFrame(
            frameBytes,
            out AstmFrame frame,
            out string error
        );

        Assert.True(ok, error);
        Assert.False(frame.ChecksumOk);
        Assert.NotEqual(frame.ExpectedChecksum, frame.ReceivedChecksum);
    }

    [Fact]
    public void ComputeChecksum_matches_astm_modulo_sum()
    {
        byte[] input = Encoding.ASCII.GetBytes("1H|\\^&|||CA-270\r");
        string expected = (input.Aggregate(0, (sum, value) => (sum + value) & 0xFF))
            .ToString("X2");

        Assert.Equal(expected, AstmFrameCodec.ComputeChecksum(input));
    }
}
