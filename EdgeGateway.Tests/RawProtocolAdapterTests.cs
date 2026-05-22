using System.Text;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Protocols.Raw;

namespace EdgeGateway.Tests;

public sealed class RawProtocolAdapterTests
{
    [Fact]
    public void Capture_creates_raw_message_draft_without_transforming_bytes()
    {
        DeviceProfile profile = new()
        {
            DeviceId = "raw-tcp-dev",
            Name = "Raw TCP Dev",
            Transport = DeviceTransport.TcpServer,
            Protocol = DeviceProtocol.Raw,
            Mode = DeviceRuntimeMode.EdgeAgentFirst,
            Port = 9100,
            Output = new OutputTargetProfile
            {
                Type = OutputTargetType.Http,
                Url = new Uri("http://localhost:18081/mock-mirth"),
            },
        };
        RawProtocolAdapter adapter = new(profile);
        byte[] payload = Encoding.ASCII.GetBytes("hello raw");

        RawCaptureResult capture = adapter.Capture(payload);
        EdgeGateway.Core.Journal.RawMessageDraft draft = Assert.Single(capture.Messages);

        Assert.Equal(profile.DeviceId, draft.DeviceId);
        Assert.Equal("TcpServer", draft.Transport);
        Assert.Equal("Raw", draft.Protocol);
        Assert.Equal(payload, draft.Payload.ToArray());
    }

    [Fact]
    public void Capture_can_split_delimited_raw_payloads_from_one_read()
    {
        DeviceProfile profile = CreateRawProfile() with
        {
            RawBoundary = new RawBoundaryOptions
            {
                Mode = RawBoundaryMode.DelimitedText,
                Delimiter = "CRLF",
            },
        };
        RawProtocolAdapter adapter = new(profile);

        RawCaptureResult capture = adapter.Capture(Encoding.ASCII.GetBytes("ONE\r\nTWO\r\n"));

        Assert.Collection(
            capture.Messages,
            first => Assert.Equal("ONE", Encoding.ASCII.GetString(first.Payload.ToArray())),
            second => Assert.Equal("TWO", Encoding.ASCII.GetString(second.Payload.ToArray()))
        );
    }

    private static DeviceProfile CreateRawProfile()
    {
        return new DeviceProfile
        {
            DeviceId = "raw-tcp-dev",
            Name = "Raw TCP Dev",
            Transport = DeviceTransport.TcpServer,
            Protocol = DeviceProtocol.Raw,
            Mode = DeviceRuntimeMode.EdgeAgentFirst,
            Port = 9100,
            Output = new OutputTargetProfile
            {
                Type = OutputTargetType.Http,
                Url = new Uri("http://localhost:18081/mock-mirth"),
            },
        };
    }
}
