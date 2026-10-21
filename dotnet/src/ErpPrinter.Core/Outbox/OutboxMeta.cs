using System.Text.Json.Serialization;

namespace ErpPrinter.Outbox;

/// <summary>
/// The <c>&lt;id&gt;.json</c> sidecar next to each spooled PDF. Property names and the ISO 8601 timestamps match the
/// PowerShell product's files, so documents queued by an older version are delivered by the new one.
/// Missing fields (older or hand-written sidecars) read as their defaults.
/// </summary>
public sealed class OutboxMeta
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("profile")] public string Profile { get; set; } = "";
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("computer")] public string? Computer { get; set; }
    [JsonPropertyName("jobId")] public string JobId { get; set; } = "";
    [JsonPropertyName("pages")] public int Pages { get; set; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("printed")] public DateTimeOffset? Printed { get; set; }
    [JsonPropertyName("attempts")] public int Attempts { get; set; }
    [JsonPropertyName("lastAttempt")] public DateTimeOffset? LastAttempt { get; set; }
    [JsonPropertyName("nextAttempt")] public DateTimeOffset? NextAttempt { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
    [JsonPropertyName("remoteId")] public string? RemoteId { get; set; }
    [JsonPropertyName("sentAt")] public DateTimeOffset? SentAt { get; set; }
}
