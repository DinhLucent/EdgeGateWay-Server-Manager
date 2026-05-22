using System.Text;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Parsing;

internal sealed class DelimiterTableMessageParser(
    ParserProfileOptions options,
    string encodingName
) : IMessageParser
{
    private readonly Encoding encoding = ParserTextEncoding.GetEncoding(encodingName);

    public ParserResult Parse(ReadOnlyMemory<byte> payload)
    {
        string[] rows = ParserTextEncoding
            .SplitLines(encoding.GetString(payload.Span))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (rows.Length == 0)
        {
            return NoMatch();
        }

        string delimiter = NormalizeDelimiter(options.DelimiterTable.Delimiter);
        Dictionary<string, int> headerIndexes = [];
        int dataStartIndex = 0;
        if (options.DelimiterTable.HasHeader)
        {
            string[] headers = SplitRow(rows[0], delimiter);
            for (int index = 0; index < headers.Length; index++)
            {
                headerIndexes[headers[index].Trim()] = index;
            }

            dataStartIndex = 1;
        }

        List<ParsedObservation> observations = [];
        for (int rowIndex = dataStartIndex; rowIndex < rows.Length; rowIndex++)
        {
            string[] cells = SplitRow(rows[rowIndex], delimiter);
            string? testCode = GetCell("testCode", cells, headerIndexes);
            string? value = GetCell("value", cells, headerIndexes);
            if (string.IsNullOrWhiteSpace(testCode) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            observations.Add(new ParsedObservation(
                Name: testCode,
                TestCode: testCode,
                ValueText: value,
                Unit: GetCell("unit", cells, headerIndexes),
                Flag: GetCell("flag", cells, headerIndexes)
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

    private ParserResult NoMatch()
    {
        return new ParserResult(options.Type, options.Profile, ParserStatus.NoMatch, [], []);
    }

    private string? GetCell(
        string logicalName,
        string[] cells,
        IReadOnlyDictionary<string, int> headerIndexes
    )
    {
        if (!options.DelimiterTable.Columns.TryGetValue(logicalName, out string? columnRef))
        {
            return null;
        }

        int index;
        if (int.TryParse(columnRef, out int numericIndex))
        {
            index = numericIndex;
        }
        else if (!headerIndexes.TryGetValue(columnRef, out index))
        {
            return null;
        }

        if (index < 0 || index >= cells.Length)
        {
            return null;
        }

        string value = cells[index].Trim();
        return value.Length == 0 ? null : value;
    }

    private static string[] SplitRow(string row, string delimiter)
    {
        return row.Split(delimiter, StringSplitOptions.None);
    }

    private static string NormalizeDelimiter(string delimiter)
    {
        return delimiter switch
        {
            "\\t" or "TAB" => "\t",
            _ => delimiter,
        };
    }
}
