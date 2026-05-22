using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Parsing;

internal sealed class UnsupportedMessageParser(ParserProfileOptions options) : IMessageParser
{
    public ParserResult Parse(ReadOnlyMemory<byte> payload)
    {
        return new ParserResult(
            options.Type,
            options.Profile,
            ParserStatus.Failed,
            [],
            [$"Parser type '{options.Type}' is not implemented in this slice."]
        );
    }
}
