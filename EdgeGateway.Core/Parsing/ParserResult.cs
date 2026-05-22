using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Parsing;

public sealed record ParserResult(
    ParserProfileType Type,
    string? Profile,
    ParserStatus Status,
    IReadOnlyList<ParsedObservation> Observations,
    IReadOnlyList<string> Warnings
)
{
    public static ParserResult Skipped(ParserProfileOptions options)
    {
        return new ParserResult(
            options.Type,
            options.Profile,
            ParserStatus.Skipped,
            [],
            []
        );
    }
}
