namespace EdgeGateway.Core.Parsing;

public sealed record ParsedObservation(
    string? Name,
    string? TestCode,
    string? ValueText,
    string? Unit,
    string? Flag
);
