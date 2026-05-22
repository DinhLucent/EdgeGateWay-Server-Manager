namespace EdgeGateway.Core.Protocols.Hl7Mllp;

public sealed class MllpSessionHandler
{
    public const byte StartBlock = 0x0B;
    public const byte EndBlock = 0x1C;
    public const byte CarriageReturn = 0x0D;

    private readonly List<byte> receiveBuffer = [];

    public IReadOnlyList<MllpFrame> ProcessBytes(ReadOnlySpan<byte> bytes)
    {
        receiveBuffer.AddRange(bytes.ToArray());
        List<MllpFrame> frames = [];

        while (receiveBuffer.Count > 0)
        {
            int start = receiveBuffer.IndexOf(StartBlock);
            if (start < 0)
            {
                receiveBuffer.Clear();
                break;
            }

            if (start > 0)
            {
                receiveBuffer.RemoveRange(0, start);
            }

            int end = FindFrameEnd();
            if (end < 0)
            {
                break;
            }

            int payloadLength = end - 1;
            byte[] payload = receiveBuffer.Skip(1).Take(payloadLength).ToArray();
            receiveBuffer.RemoveRange(0, end + 2);
            frames.Add(new MllpFrame(payload));
        }

        return frames;
    }

    public static byte[] BuildFrame(ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[payload.Length + 3];
        frame[0] = StartBlock;
        payload.CopyTo(frame.AsSpan(1));
        frame[^2] = EndBlock;
        frame[^1] = CarriageReturn;
        return frame;
    }

    private int FindFrameEnd()
    {
        for (int index = 1; index < receiveBuffer.Count - 1; index++)
        {
            if (receiveBuffer[index] == EndBlock
                && receiveBuffer[index + 1] == CarriageReturn)
            {
                return index;
            }
        }

        return -1;
    }
}
