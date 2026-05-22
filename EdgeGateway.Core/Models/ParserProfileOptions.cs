namespace EdgeGateway.Core.Models;

public sealed record ParserProfileOptions
{
    public ParserProfileType Type { get; init; } = ParserProfileType.None;

    public string? Profile { get; init; }

    public bool Enabled { get; init; } = true;

    public ParserOutputMode OutputMode { get; init; } = ParserOutputMode.MetadataOnly;

    public IReadOnlyList<RegexLineRuleOptions> RegexLineRules { get; init; } = [];

    public DelimiterTableParserOptions DelimiterTable { get; init; } = new();

    public FixedColumnReportParserOptions FixedColumnReport { get; init; } = new();
}

public sealed record RegexLineRuleOptions
{
    public string Name { get; init; } = "";

    public string Pattern { get; init; } = "";

    public string ValueGroup { get; init; } = "value";

    public string? TestCode { get; init; }

    public string? Unit { get; init; }

    public string? FlagGroup { get; init; }
}

public sealed record DelimiterTableParserOptions
{
    public string Delimiter { get; init; } = "|";

    public bool HasHeader { get; init; } = true;

    public IReadOnlyDictionary<string, string> Columns { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record FixedColumnReportParserOptions
{
    public string? TableStartPattern { get; init; }

    public string? TableEndPattern { get; init; }

    public IReadOnlyDictionary<string, FixedColumnSlice> Columns { get; init; } =
        new Dictionary<string, FixedColumnSlice>(StringComparer.OrdinalIgnoreCase);
}

public sealed record FixedColumnSlice(int Start, int Length);
