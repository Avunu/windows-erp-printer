using System.Text.Json.Nodes;

namespace ErpPrinter.Http;

/// <summary>
/// What <see cref="ErpHttp"/> got back: the status code, the body as text (empty for downloads) and, when the body
/// looked like JSON and parsed, the parsed document. HTTP error statuses are returned, not thrown; pass the result to
/// <see cref="ErpHttp.AssertSuccess"/> to turn them into classified exceptions.
/// </summary>
public sealed record HttpResult(int StatusCode, string Body, JsonNode? Json)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}
