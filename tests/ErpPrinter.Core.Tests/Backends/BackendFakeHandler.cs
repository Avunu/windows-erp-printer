using System.Text;

namespace ErpPrinter.Core.Tests;

/// <summary>One request a backend made, with its body decoded to text.</summary>
public sealed record BackendCall(HttpMethod Method, string Uri, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// Stands in for the network: replies are queued per test (status, body) and every call is recorded.
/// A call without a queued reply fails the test, so an unexpected request is noticed.
/// </summary>
public sealed class BackendFakeHandler : HttpMessageHandler
{
    private readonly Queue<(int Status, string Body)> _replies = new();
    private readonly List<BackendCall> _calls = [];

    public IReadOnlyList<BackendCall> Calls => _calls;

    public void Reply(int status, string body) => _replies.Enqueue((status, body));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        _calls.Add(new BackendCall(request.Method, request.RequestUri!.ToString(), headers, body));
        if (!_replies.TryDequeue(out var reply)) throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        return new HttpResponseMessage((System.Net.HttpStatusCode)reply.Status)
        {
            Content = new StringContent(reply.Body, Encoding.UTF8, "application/json"),
        };
    }
}
