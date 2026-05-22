using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Parsing;

public static class MessageParserFactory
{
    public static IMessageParser Create(ParserProfileOptions options, string encodingName)
    {
        if (!options.Enabled || options.Type == ParserProfileType.None)
        {
            return new NullMessageParser(options);
        }

        return options.Type switch
        {
            ParserProfileType.RegexLines => new RegexLinesMessageParser(options, encodingName),
            ParserProfileType.DelimiterTable => new DelimiterTableMessageParser(options, encodingName),
            ParserProfileType.FixedColumnReport => new FixedColumnReportMessageParser(options, encodingName),
            _ => new UnsupportedMessageParser(options),
        };
    }
}
