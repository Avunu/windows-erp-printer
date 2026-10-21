using System.Text.Json;
using System.Text.Json.Serialization;
using ErpPrinter.Outbox;

namespace ErpPrinter.Diagnostics;

/// <summary>The per-profile folders under the data root: <c>spool\&lt;safe name&gt;\{outbox,failed,sent}</c> and <c>delivered.json</c>.</summary>
public sealed record ProfileFolders(string Root, string Outbox, string Failed, string Sent, string Stats)
{
    public static ProfileFolders For(string dataRoot, string profileName)
    {
        var root = Path.Combine(dataRoot, "spool", Naming.ToSafeName(profileName));
        return new ProfileFolders(root, Path.Combine(root, "outbox"), Path.Combine(root, "failed"), Path.Combine(root, "sent"), Path.Combine(root, "delivered.json"));
    }
}

/// <summary>A document found in one of a profile's folders. <see cref="Folder"/> is "outbox", "failed" or "sent".</summary>
public sealed record ScannedOutboxItem(string Id, string Folder, OutboxMeta Meta);

/// <summary>The running delivery count kept in <c>delivered.json</c>.</summary>
public sealed class DeliveryStats
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("lastSentAt")] public DateTimeOffset? LastSentAt { get; set; }
    [JsonPropertyName("lastTitle")] public string? LastTitle { get; set; }
    [JsonPropertyName("lastRemoteId")] public string? LastRemoteId { get; set; }
}

/// <summary>
/// Reads the outbox sidecars and delivery statistics (read-only, tolerant of unreadable files). The listener and uploader own writing them.
/// </summary>
public static class OutboxReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The sidecars in <paramref name="folder"/>, oldest first by last write time. Unreadable or half-written files are skipped.</summary>
    public static async Task<IReadOnlyList<ScannedOutboxItem>> ReadFolderAsync(string folder, string folderName, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder)) return [];
        List<FileInfo> files;
        try { files = new DirectoryInfo(folder).EnumerateFiles("*.json").OrderBy(f => f.LastWriteTimeUtc).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var items = new List<ScannedOutboxItem>();
        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            var meta = await TryReadAsync<OutboxMeta>(file.FullName, cancellationToken).ConfigureAwait(false);
            if (meta is null) continue;
            if (meta.Id.Length == 0) meta.Id = id;
            items.Add(new ScannedOutboxItem(id, folderName, meta));
        }
        return items;
    }

    public static async Task<DeliveryStats> ReadStatsAsync(string statsPath, CancellationToken cancellationToken = default) =>
        await TryReadAsync<DeliveryStats>(statsPath, cancellationToken).ConfigureAwait(false) ?? new DeliveryStats();

    private static async Task<T?> TryReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>Finds the test page's document while a test print runs.</summary>
public interface IOutboxScanner
{
    /// <summary>Ids of every document already in the outbox, failed and sent folders, taken before printing.</summary>
    Task<IReadOnlySet<string>> GetKnownIdsAsync(ProfileFolders folders, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks for the test page's document in the outbox, failed and sent folders, ignoring <paramref name="known"/> ids. Matches the unique
    /// token in the title; if the spooler job could not be looked up (so the title is empty) a single new untitled document is accepted.
    /// </summary>
    Task<ScannedOutboxItem?> FindTestPageAsync(ProfileFolders folders, string token, IReadOnlySet<string> known, CancellationToken cancellationToken = default);

    /// <summary>
    /// The profile's running delivery count and last delivery. With KeepSentDays = 0 a delivered document is deleted at once, sometimes
    /// before the test print ever sees it in a folder; the last delivered title still shows that it arrived.
    /// </summary>
    Task<DeliveryStats> GetDeliveryStatsAsync(ProfileFolders folders, CancellationToken cancellationToken = default);
}

/// <summary>Port of Find-ErpTestPrintItem on the real file system.</summary>
public sealed class FileSystemOutboxScanner : IOutboxScanner
{
    public async Task<IReadOnlySet<string>> GetKnownIdsAsync(ProfileFolders folders, CancellationToken cancellationToken = default)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, name) in Folders(folders))
            foreach (var item in await OutboxReader.ReadFolderAsync(folder, name, cancellationToken).ConfigureAwait(false))
                known.Add(item.Id);
        return known;
    }

    public async Task<ScannedOutboxItem?> FindTestPageAsync(ProfileFolders folders, string token, IReadOnlySet<string> known, CancellationToken cancellationToken = default)
    {
        var candidates = new List<ScannedOutboxItem>();
        foreach (var (folder, name) in Folders(folders))
            foreach (var item in await OutboxReader.ReadFolderAsync(folder, name, cancellationToken).ConfigureAwait(false))
                if (!known.Contains(item.Id)) candidates.Add(item);

        // A sidecar with "title": null deserializes to a null string despite the annotation.
        var match = candidates.FirstOrDefault(c => (c.Meta.Title ?? "").Contains(token, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        var untitled = candidates.Where(c => string.IsNullOrEmpty(c.Meta.Title)).ToList();
        return untitled.Count == 1 ? untitled[0] : null;
    }

    public Task<DeliveryStats> GetDeliveryStatsAsync(ProfileFolders folders, CancellationToken cancellationToken = default) =>
        OutboxReader.ReadStatsAsync(folders.Stats, cancellationToken);

    private static IEnumerable<(string Path, string Name)> Folders(ProfileFolders f) =>
        [(f.Outbox, "outbox"), (f.Failed, "failed"), (f.Sent, "sent")];
}
