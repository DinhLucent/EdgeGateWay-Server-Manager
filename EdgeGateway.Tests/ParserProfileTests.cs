using System.Text;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Parsing;

namespace EdgeGateway.Tests;

public sealed class ParserProfileTests
{
    [Fact]
    public void Regex_lines_parser_extracts_labeled_result_lines()
    {
        ParserProfileOptions options = new()
        {
            Type = ParserProfileType.RegexLines,
            Profile = "generic/raw-key-value",
            RegexLineRules =
            [
                new RegexLineRuleOptions
                {
                    Name = "wbc",
                    Pattern = "^WBC:(?<value>[0-9.]+)$",
                    TestCode = "WBC",
                    Unit = "10^9/L",
                },
                new RegexLineRuleOptions
                {
                    Name = "hgb",
                    Pattern = "^HGB:(?<value>[0-9.]+)$",
                    TestCode = "HGB",
                    Unit = "g/dL",
                },
            ],
        };
        IMessageParser parser = MessageParserFactory.Create(options, "ascii");

        ParserResult result = parser.Parse(Encoding.ASCII.GetBytes("ID:12345\r\nWBC:6.2\r\nHGB:13.8\r\n"));

        Assert.Equal(ParserStatus.Parsed, result.Status);
        Assert.Equal("generic/raw-key-value", result.Profile);
        Assert.Collection(
            result.Observations,
            first =>
            {
                Assert.Equal("WBC", first.TestCode);
                Assert.Equal("6.2", first.ValueText);
                Assert.Equal("10^9/L", first.Unit);
            },
            second =>
            {
                Assert.Equal("HGB", second.TestCode);
                Assert.Equal("13.8", second.ValueText);
                Assert.Equal("g/dL", second.Unit);
            }
        );
    }

    [Fact]
    public void Delimiter_table_parser_extracts_rows_by_header_names()
    {
        ParserProfileOptions options = new()
        {
            Type = ParserProfileType.DelimiterTable,
            Profile = "generic/pipe-table",
            DelimiterTable = new DelimiterTableParserOptions
            {
                Delimiter = "|",
                HasHeader = true,
                Columns = new Dictionary<string, string>
                {
                    ["testCode"] = "TEST",
                    ["value"] = "RESULT",
                    ["unit"] = "UNIT",
                    ["flag"] = "FLAG",
                },
            },
        };
        IMessageParser parser = MessageParserFactory.Create(options, "ascii");

        ParserResult result = parser.Parse(Encoding.ASCII.GetBytes("TEST|RESULT|UNIT|FLAG\r\nWBC|6.2|10^9/L|\r\n"));

        ParsedObservation observation = Assert.Single(result.Observations);
        Assert.Equal(ParserStatus.Parsed, result.Status);
        Assert.Equal("WBC", observation.TestCode);
        Assert.Equal("6.2", observation.ValueText);
        Assert.Equal("10^9/L", observation.Unit);
        Assert.Null(observation.Flag);
    }

    [Fact]
    public void Fixed_column_report_parser_extracts_table_rows_between_markers()
    {
        ParserProfileOptions options = new()
        {
            Type = ParserProfileType.FixedColumnReport,
            Profile = "vendor/machine-x-print-report-v1",
            FixedColumnReport = new FixedColumnReportParserOptions
            {
                TableStartPattern = "^TEST\\s+RESULT\\s+UNIT\\s+FLAG$",
                TableEndPattern = "^\\s*$",
                Columns = new Dictionary<string, FixedColumnSlice>
                {
                    ["testCode"] = new(0, 14),
                    ["value"] = new(14, 13),
                    ["unit"] = new(27, 11),
                    ["flag"] = new(38, 8),
                },
            },
        };
        IMessageParser parser = MessageParserFactory.Create(options, "ascii");
        string report = """
            PATIENT: 12345
            TEST          RESULT       UNIT       FLAG
            WBC           6.2          10^9/L
            HGB           13.8         g/dL       H

            """;

        ParserResult result = parser.Parse(Encoding.ASCII.GetBytes(report));

        Assert.Equal(ParserStatus.Parsed, result.Status);
        Assert.Collection(
            result.Observations,
            first =>
            {
                Assert.Equal("WBC", first.TestCode);
                Assert.Equal("6.2", first.ValueText);
                Assert.Equal("10^9/L", first.Unit);
            },
            second =>
            {
                Assert.Equal("HGB", second.TestCode);
                Assert.Equal("13.8", second.ValueText);
                Assert.Equal("g/dL", second.Unit);
                Assert.Equal("H", second.Flag);
            }
        );
    }
}
