using System.Text;
using System.Text.RegularExpressions;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Parsing;

internal sealed class RegexLinesMessageParser(
    ParserProfileOptions options,
    string encodingName
) : IMessageParser
{
    private readonly Encoding encoding = ParserTextEncoding.GetEncoding(encodingName);
    private readonly RegexLineRule[] rules = options
        .RegexLineRules
        .Select(rule => new RegexLineRule(
            rule,
            new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant)
        ))
        .ToArray();

    public ParserResult Parse(ReadOnlyMemory<byte> payload)
    {
        List<ParsedObservation> observations = [];
        foreach (string line in ParserTextEncoding.SplitLines(encoding.GetString(payload.Span)))
        {
            string trimmedLine = line.Trim();
            if (trimmedLine.Length == 0)
            {
                continue;
            }

            foreach (RegexLineRule rule in rules)
            {
                Match match = rule.Regex.Match(trimmedLine);
                if (!match.Success)
                {
                    continue;
                }

                string? value = GetGroupValue(match, rule.Options.ValueGroup);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                observations.Add(new ParsedObservation(
                    rule.Options.Name,
                    rule.Options.TestCode ?? rule.Options.Name,
                    value,
                    rule.Options.Unit,
                    GetGroupValue(match, rule.Options.FlagGroup)
                ));
            }
        }

        return new ParserResult(
            options.Type,
            options.Profile,
            observations.Count == 0 ? ParserStatus.NoMatch : ParserStatus.Parsed,
            observations,
            []
        );
    }

    private static string? GetGroupValue(Match match, string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return null;
        }

        Group group = match.Groups[groupName];
        return group.Success ? group.Value.Trim() : null;
    }

    private sealed record RegexLineRule(RegexLineRuleOptions Options, Regex Regex);
}
