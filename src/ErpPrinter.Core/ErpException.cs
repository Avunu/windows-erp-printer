namespace ErpPrinter;

/// <summary>
/// A delivery failure, tagged as permanent (do not retry: the request itself is wrong) or
/// transient (retry later: network, server or credentials may be fixed).
/// </summary>
public sealed class ErpException : Exception
{
    public ErpException(string message, bool permanent = false, Exception? inner = null) : base(message, inner) => Permanent = permanent;

    public bool Permanent { get; }

    /// <summary>True when the exception (or any inner exception) is a permanent ErpException.</summary>
    public static bool IsPermanent(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is ErpException e) return e.Permanent;
        return false;
    }
}
