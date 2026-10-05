using System.Collections.Concurrent;
using System.Net;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

// Routes through the actual Notification TestServer. Only the caller-facing
// response is lost after the producer acknowledges durable provider acceptance.
internal sealed class NotificationCallerResponseLossHandler(
    HttpMessageHandler inner, string? intentId, bool loseFirstAcceptedResponse) : DelegatingHandler(inner)
{
    private readonly ConcurrentQueue<(string Method, string Path)> requests = new();
    private int responseLost;
    public (string Method, string Path)[] Requests => requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        requests.Enqueue((request.Method.Method, path));
        var response = await base.SendAsync(request, token);
        if (loseFirstAcceptedResponse && request.Method == HttpMethod.Post &&
            (intentId is null ? path.StartsWith("/notifications/v2/delivery-intents/", StringComparison.Ordinal) && path.EndsWith("/execute", StringComparison.Ordinal)
                : path == "/notifications/v2/delivery-intents/" + intentId + "/execute") && response.StatusCode == HttpStatusCode.OK &&
            Interlocked.CompareExchange(ref responseLost, 1, 0) == 0)
        {
            response.Dispose();
            throw new HttpRequestException("Controlled caller response loss after producer acceptance.");
        }
        return response;
    }
}
