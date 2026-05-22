namespace EdgeGateway.Core.Protocols.Astm;

public sealed record AstmSessionProcessResult(
    IReadOnlyList<byte[]> Responses,
    IReadOnlyList<AstmSessionEvent> Events
);
