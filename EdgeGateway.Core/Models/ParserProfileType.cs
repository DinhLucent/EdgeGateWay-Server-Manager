namespace EdgeGateway.Core.Models;

public enum ParserProfileType
{
    None,
    RegexLines,
    DelimiterTable,
    FixedColumnReport,
    AstmRecordMap,
    Hl7SegmentMap,
    ScriptedPlugin,
    ExternalParser,
}
