namespace EdgeGateway.Core.Models;

public sealed record AstmOrderReplyOptions
{
    public bool Enabled { get; init; }

    public bool AutoReplyMatchedQuery { get; init; }

    public AstmUnknownQueryPolicy UnknownQueryPolicy { get; init; } =
        AstmUnknownQueryPolicy.LogOnly;

    public string Host { get; init; } = "EdgeGateway";

    public AstmOrderTemplateOptions Template { get; init; } = new();
}
