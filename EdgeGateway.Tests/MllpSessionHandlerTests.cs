using System.Text;
using EdgeGateway.Core.Protocols.Hl7Mllp;

namespace EdgeGateway.Tests;

public sealed class MllpSessionHandlerTests
{
    [Fact]
    public void ProcessBytes_extracts_complete_mllp_frame()
    {
        MllpSessionHandler handler = new();
        byte[] message = Encoding.ASCII.GetBytes("MSH|^~\\&|EDGE|LAB\rOBR|1\r");

        IReadOnlyList<MllpFrame> frames = handler.ProcessBytes(
            MllpSessionHandler.BuildFrame(message)
        );

        MllpFrame frame = Assert.Single(frames);
        Assert.Equal(message, frame.Payload);
    }

    [Fact]
    public void ProcessBytes_waits_for_complete_mllp_frame()
    {
        MllpSessionHandler handler = new();
        byte[] message = Encoding.ASCII.GetBytes("MSH|^~\\&|EDGE|LAB\r");
        byte[] frame = MllpSessionHandler.BuildFrame(message);

        Assert.Empty(handler.ProcessBytes(frame.AsSpan(0, 5)));
        IReadOnlyList<MllpFrame> frames = handler.ProcessBytes(frame.AsSpan(5));

        Assert.Equal(message, Assert.Single(frames).Payload);
    }
}
