using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UAV.Services;

/// <summary>
/// Compact asset descriptor — only what the table needs. Stored as a flat array.
/// ~56 bytes per entry. Type strings are interned so "Texture2D" exists once in memory.
/// </summary>
public readonly struct AssetDescriptor
{
    public readonly long   PathId;
    public readonly int    BundleSlot;
    public readonly int    SubFileIndex;
    public readonly long   ByteSize;
    public readonly string Type;   // interned
    public readonly string Name;

    public AssetDescriptor(long pathId, int bundleSlot, int subFileIndex,
                           long byteSize, string type, string name)
    {
        PathId       = pathId;
        BundleSlot   = bundleSlot;
        SubFileIndex = subFileIndex;
        ByteSize     = byteSize;
        Type         = string.Intern(type);
        Name         = name;
    }
}

/// <summary>
/// Per-bundle state. Before first click: stores file path only.
/// After first click: holds the open AssetsManager + sub-files.
/// </summary>
internal sealed class BundleSlot
{
    public readonly string  DisplayName;
    public          string  FilePath;
    public readonly string? SafUri;

    public AssetsFileInstance?[] OpenFiles   = Array.Empty<AssetsFileInstance?>();
    public AssetsManager?        OpenManager;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BundleSlot(string displayName, string filePath, string? safUri = null)
    {
        DisplayName = displayName;
        FilePath    = filePath;
        SafUri      = safUri;
    }

    public async Task<AssetsFileInstance?> GetOrOpenSubFileAsync(int subFileIndex)
    {
        await _lock.WaitAsync();
        try
        {
            if (OpenManager == null)
            {
#if ANDROID
                if (SafUri != null)
                {
                    OpenManager = new AssetsManager();
                    using var stream = AndroidDownloadService.OpenSafStream(SafUri);
                    var bundle = OpenManager.LoadBundleFile(stream, FilePath);
                    int count  = bundle.file.BlockAndDirInfo.DirectoryInfos.Count;
                    OpenFiles  = new AssetsFileInstance?[count];
                    for (int i = 0; i < count; i++)
                    {
                        try { OpenFiles[i] = OpenManager.LoadAssetsFileFromBundle(bundle, i); }
                        catch { }
                    }
                    return subFileIndex >= 0 && subFileIndex < OpenFiles.Length
                        ? OpenFiles[subFileIndex] : null;
                }
#endif
                OpenManager = new AssetsManager();
                var bundleFile = OpenManager.LoadBundleFile(FilePath);
                int fileCount  = bundleFile.file.BlockAndDirInfo.DirectoryInfos.Count;
                OpenFiles      = new AssetsFileInstance?[fileCount];
                for (int i = 0; i < fileCount; i++)
                {
                    try { OpenFiles[i] = OpenManager.LoadAssetsFileFromBundle(bundleFile, i); }
                    catch { }
                }
            }
            return subFileIndex >= 0 && subFileIndex < OpenFiles.Length
                ? OpenFiles[subFileIndex] : null;
        }
        finally { _lock.Release(); }
    }

    public void Release()
    {
        _lock.Wait();
        try { OpenFiles = Array.Empty<AssetsFileInstance?>(); OpenManager = null; }
        finally { _lock.Release(); }
    }
}

internal static class FontTypeGuard
{
    private static readonly HashSet<string> BlockedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Font", "TMP_FontAsset", "TMP_SpriteAsset", "TextMeshProFont", "FontDef", "GUISkin",
    };

    public static bool IsBlocked(string typeName) => BlockedTypes.Contains(typeName);
}

// ── Parallel scan result from one worker ─────────────────────────────────────
internal sealed class ScanResult
{
    public string      DisplayName = "";
    public string      FilePath    = "";
    public string?     SafUri;
    public List<AssetDescriptor> Descriptors = new();
    public string?     Error;
    public string?     FontSkip;
}

/// <summary>
/// Adaptive parallelism controller.
///
/// Calibration phase (on start and after memory-pressure reset):
///   Scans SAMPLE_BUNDLES files at workers=1, measures MB/s.
///   Bumps to workers=2, scans SAMPLE_BUNDLES more, compares.
///   Keeps bumping until MB/s stops improving; settles at best.
///
/// Stable phase (after calibration):
///   Re-probes +1 worker every PROBE_INTERVAL_S seconds, but ONLY
///   when at least SAMPLE_BUNDLES small files remain in the queue.
///   If fewer remain, the probe is silently skipped.
///   Rolls back if probe does not beat DROP_THRESHOLD * bestMbps.
///
/// Memory pressure:
///   Call ForceMinimum() to immediately drop to 1 worker,
///   then ResetCalibration() after GC + delay to re-discover sweet spot.
///
/// Underrun safety:
///   The caller always slices batches with Math.Min(CurrentWorkers, remaining),
///   so a batch of 1 when workers=N is normal and handled correctly here.
/// </summary>
internal sealed class ParallelAdaptor
{
    // ── Public constants ──────────────────────────────────────────────────────
    public const long PARALLEL_SIZE_CAP = 15L * 1024 * 1024;  // >15 MB → sequential
    public const int  SAMPLE_BUNDLES    = 15;                   // bundles to measure per level

    // ── Private tuning ────────────────────────────────────────────────────────
    private const double DROP_THRESHOLD   = 0.70;   // keep bump if mbps >= 70% of best
    private const double PROBE_INTERVAL_S = 60.0;   // seconds between stable re-probes

    // ── State ─────────────────────────────────────────────────────────────────
    public int CurrentWorkers { get; private set; } = 1;

    private enum Phase { Calibrating, Stable }
    private Phase _phase = Phase.Calibrating;

    // Calibration window
    private int    _calSampled     = 0;
    private long   _calBytes       = 0;
    private long   _calStartTick   = Stopwatch.GetTimestamp();
    private double _bestMbps       = 0;
    private int    _bestWorkers    = 1;

    // Stable re-probe window
    private long   _lastProbeTick  = Stopwatch.GetTimestamp();
    private bool   _probing        = false;
    private int    _probeSampled   = 0;
    private long   _probeBytes     = 0;
    private long   _probeStartTick = 0;

    // ── API ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Report a completed batch. Returns next recommended worker count.
    /// remainingBundles = small files still queued AFTER this batch.
    /// </summary>
    public int ReportBatch(long bytesProcessed, int filesInBatch, int remainingBundles = int.MaxValue)
    {
        if (_phase == Phase.Calibrating)
            return HandleCalibration(bytesProcessed, filesInBatch);

        return HandleStable(bytesProcessed, filesInBatch, remainingBundles);
    }

    /// <summary>Immediately drops to 1 worker. Call before GC + delay under memory pressure.</summary>
    public void ForceMinimum() => CurrentWorkers = 1;

    /// <summary>
    /// Restarts full calibration from workers=1.
    /// Call after memory pressure is relieved to re-discover the sweet spot.
    /// </summary>
    public void ResetCalibration()
    {
        CurrentWorkers  = 1;
        _phase          = Phase.Calibrating;
        _calSampled     = 0;
        _calBytes       = 0;
        _calStartTick   = Stopwatch.GetTimestamp();
        _bestMbps       = 0;
        _bestWorkers    = 1;
        _probing        = false;
        _probeSampled   = 0;
        _probeBytes     = 0;
        _lastProbeTick  = Stopwatch.GetTimestamp();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private int HandleCalibration(long bytesProcessed, int filesInBatch)
    {
        _calSampled += filesInBatch;
        _calBytes   += bytesProcessed;

        if (_calSampled < SAMPLE_BUNDLES)
            return CurrentWorkers;  // still collecting this level's sample

        long   now     = Stopwatch.GetTimestamp();
        double elapsed = (double)(now - _calStartTick) / Stopwatch.Frequency;
        double mbps    = elapsed > 0 ? (_calBytes / (1024.0 * 1024.0)) / elapsed : 0;

        if (_bestMbps == 0 || mbps > _bestMbps)
        {
            // Better — record and try one more level up
            _bestMbps     = mbps;
            _bestWorkers  = CurrentWorkers;
            CurrentWorkers++;
            _calSampled   = 0;
            _calBytes     = 0;
            _calStartTick = now;
            // Stay in Calibrating phase
        }
        else
        {
            // No improvement — settle at best found
            CurrentWorkers  = _bestWorkers;
            _phase          = Phase.Stable;
            _lastProbeTick  = now;
        }

        return CurrentWorkers;
    }

    private int HandleStable(long bytesProcessed, int filesInBatch, int remainingBundles)
    {
        long   now        = Stopwatch.GetTimestamp();
        double sinceProbe = (double)(now - _lastProbeTick) / Stopwatch.Frequency;

        if (_probing)
        {
            _probeSampled += filesInBatch;
            _probeBytes   += bytesProcessed;

            if (_probeSampled >= SAMPLE_BUNDLES)
            {
                double elapsed = (double)(now - _probeStartTick) / Stopwatch.Frequency;
                double mbps    = elapsed > 0 ? (_probeBytes / (1024.0 * 1024.0)) / elapsed : 0;

                if (mbps >= _bestMbps * DROP_THRESHOLD)
                {
                    _bestMbps    = Math.Max(_bestMbps, mbps);
                    _bestWorkers = CurrentWorkers;
                }
                else
                {
                    CurrentWorkers = _bestWorkers;  // roll back
                }

                _probing       = false;
                _probeSampled  = 0;
                _probeBytes    = 0;
                _lastProbeTick = now;
            }
        }
        else if (sinceProbe >= PROBE_INTERVAL_S)
        {
            if (remainingBundles >= SAMPLE_BUNDLES)
            {
                // Enough bundles left to get a real measurement
                CurrentWorkers++;
                _probing        = true;
                _probeSampled   = 0;
                _probeBytes     = 0;
                _probeStartTick = now;
                _lastProbeTick  = now;
            }
            else
            {
                // Not enough bundles left — stay quiet, reset timer
                _lastProbeTick = now;
            }
        }

        return CurrentWorkers;
    }
}

/// <summary>
/// Central registry. Holds only compact AssetDescriptor[] at rest.
/// Full bundle files are opened on demand when an asset is clicked.
///
/// Parallel scan: files &lt;= 15 MB are scanned in parallel when >= 200 bundles
/// are queued. Parallelism is discovered via a calibration phase (15-bundle
/// samples per level) that keeps bumping until MB/s stops improving.
/// Files > 15 MB always run single-threaded with aggressive GC.
///
/// Memory guard: if free RAM drops below 450 MB during scan, workers drop to
/// 1, GC runs, and calibration restarts after a 10-second breathing window.
/// </summary>
public sealed class BundleRegistry : IDisposable
{
    private readonly List<BundleSlot>  _slots       = new();
    private          AssetDescriptor[] _descriptors = Array.Empty<AssetDescriptor>();
    private          int               _count       = 0;

    public ReadOnlySpan<AssetDescriptor> All   => _descriptors.AsSpan(0, _count);
    public int                           Count => _count;
    public List<string> ScanErrors         { get; } = new();
    public List<string> SkippedFontBundles { get; } = new();

    private const int  MIN_BUNDLES_FOR_PARALLEL = 200;
    private const long LARGE_FILE_BYTES         = 30L * 1024 * 1024;
    private const long MEMORY_PRESSURE_BYTES    = 450L * 1024 * 1024;

    public void Clear()
    {
        foreach (var s in _slots) s.Release();
        _slots.Clear();
        _descriptors = Array.Empty<AssetDescriptor>();
        _count = 0;
        ScanErrors.Clear();
        SkippedFontBundles.Clear();
    }

    // ── GC helpers ────────────────────────────────────────────────────────────

    private static void AggressiveGC()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static void NudgeGC()
        => GC.Collect(0, GCCollectionMode.Optimized, blocking: false);

    // ── Bulk scan entry point (called from PickFolder) ────────────────────────

    /// <summary>
    /// Scans all files. Uses adaptive parallel for small files when total >= 200.
    /// Progress callback receives the number of files completed in each batch.
    ///
    /// File sizes are resolved up-front before any loading begins, so large-file
    /// routing is decided before the first byte is read.
    /// </summary>
    public async Task ScanAllAsync(
        IReadOnlyList<(string FilePath, string DisplayName, string? SafUri)> files,
        Action<int>? onProgress = null)
    {
        if (files.Count == 0) return;

        bool useParallel = files.Count >= MIN_BUNDLES_FOR_PARALLEL;

        if (!useParallel)
        {
            foreach (var (fp, dn, su) in files)
            {
                ScanFile(fp, dn, su);
                onProgress?.Invoke(1);
            }
            return;
        }

        // ── Resolve ALL file sizes BEFORE any loading ─────────────────────────
        // Large files (>15 MB) go to the sequential queue; SAF URIs always go
        // sequential since we cannot cheaply size them.
        var smallFiles = new List<(string FilePath, string DisplayName, string? SafUri, long Size)>();
        var largeFiles = new List<(string FilePath, string DisplayName, string? SafUri)>();

        foreach (var (fp, dn, su) in files)
        {
            long sz = 0;
            try { if (su == null) sz = new FileInfo(fp).Length; } catch { }

            if (sz > ParallelAdaptor.PARALLEL_SIZE_CAP || su != null)
                largeFiles.Add((fp, dn, su));
            else
                smallFiles.Add((fp, dn, su, sz));
        }

        var adaptor = new ParallelAdaptor();
        int idx     = 0;

        // ── Small files: adaptive parallel ───────────────────────────────────
        while (idx < smallFiles.Count)
        {
            // ── Memory pressure check ─────────────────────────────────────────
            // If free RAM < 450 MB: drop to single worker, GC, wait 10 s,
            // then reset calibration so the sweet spot is re-discovered fresh.
            long freeBytes = MemoryGuard.GetApproximateFreeBytes();
            if (freeBytes > 0 && freeBytes < MEMORY_PRESSURE_BYTES)
            {
                adaptor.ForceMinimum();
                AggressiveGC();
                await Task.Delay(TimeSpan.FromSeconds(10));
                AggressiveGC();
                adaptor.ResetCalibration();
            }

            int workers   = adaptor.CurrentWorkers;
            int remaining = smallFiles.Count - idx;

            // Math.Min handles end-of-list underrun: if workers=4 but only 1
            // file remains, we take 1 — no crash, no empty slots.
            int take  = Math.Min(workers, remaining);
            var batch = smallFiles.GetRange(idx, take);
            idx      += take;

            long batchBytes = batch.Sum(f => f.Size);

            ScanResult[] results = new ScanResult[batch.Count];

            if (workers == 1 || batch.Count == 1)
            {
                for (int i = 0; i < batch.Count; i++)
                    results[i] = ScanFileToResult(batch[i].FilePath, batch[i].DisplayName, batch[i].SafUri);
            }
            else
            {
                await Task.Run(() =>
                    Parallel.For(0, batch.Count,
                        new ParallelOptions { MaxDegreeOfParallelism = workers },
                        i => results[i] = ScanFileToResult(batch[i].FilePath, batch[i].DisplayName, batch[i].SafUri)));
            }

            MergeResults(results);
            onProgress?.Invoke(batch.Count);

            // Tell the adaptor how many small files remain so it can decide
            // whether to bother probing (60 s recheck respects this).
            int remainingAfter = smallFiles.Count - idx;
            adaptor.ReportBatch(batchBytes, batch.Count, remainingAfter);
            NudgeGC();
        }

        // ── Large files: always sequential with aggressive GC ─────────────────
        foreach (var (fp, dn, su) in largeFiles)
        {
            AggressiveGC();
            ScanFile(fp, dn, su);
            AggressiveGC();
            onProgress?.Invoke(1);
        }
    }

    // ── Merge parallel results into shared registry ───────────────────────────

    private void MergeResults(ScanResult[] results)
    {
        foreach (var r in results)
        {
            if (r == null) continue;
            if (r.FontSkip != null) { SkippedFontBundles.Add(r.FontSkip); continue; }
            if (r.Error    != null) { ScanErrors.Add(r.Error);             continue; }
            if (r.Descriptors.Count == 0) continue;

            int slotIndex = _slots.Count;
            _slots.Add(new BundleSlot(r.DisplayName, r.FilePath, r.SafUri));

            EnsureCapacity(_count + r.Descriptors.Count);
            foreach (var d in r.Descriptors)
            {
                _descriptors[_count++] = new AssetDescriptor(
                    d.PathId, slotIndex, d.SubFileIndex, d.ByteSize, d.Type, d.Name);
            }
        }
    }

    // ── Parallel-safe scan: no shared state writes ────────────────────────────

    private static ScanResult ScanFileToResult(string filePath, string displayName, string? safUri)
    {
        var result = new ScanResult { DisplayName = displayName, FilePath = filePath, SafUri = safUri };
        try
        {
            var mgr = new AssetsManager();
            bool any = false;
            try
            {
#if ANDROID
                if (safUri != null)
                {
                    using var stream = AndroidDownloadService.OpenSafStream(safUri);
                    var bundle = mgr.LoadBundleFile(stream, filePath);
                    var dirs   = bundle.file.BlockAndDirInfo.DirectoryInfos;
                    for (int i = 0; i < dirs.Count; i++)
                    {
                        try
                        {
                            var af = mgr.LoadAssetsFileFromBundle(bundle, i);
                            if (af?.file?.AssetInfos == null) continue;
                            ScanSubFileToResult(af, mgr, i, result);
                            any = true;
                        }
                        catch (InvalidOperationException ioe) when (ioe.Message.StartsWith("FONT_BUNDLE:"))
                        { result.Descriptors.Clear(); result.FontSkip = displayName; return result; }
                        catch { }
                    }
                }
                else
#endif
                {
                    var bundle = mgr.LoadBundleFile(filePath);
                    var dirs   = bundle.file.BlockAndDirInfo.DirectoryInfos;
                    for (int i = 0; i < dirs.Count; i++)
                    {
                        try
                        {
                            var af = mgr.LoadAssetsFileFromBundle(bundle, i);
                            if (af?.file?.AssetInfos == null) continue;
                            ScanSubFileToResult(af, mgr, i, result);
                            any = true;
                        }
                        catch (InvalidOperationException ioe) when (ioe.Message.StartsWith("FONT_BUNDLE:"))
                        { result.Descriptors.Clear(); result.FontSkip = displayName; return result; }
                        catch { }
                    }
                }
            }
            catch (InvalidOperationException fontEx) when (fontEx.Message.StartsWith("FONT_BUNDLE:"))
            { result.Descriptors.Clear(); result.FontSkip = displayName; return result; }
            catch (Exception bundleEx)
            {
                try
                {
                    var af = mgr.LoadAssetsFile(filePath);
                    if (af?.file?.AssetInfos != null) { ScanSubFileToResult(af, mgr, 0, result); any = true; }
                }
                catch (InvalidOperationException fontEx2) when (fontEx2.Message.StartsWith("FONT_BUNDLE:"))
                { result.Descriptors.Clear(); result.FontSkip = displayName; return result; }
                catch (Exception assetsEx)
                { result.Error = $"{displayName}: {bundleEx.Message} | {assetsEx.Message}"; return result; }
            }

            if (!any) result.Descriptors.Clear();
        }
        catch (Exception ex) { result.Error = $"{displayName}: {ex.Message}"; }
        return result;
    }

    private static void ScanSubFileToResult(AssetsFileInstance af, AssetsManager mgr, int subFile, ScanResult result)
    {
        var infos     = af.file.AssetInfos;
        var typeCache = new Dictionary<int, (string TypeName, bool HasName)>();
        int gcCounter = 0;

        foreach (var info in infos)
        {
            if (!typeCache.TryGetValue(info.TypeId, out var tc))
            {
                try
                {
                    var probe = mgr.GetBaseField(af, info);
                    tc = (probe.TypeName ?? "Unknown", probe["m_Name"] is { IsDummy: false });
                }
                catch { tc = ("Unknown", false); }
                typeCache[info.TypeId] = tc;
            }

            if (FontTypeGuard.IsBlocked(tc.TypeName))
                throw new InvalidOperationException("FONT_BUNDLE:" + tc.TypeName);

            string name = "";
            if (tc.HasName)
            {
                try
                {
                    var bf        = mgr.GetBaseField(af, info);
                    var nameField = bf["m_Name"];
                    name = nameField.IsDummy ? "" : (nameField.AsString ?? "");
                    bf   = null!;
                }
                catch { }

                if (++gcCounter % 2000 == 0)
                    GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
            }

            result.Descriptors.Add(new AssetDescriptor(
                info.PathId, -1, subFile, info.ByteSize, tc.TypeName, name));
        }
    }

    // ── Legacy single-file scan (PickFile + sequential fallback) ─────────────

    public void ScanFile(string filePath, string displayName, string? safUri = null)
    {
        bool isLargeFile = false;
        try { isLargeFile = safUri == null && new FileInfo(filePath).Length >= LARGE_FILE_BYTES; }
        catch { }

        if (isLargeFile) AggressiveGC();

        var result = ScanFileToResult(filePath, displayName, safUri);
        MergeResults(new[] { result });

        if (isLargeFile) AggressiveGC(); else NudgeGC();
    }

    // ── Accessors ─────────────────────────────────────────────────────────────

    public Task<AssetsFileInstance?> GetLiveFileAsync(in AssetDescriptor desc)
    {
        if (desc.BundleSlot < 0 || desc.BundleSlot >= _slots.Count)
            return Task.FromResult<AssetsFileInstance?>(null);
        return _slots[desc.BundleSlot].GetOrOpenSubFileAsync(desc.SubFileIndex);
    }

    public AssetsManager? GetOpenManager(int slot)
        => slot >= 0 && slot < _slots.Count ? _slots[slot].OpenManager : null;

    public string GetBundleName(int slot)
        => slot >= 0 && slot < _slots.Count ? _slots[slot].DisplayName : "";

    private void EnsureCapacity(int needed)
    {
        if (needed <= _descriptors.Length) return;
        int newSize = Math.Max(needed, Math.Max(1024, _descriptors.Length * 2));
        Array.Resize(ref _descriptors, newSize);
    }

    public void Dispose() => Clear();
}
