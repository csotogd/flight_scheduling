namespace Acsp.Core;

/// <summary>
/// Periodic planning horizon T = {0, 1, ..., N-1} in minutes (§3.1.1).
/// For a weekly schedule planned to-the-minute, N = 7*24*60 = 10080.
/// </summary>
public readonly record struct Period(int N)
{
    public static readonly Period Weekly = new(7 * 24 * 60);

    /// <summary>time(t1, t2): forward time from t1 to t2, wrapping into the next period when t2 &lt; t1.</summary>
    public int Time(int t1, int t2) => t2 >= t1 ? t2 - t1 : t2 - t1 + N;

    /// <summary>Normalizes an absolute minute value into [0, N).</summary>
    public int Wrap(int t)
    {
        int m = t % N;
        return m < 0 ? m + N : m;
    }
}
