using System;

namespace SceneBuilder.Editor.Licensing
{
    // Sole owner of every named licensing duration and the one clock seam (UtcNow).
    public static class LicenseClock
    {
        public static readonly TimeSpan OfflineBudget = TimeSpan.FromDays(14);
        public static readonly TimeSpan RefreshCadence = TimeSpan.FromDays(1);
        public static readonly TimeSpan NearExpiryWarningWindow = TimeSpan.FromDays(3);
        public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromHours(1);

        public static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        public static bool IsClockRolledBack(DateTime lastObservedUtc)
        {
            return lastObservedUtc != DateTime.MinValue && UtcNow() < lastObservedUtc - ClockSkewTolerance;
        }
    }
}
