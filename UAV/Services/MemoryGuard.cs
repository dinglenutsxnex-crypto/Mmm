using System;
using System.Threading.Tasks;

namespace UAV.Services;

/// <summary>
/// Platform-aware RAM pressure guard.
/// Prevents the app from crashing by checking available device memory
/// before loading each bundle. If free RAM drops below <see cref="ThresholdBytes"/>,
/// loading pauses, GC is forced, and if memory is still critical, loading stops.
/// </summary>
public static class MemoryGuard
{
    /// <summary>
    /// Minimum free RAM required to continue loading bundles.
    /// 800 MB is a safe default — adjust lower (e.g. 400 MB) on
    /// devices you know have limited RAM.
    /// </summary>
    public const long ThresholdBytes = 800L * 1024 * 1024; // 800 MB

    /// <summary>
    /// Returns the best estimate of currently available (free) device RAM in bytes.
    /// On Android this uses the system ActivityManager so it reflects true device pressure.
    /// On other platforms it falls back to CLR GC info, which is less accurate but still useful.
    /// </summary>
    public static long GetAvailableBytes()
    {
#if ANDROID
        try
        {
            var activityManager =
                Android.App.Application.Context.GetSystemService(
                    Android.Content.Context.ActivityService)
                as Android.App.ActivityManager;

            if (activityManager != null)
            {
                var mi = new Android.App.ActivityManager.MemoryInfo();
                activityManager.GetMemoryInfo(mi);
                return mi.AvailMem; // true free RAM from the OS
            }
        }
        catch { /* fall through to GC fallback */ }
#endif
        // Cross-platform fallback: total available to the CLR minus what we've already used
        var gcInfo = GC.GetGCMemoryInfo();
        long totalAvailable = gcInfo.TotalAvailableMemoryBytes;
        long alreadyUsed    = GC.GetTotalMemory(false);
        return Math.Max(0L, totalAvailable - alreadyUsed);
    }

    /// <summary>True when free RAM is below the configured threshold.</summary>
    public static bool IsMemoryLow() => GetAvailableBytes() < ThresholdBytes;

    /// <summary>
    /// Tries to recover memory by forcing a full GC compaction.
    /// Retries up to <paramref name="retries"/> times with <paramref name="delayMs"/> between each.
    /// Returns true if memory is no longer critically low after recovery.
    /// </summary>
    public static async Task<bool> TryRecoverAsync(int retries = 3, int delayMs = 600)
    {
        for (int i = 0; i < retries; i++)
        {
            if (!IsMemoryLow()) return true;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive,
                       blocking: true, compacting: true);
            await Task.Delay(delayMs);
        }
        return !IsMemoryLow();
    }

    /// <summary>
    /// Returns a human-readable description of current memory state, e.g. "1.2 GB free".
    /// </summary>
    public static string GetStatusString()
    {
        long free = GetAvailableBytes();
        return free >= 1024L * 1024 * 1024
            ? $"{free / (1024.0 * 1024 * 1024):F1} GB free"
            : $"{free / (1024.0 * 1024):F0} MB free";
    }
}
