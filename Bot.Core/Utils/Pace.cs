using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Utils
{
    /// <summary>
    /// Fixed UI waits (after taps, swipes, back) scaled by KINGSHOT_DELAY_SCALE (default 0.6).
    /// Raise it toward 1.0 if taps start landing before screens finish opening.
    /// Waits over 3s (loading, restarts) are left alone.
    /// </summary>
    public static class Pace
    {
        public static readonly double Scale =
            double.TryParse(Environment.GetEnvironmentVariable("KINGSHOT_DELAY_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
                ? Math.Min(s, 1.0) : 0.6;

        public static int Ms(int ms) => ms <= 3000 ? Math.Max(100, (int)(ms * Scale)) : ms;

        public static Task Delay(int ms, CancellationToken cancellationToken) => Task.Delay(Ms(ms), cancellationToken);
    }
}
