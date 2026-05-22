using System.Collections.Concurrent;
using System.Net;

namespace EdgeGateway.Tests;

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory;
    private int attemptCount;

    public RecordingHttpMessageHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage>? responseFactory = null
    )
    {
        this.responseFactory = responseFactory
            ?? ((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
    }

    public ConcurrentQueue<string> RequestBodies { get; } = new();

    public int AttemptCount => attemptCount;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        int attempt = Interlocked.Increment(ref attemptCount);
        if (request.Content is not null)
        {
            RequestBodies.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        return responseFactory(request, attempt);
    }
}
