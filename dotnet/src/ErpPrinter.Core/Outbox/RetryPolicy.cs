namespace ErpPrinter.Outbox;

public static class RetryPolicy
{
    /// <summary>Exponential backoff: 30 s, 60 s, 120 s ... capped at <paramref name="maxMinutes"/>. <paramref name="attempt"/> is the number of attempts made so far (1 after the first failure).</summary>
    public static int DelaySeconds(int attempt, int maxMinutes)
    {
        var exponent = Math.Min(Math.Max(attempt - 1, 0), 20);
        return (int)Math.Min(30d * Math.Pow(2, exponent), maxMinutes * 60d);
    }
}
