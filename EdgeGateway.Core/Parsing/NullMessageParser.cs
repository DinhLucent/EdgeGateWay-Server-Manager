using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Parsing;

internal sealed class NullMessageParser(ParserProfileOptions options) : IMessageParser
{
    public ParserResult Parse(ReadOnlyMemory<byte> payload)
    {
        return ParserResult.Skipped(options);
    }
}
