using System;
using System.Runtime;

namespace MauiApp_bareiron_viewer.Services;

/// <summary>
/// Lightweight RAM pressure guard.
/// Call CheckAsync() between bundle scans; it will GC and optionally delay
/// if free memory drops below the configured threshold.
///
/// Uses GC.GetGCMemoryInfo() which is available on all .NET 5+ targets.
/// Falls back gracefully on platforms that return 0 for TotalAvailableMemoryBytes.
/// </summary>
public static class MemoryGuard
{
    // Pause scanning when less than this many bytes of committed headroom remain.
    // 350 MB is a conservative floor for a MAUI app running on mid-range Android.
    private const long PauseThresholdBytes = 350L * 1024 * 1024;

    // After an aggressive GC, if still below threshold, wait this long before
    // resuming so the OS has a moment to reclaim pages from other pressure.
    private static readonly TimeSpan PauseDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Returns true if memory is under pressure (below threshold).
    /// Performs a gen-0 collect on every call and a full compacting collect
    /// when pressure is detected.
    /// </summary>
    public static bool IsUnderPressure()
    {
        // Quick gen-0 nudge to free short-lived scan temporaries.
        GC.Collect(0, GCCollectionMode.Optimized, blocking: false);

        long free = GetApproximateFreeBytes();
        if (free <= 0) return false; // platform doesn't report — don't throttle

        return free < PauseThresholdBytes;
    }

    /// <summary>
    /// Call between each bundle scan.  If RAM is tight, performs a full GC
    /// and waits <see cref="PauseDelay"/> before returning.
    /// </summary>
    public static async System.Threading.Tasks.Task ThrottleIfNeededAsync()
    {
        long free = GetApproximateFreeBytes();
        if (free <= 0 || free >= PauseThresholdBytes) return;

        // Pressure detected — full blocking compacting collect.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // Re-check after GC.
        free = GetApproximateFreeBytes();
        if (free < PauseThresholdBytes)
        {
            // Still tight — yield to let the OS breathe.
            await System.Threading.Tasks.Task.Delay(PauseDelay);
        }
    }

    /// <summary>
    /// Returns an approximation of free physical memory in bytes.
    /// Uses GCMemoryInfo.TotalAvailableMemoryBytes minus the current heap size.
    /// Returns 0 if the platform doesn't expose this info.
    /// </summary>
    public static long GetApproximateFreeBytes()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            long total = info.TotalAvailableMemoryBytes;
            if (total <= 0) return 0;

            // Committed heap gives a rough "how much are we using" figure.
            long used = GC.GetTotalMemory(forceFullCollection: false);
            return total - used;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Human-readable free memory string for status display.</summary>
    public static string FreeMemoryString()
    {
        long b = GetApproximateFreeBytes();
        if (b <= 0) return "";
        if (b >= 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024 * 1024):F1} GB free";
        return $"{b / (1024.0 * 1024):F0} MB free";
    }
}
