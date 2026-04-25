#if ANDROID
using Android.App;
using Android.Content;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MauiApp_bareiron_viewer.Services;

/// <summary>
/// Monitors device RAM on Android and provides throttling mechanisms to prevent OOM crashes.
/// Uses Android ActivityManager.GetMemoryInfo() to detect available RAM.
/// </summary>
public sealed class MemoryMonitor
{
    private static MemoryMonitor? _instance;
    private static readonly object _lock = new();

    public static MemoryMonitor Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new MemoryMonitor();
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Memory level thresholds (in bytes)
    /// </summary>
    public static readonly long GreenThreshold = 800 * 1024 * 1024;   // >800MB
    public static readonly long YellowThreshold = 400 * 1024 * 1024; // 400-800MB
    public static readonly long OrangeThreshold = 200 * 1024 * 1024; // 200-400MB
    public static readonly long RedThreshold = 100 * 1024 * 1024;     // <100MB

    /// <summary>
    /// Current memory level
    /// </summary>
    public enum MemoryLevel
    {
        Green,
        Yellow,
        Orange,
        Red
    }

    private ActivityManager? _activityManager;
    private bool _warningShown;

    private MemoryMonitor()
    {
        try
        {
            _activityManager = Android.App.Application.Context.GetSystemService(Context.ActivityService) as ActivityManager;
        }
        catch
        {
            // Fallback if service unavailable
        }
    }

    /// <summary>
    /// Gets current available RAM in bytes
    /// </summary>
    public long GetAvailableMemory()
    {
        try
        {
            if (_activityManager != null)
            {
                var memInfo = new ActivityManager.MemoryInfo();
                _activityManager.GetMemoryInfo(memInfo);
                return memInfo.AvailMem;
            }
        }
        catch
        {
            // Fallback
        }

        // Return a conservative estimate if we can't get actual value
        return GreenThreshold + 1;
    }

    /// <summary>
    /// Gets current memory level based on available RAM
    /// </summary>
    public MemoryLevel GetMemoryLevel()
    {
        long available = GetAvailableMemory();

        if (available > GreenThreshold)
            return MemoryLevel.Green;
        else if (available > YellowThreshold)
            return MemoryLevel.Yellow;
        else if (available > OrangeThreshold)
            return MemoryLevel.Orange;
        else
            return MemoryLevel.Red;
    }

    /// <summary>
    /// Checks if we should stop loading due to low memory
    /// </summary>
    public bool ShouldStopLoading()
    {
        return GetMemoryLevel() == MemoryLevel.Red;
    }

    /// <summary>
    /// Throttles if needed based on current memory level.
    /// Call this before loading each asset bundle.
    /// Returns true if throttle was applied, false otherwise.
    /// </summary>
    public async Task<bool> ThrottleIfNeededAsync(CancellationToken ct = default)
    {
        var level = GetMemoryLevel();

        switch (level)
        {
            case MemoryLevel.Green:
                // No throttling needed
                _warningShown = false;
                return false;

            case MemoryLevel.Yellow:
                // Light throttle: small delay + light GC hint
                await Task.Delay(50, ct);
                return true;

            case MemoryLevel.Orange:
                // Medium throttle: longer delay + more aggressive GC hint
                await Task.Delay(150, ct);
                ForceGarbageCollection(light: true);
                return true;

            case MemoryLevel.Red:
                // Stop loading, show warning, force aggressive GC
                if (!_warningShown)
                {
                    _warningShown = true;
                    ShowMemoryWarning();
                }
                ForceGarbageCollection(light: false);
                throw new OutOfMemoryException("Insufficient memory to load bundle. Please close other apps and try again.");
        }

        return false;
    }

    /// <summary>
    /// Forces garbage collection based on severity
    /// </summary>
    public void ForceGarbageCollection(bool light = false)
    {
        try
        {
            if (light)
            {
                // Light GC - only collect generation 0
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
            }
            else
            {
                // Aggressive GC - collect all generations and compact
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
        }
        catch
        {
            // Ignore GC errors
        }
    }

    /// <summary>
    /// Shows a memory warning dialog to the user
    /// </summary>
    private void ShowMemoryWarning()
    {
        try
        {
            Android.App.Application?.Context?.GetSystemService(Context.ActivityService);
            // Note: Cannot show dialogs from a service directly
            // The UI layer should subscribe to memory warnings and handle display
            OnMemoryWarning?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Ignore
        }
    }

    /// <summary>
    /// Event fired when memory warning should be shown
    /// </summary>
    public event EventHandler? OnMemoryWarning;
}
#else
// Stub for non-Android platforms
namespace MauiApp_bareiron_viewer.Services;

public sealed class MemoryMonitor
{
    private static MemoryMonitor? _instance;

    public static MemoryMonitor Instance => _instance ??= new MemoryMonitor();

    public enum MemoryLevel { Green }

    public Task<bool> ThrottleIfNeededAsync(CancellationToken ct = default) => Task.FromResult(false);

    public void ForceGarbageCollection(bool light = false) { }

    public bool ShouldStopLoading() => false;
}
#endif