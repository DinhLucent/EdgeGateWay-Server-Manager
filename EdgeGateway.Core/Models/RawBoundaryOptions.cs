namespace EdgeGateway.Core.Models;

public sealed record RawBoundaryOptions
{
    public RawBoundaryMode Mode { get; init; } = RawBoundaryMode.RawChunk;

    public string Delimiter { get; init; } = "CRLF";

    public string? CustomDelimiter { get; init; }

    public int? FixedLength { get; init; }

    public string? StartBytes { get; init; }

    public string? EndBytes { get; init; }

    public bool IncludeBoundary { get; init; }

    public int MaxMessageBytes { get; init; } = 65_536;
}
