namespace ErpPrinter.Outbox;

/// <summary>
/// Folder layout of one profile's spool area:
/// <code>
/// &lt;DataRoot&gt;\spool\&lt;safe profile name&gt;\outbox\&lt;id&gt;.pdf + &lt;id&gt;.json   waiting for delivery
/// ...\failed\...                                                  gave up (permanent error or MaxAttempts)
/// ...\sent\...                                                    delivered (only if KeepSentDays &gt; 0)
/// ...\delivered.json                                              running delivery count and the latest delivery
/// </code>
/// The listener writes the PDF first and the JSON sidecar last, each via a <c>.tmp</c> rename, so the presence of
/// <c>&lt;id&gt;.json</c> means <c>&lt;id&gt;.pdf</c> is complete.
/// </summary>
public sealed record OutboxPaths(string Root, string Outbox, string Failed, string Sent, string Stats)
{
    public const string SpoolFolderName = "spool";

    public static OutboxPaths For(string dataRoot, string profileName)
    {
        var root = Path.Combine(dataRoot, SpoolFolderName, Naming.ToSafeName(profileName));
        return new OutboxPaths(root, Path.Combine(root, "outbox"), Path.Combine(root, "failed"), Path.Combine(root, "sent"), Path.Combine(root, "delivered.json"));
    }

    /// <summary>Creates the outbox, failed and sent folders.</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(Outbox);
        Directory.CreateDirectory(Failed);
        Directory.CreateDirectory(Sent);
    }
}
