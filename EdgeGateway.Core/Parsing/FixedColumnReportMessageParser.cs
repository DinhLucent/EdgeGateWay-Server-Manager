using System.Text;
using System.Text.RegularExpressions;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Parsing;

internal sealed class FixedColumnReportMessageParser(
    ParserProfileOptions options,
    string encodingName
) : IMessageParser
{
    private readonly Encoding encoding = ParserTextEncoding.GetEncoding(encodingName);
    private readonly Regex? tableStart = CreateRegex(options.FixedColumnReport.TableStartPattern);
    private readonly Regex? tableEnd = CreateRegex(options.FixedColumnReport.TableEndPattern);

    public ParserResult Parse(ReadOnlyMemory<byte> payload)
    {
        string[] lines = ParserTextEncoding.SplitLines(encoding.GetString(payload.Span));
        bool inTable = tableStart is null;
        List<ParsedObservation> observations = [];

        foreach (string line in lines)
        {
            if (!inTable)
            {
                if (tableStart?.IsMatch(line.TrimEnd()) == true)
                {
                    inTable = true;
                }

                continue;
            }

            if (tableEnd?.IsMatch(line) == true)
            {
                break;
            }

            string? testCode = GetColumn(line, "testCode");
            string? value = GetColumn(line, "value");
            if (string.IsNullOrWhiteSpace(testCode) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            observations.Add(new ParsedObservation(
                Name: testCode,
                TestCode: testCode,
                ValueText: value,
                Unit: GetColumn(line, "unit"),
                Flag: GetColumn(line, "flag")
            ));
        }

        return new ParserResult(
            options.Type,
            options.Profile,
            observations.Count == 0 ? ParserStatus.NoMatch : ParserStatus.Parsed,
            observations,
            []
        );
    }

    private string? GetColumn(string line, string columnName)
    {
        if (!options.FixedColumnReport.Columns.TryGetValue(columnName, out FixedColumnSlice? slice)
            || slice is null)
        {
            return null;
        }

        if (slice.Start < 0 || slice.Length < 1 || slice.Start >= line.Length)
        {
            return null;
        }

        int length = Math.Min(slice.Length, line.Length - slice.Start);
        string value = line.Substring(slice.Start, length).Trim();
        return value.Length == 0 ? null : value;
    }

    private static Regex? CreateRegex(string? pattern)
    {
        return string.IsNullOrWhiteSpace(pattern)
            ? null
            : new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
