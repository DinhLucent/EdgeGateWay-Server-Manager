namespace EdgeGateway.Service.Api.V1;

public sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    string? Error,
    DateTimeOffset CheckedAt
)
{
    public static ApiResponse<T> Ok(T data)
    {
        return new ApiResponse<T>(true, data, null, DateTimeOffset.UtcNow);
    }

    public static ApiResponse<T> Fail(string error)
    {
        return new ApiResponse<T>(false, default, error, DateTimeOffset.UtcNow);
    }
}
