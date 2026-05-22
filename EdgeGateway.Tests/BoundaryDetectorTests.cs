using System.Text;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Protocols.Boundaries;

namespace EdgeGateway.Tests;

public sealed class BoundaryDetectorTests
{
    [Fact]
    public void DelimitedText_splits_multiple_crlf_messages_in_one_chunk()
    {
        IBoundaryDetector detector = BoundaryDetectorFactory.Create(
            new RawBoundaryOptions
            {
                Mode = RawBoundaryMode.DelimitedText,
                Delimiter = "CRLF",
            }
        );

        BoundaryFeedResult result = detector.Feed(Encoding.ASCII.GetBytes("A\r\nB\r\n"));

        AssertMessages(result, "A", "B");
    }

    [Fact]
    public void DelimitedText_holds_fragment_until_delimiter_arrives()
    {
        IBoundaryDetector detector = BoundaryDetectorFactory.Create(
            new RawBoundaryOptions
            {
                Mode = RawBoundaryMode.DelimitedText,
                Delimiter = "CRLF",
            }
        );

        BoundaryFeedResult first = detector.Feed(Encoding.ASCII.GetBytes("A\r"));
        BoundaryFeedResult second = detector.Feed(Encoding.ASCII.GetBytes("\nB\r\n"));

        Assert.Empty(first.Messages);
        AssertMessages(second, "A", "B");
    }

    [Fact]
    public void DelimitedText_allows_multiple_small_messages_when_chunk_exceeds_max_message_bytes()
    {
        IBoundaryDetector detector = BoundaryDetectorFactory.Create(
            new RawBoundaryOptions
            {
                Mode = RawBoundaryMode.DelimitedText,
                Delimiter = "CRLF",
                MaxMessageBytes = 3,
            }
        );

        BoundaryFeedResult result = detector.Feed(Encoding.ASCII.GetBytes("A\r\nB\r\n"));

        AssertMessages(result, "A", "B");
    }

    [Fact]
    public void FixedLength_emits_each_complete_record()
    {
        IBoundaryDetector detector = BoundaryDetectorFactory.Create(
            new RawBoundaryOptions
            {
                Mode = RawBoundaryMode.FixedLength,
                FixedLength = 3,
            }
        );

        BoundaryFeedResult result = detector.Feed(Encoding.ASCII.GetBytes("ABCDEF"));

        AssertMessages(result, "ABC", "DEF");
    }

    [Fact]
    public void FixedLength_allows_multiple_complete_records_when_chunk_exceeds_max_message_bytes()
    {
        IBoundaryDetector detector = BoundaryDetectorFactory.Create(
            new RawBoundaryOptions
            {
                Mode = RawBoundaryMode.FixedLength,
                FixedLength = 2,
                MaxMessageBytes = 2,
            }
        );

        BoundaryFeedResult result = detector.Feed(Encoding.ASCII.GetBytes("AABB"));

        AssertMessages(result, "AA", "BB");
    }

    [Fact]
    public void CustomBytes_discards_garbage_and_extracts_stx_etx_payloads()
    {
        IBoundaryDetector detector = BoundaryDetectorFactory.Create(
            new RawBoundaryOptions
            {
                Mode = RawBoundaryMode.CustomBytes,
                StartBytes = "<STX>",
                EndBytes = "<ETX>",
            }
        );

        BoundaryFeedResult result = detector.Feed([0x55, 0x02, (byte)'A', 0x03, 0x02, (byte)'B', 0x03]);

        AssertMessages(result, "A", "B");
    }

    [Fact]
    public void CustomBytes_supports_common_control_token_sequences()
    {
        IBoundaryDetector detector = BoundaryDetectorFactory.Create(
            new RawBoundaryOptions
            {
                Mode = RawBoundaryMode.CustomBytes,
                StartBytes = "<VT>",
                EndBytes = "<FS><CR>",
            }
        );

        BoundaryFeedResult result = detector.Feed([0x0B, (byte)'A', 0x1C, 0x0D]);

        AssertMessages(result, "A");
    }

    [Fact]
    public void BoundaryDetector_resets_buffer_when_max_message_bytes_is_exceeded()
    {
        IBoundaryDetector detector = BoundaryDetectorFactory.Create(
            new RawBoundaryOptions
            {
                Mode = RawBoundaryMode.DelimitedText,
                Delimiter = "CRLF",
                MaxMessageBytes = 5,
            }
        );

        BoundaryFeedResult overflow = detector.Feed(Encoding.ASCII.GetBytes("ABCDEF"));
        BoundaryFeedResult recovered = detector.Feed(Encoding.ASCII.GetBytes("OK\r\n"));

        Assert.NotNull(overflow.Error);
        Assert.Empty(overflow.Messages);
        AssertMessages(recovered, "OK");
    }

    private static void AssertMessages(BoundaryFeedResult result, params string[] expected)
    {
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Messages.Select(message => Encoding.ASCII.GetString(message.ToArray())));
    }
}
