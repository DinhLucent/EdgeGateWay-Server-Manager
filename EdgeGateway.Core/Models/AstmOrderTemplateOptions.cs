namespace EdgeGateway.Core.Models;

public sealed record AstmOrderTemplateOptions
{
    public string TestCodePrefix { get; init; } = "^^^";

    public string TestSeparator { get; init; } = "\\";

    public string EmptyTestList { get; init; } = "^^^00";

    public string RecordSeparator { get; init; } = "\r";

    public IReadOnlyList<string> Records { get; init; } =
    [
        "H|\\^&|||{HOST}|||||||||{TIMESTAMP}",
        "P|1|{SAMPLE_ID}",
        "O|1|{SAMPLE_ID}||{TEST_LIST}",
        "L|1|N",
    ];
}
