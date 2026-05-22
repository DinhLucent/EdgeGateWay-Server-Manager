namespace EdgeGateway.Core.Protocols.Astm;

public static class AstmControlBytes
{
    public const byte Enq = 0x05;
    public const byte Eot = 0x04;
    public const byte Ack = 0x06;
    public const byte Nak = 0x15;
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const byte Etb = 0x17;
    public const byte Cr = 0x0D;
    public const byte Lf = 0x0A;

    public static bool IsControl(byte value)
    {
        return value is Enq or Eot or Ack or Nak;
    }
}
