using System.Text;

namespace EdgeGateway.Core.Protocols.Astm;

public static class AstmFrameCodec
{
    private static readonly Encoding Ascii = Encoding.ASCII;

    public static byte[] BuildFrame(int frameNumber, ReadOnlySpan<byte> payload, bool isLast)
    {
        if (frameNumber is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(frameNumber), "Frame number must be 0-7.");
        }

        byte frameNumberByte = Ascii.GetBytes(frameNumber.ToString())[0];
        byte terminator = isLast ? AstmControlBytes.Etx : AstmControlBytes.Etb;
        byte[] checksumInput = [frameNumberByte, .. payload.ToArray(), terminator];
        string checksum = ComputeChecksum(checksumInput);

        return [
            AstmControlBytes.Stx,
            .. checksumInput,
            .. Ascii.GetBytes(checksum),
            AstmControlBytes.Cr,
            AstmControlBytes.Lf,
        ];
    }

    public static bool TryDecodeFrame(
        ReadOnlySpan<byte> raw,
        out AstmFrame frame,
        out string error
    )
    {
        frame = new AstmFrame(0, [], IsLastFrame: false, ChecksumOk: false, "", "");
        error = string.Empty;

        if (raw.Length < 7)
        {
            error = "frame too short";
            return false;
        }

        if (raw[0] != AstmControlBytes.Stx)
        {
            error = "missing STX";
            return false;
        }

        if (raw[^2] != AstmControlBytes.Cr || raw[^1] != AstmControlBytes.Lf)
        {
            error = "missing trailing CR LF";
            return false;
        }

        ReadOnlySpan<byte> body = raw[1..^2];
        if (body.Length < 4)
        {
            error = "frame body too short";
            return false;
        }

        byte frameNumberByte = body[0];
        if (frameNumberByte is < (byte)'0' or > (byte)'7')
        {
            error = "invalid frame number";
            return false;
        }

        byte terminator = body[^3];
        if (terminator is not AstmControlBytes.Etx and not AstmControlBytes.Etb)
        {
            error = "invalid frame terminator";
            return false;
        }

        string receivedChecksum = Ascii.GetString(body[^2..]);
        byte[] payload = body[1..^3].ToArray();
        byte[] checksumInput = [frameNumberByte, .. payload, terminator];
        string expectedChecksum = ComputeChecksum(checksumInput);

        frame = new AstmFrame(
            frameNumberByte - '0',
            payload,
            terminator == AstmControlBytes.Etx,
            string.Equals(receivedChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase),
            receivedChecksum,
            expectedChecksum
        );
        return true;
    }

    public static string ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        int total = 0;
        foreach (byte value in bytes)
        {
            total = (total + value) & 0xFF;
        }

        return total.ToString("X2");
    }
}
