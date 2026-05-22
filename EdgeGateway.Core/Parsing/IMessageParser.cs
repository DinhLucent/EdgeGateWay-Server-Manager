namespace EdgeGateway.Core.Parsing;

public interface IMessageParser
{
    ParserResult Parse(ReadOnlyMemory<byte> payload);
}
