using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErpPrinter.Logging;

namespace ErpPrinter.Outbox;

/// <summary>One document in an outbox, failed or sent folder: its sidecar and the paths of both files.</summary>
public sealed record OutboxItem(OutboxMeta Meta, string PdfPath, string JsonPath);

/// <summary>Running delivery count and the latest delivery of a profile (<c>delivered.json</c>).</summary>
public sealed class DeliveryStats
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("lastSentAt")] public DateTimeOffset? LastSentAt { get; set; }
    [JsonPropertyName("lastTitle")] public string? LastTitle { get; set; }
    [JsonPropertyName("lastRemoteId")] public string? LastRemoteId { get; set; }
}

/// <summary>
/// Reads and writes the outbox files. Port of Outbox.ps1: sidecars are written through a <c>.tmp</c> rename, reading is
/// tolerant of missing fields, and the delivery statistics never throw.
/// </summary>
public sealed class OutboxStore(IErpLog log, TimeProvider time)
{
    /// <summary>PDFs without a sidecar younger than this are left alone (the listener may still be writing them).</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(5);

    /// <summary>Interrupted <c>.tmp</c> writes older than this are deleted.</summary>
    public static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(60);

    public const string RecoveredTitle = "Recovered print job";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IErpLog Log => log;

    /// <summary>A fresh sidecar for a document that has not been attempted yet.</summary>
    public static OutboxMeta NewMeta(
        string id, string profileName, string user = "", string title = "", string jobId = "", int pages = 0, long sizeBytes = 0,
        DateTimeOffset? printed = null, string? computer = null, TimeProvider? time = null) => new()
    {
        Id = id,
        Profile = profileName,
        User = user,
        Title = title,
        Computer = computer ?? Environment.MachineName,
        JobId = jobId,
        Pages = pages,
        SizeBytes = sizeBytes,
        Printed = printed ?? (time ?? TimeProvider.System).GetLocalNow(),
    };

    /// <summary>Writes a file via <c>&lt;path&gt;.tmp</c> so readers never see a half-written file.</summary>
    public static void WriteFileAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        try { File.Move(tmp, path, overwrite: true); }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { /* best effort; stale .tmp files are cleaned up later */ }
            throw;
        }
    }

    public static string ToJson(OutboxMeta meta) => JsonSerializer.Serialize(meta, JsonOptions);

    public void SaveMeta(string folder, OutboxMeta meta) => WriteFileAtomic(Path.Combine(folder, meta.Id + ".json"), ToJson(meta));

    /// <summary>Items in a folder, oldest first. Unreadable sidecars are logged and skipped.</summary>
    /// <param name="dueOnly">Skip items whose <c>nextAttempt</c> is after <paramref name="now"/> (default: the current time).</param>
    public IReadOnlyList<OutboxItem> List(string folder, bool dueOnly = false, DateTimeOffset? now = null)
    {
        if (!Directory.Exists(folder)) return [];
        var current = now ?? time.GetLocalNow();
        var items = new List<OutboxItem>();
        var files = new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => string.Equals(f.Extension, ".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var json in files)
        {
            OutboxMeta meta;
            try { meta = ReadMeta(json.FullName); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                log.Warning($"Unreadable sidecar {json.FullName}: {ex.Message}");
                continue;
            }
            if (string.IsNullOrEmpty(meta.Id)) meta.Id = Path.GetFileNameWithoutExtension(json.Name);
            if (dueOnly && meta.NextAttempt is { } next && next > current) continue;
            items.Add(new OutboxItem(meta, Path.ChangeExtension(json.FullName, ".pdf"), json.FullName));
        }
        return items;
    }

    private static OutboxMeta ReadMeta(string jsonPath)
    {
        var text = File.ReadAllText(jsonPath, System.Text.Encoding.UTF8);
        var meta = JsonSerializer.Deserialize<OutboxMeta>(text, JsonOptions) ?? throw new JsonException("The file is empty or not a JSON object.");
        // JSON null into a non-nullable string leaves null at runtime; treat it like a missing field.
        meta.Id ??= "";
        meta.Profile ??= "";
        meta.User ??= "";
        meta.Title ??= "";
        meta.JobId ??= "";
        return meta;
    }

    /// <summary>Moves the PDF and its sidecar to <paramref name="destination"/> (the sidecar is rewritten from <see cref="OutboxItem.Meta"/>, last).</summary>
    public void Move(OutboxItem item, string destination)
    {
        Directory.CreateDirectory(destination);
        if (File.Exists(item.PdfPath)) File.Move(item.PdfPath, Path.Combine(destination, Path.GetFileName(item.PdfPath)), overwrite: true);
        SaveMeta(destination, item.Meta);
        try { File.Delete(item.JsonPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* PowerShell: SilentlyContinue */ }
    }

    /// <summary>Deletes both files of a delivered document. Failures are ignored, as in the PowerShell product.</summary>
    public void Remove(OutboxItem item)
    {
        foreach (var path in new[] { item.PdfPath, item.JsonPath })
        {
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>Delivery statistics of a profile, zeroed if nothing was delivered yet or the file is unreadable.</summary>
    public DeliveryStats GetStats(OutboxPaths paths)
    {
        if (!File.Exists(paths.Stats)) return new DeliveryStats();
        try
        {
            var text = File.ReadAllText(paths.Stats, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<DeliveryStats>(text, JsonOptions) ?? new DeliveryStats();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            log.Warning($"Unreadable delivery stats {paths.Stats}: {ex.Message}");
            return new DeliveryStats();
        }
    }

    /// <summary>Counts one delivery. Called after the document is gone from the outbox, so it must never throw.</summary>
    public void AddDeliveryStat(OutboxPaths paths, OutboxMeta meta)
    {
        try
        {
            var stats = GetStats(paths);
            stats.Count++;
            stats.LastSentAt = meta.SentAt;
            stats.LastTitle = meta.Title;
            stats.LastRemoteId = meta.RemoteId;
            WriteFileAtomic(paths.Stats, JsonSerializer.Serialize(stats, JsonOptions));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Warning($"Could not update delivery stats: {ex.Message}");
        }
    }

    /// <summary>
    /// Adopts PDFs that have no sidecar (the listener stopped between writing the PDF and the JSON) and deletes stale <c>.tmp</c>
    /// files from interrupted writes. Only touches files older than a few minutes so in-flight writes are safe.
    /// </summary>
    /// <returns>The sidecars created.</returns>
    public IReadOnlyList<OutboxMeta> RepairOrphans(string folder, string profileName)
    {
        var adopted = new List<OutboxMeta>();
        if (!Directory.Exists(folder)) return adopted;
        var cutoff = time.GetUtcNow() - OrphanAge;
        var files = new DirectoryInfo(folder).EnumerateFiles().ToList();

        foreach (var pdf in files.Where(f => string.Equals(f.Extension, ".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            if (new DateTimeOffset(pdf.LastWriteTimeUtc, TimeSpan.Zero) > cutoff) continue;
            if (File.Exists(Path.ChangeExtension(pdf.FullName, ".json"))) continue;
            var meta = NewMeta(Path.GetFileNameWithoutExtension(pdf.Name), profileName, title: RecoveredTitle, sizeBytes: pdf.Length,
                printed: new DateTimeOffset(pdf.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime());
            SaveMeta(folder, meta);
            adopted.Add(meta);
            log.Warning($"Recovered orphaned print job {pdf.Name} in profile '{profileName}' (user and title unknown).");
        }

        var staleBefore = cutoff - (StaleTempAge - OrphanAge);
        foreach (var tmp in files.Where(f => string.Equals(f.Extension, ".tmp", StringComparison.OrdinalIgnoreCase)))
        {
            if (new DateTimeOffset(tmp.LastWriteTimeUtc, TimeSpan.Zero) >= staleBefore) continue;
            try { tmp.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
        return adopted;
    }
}
