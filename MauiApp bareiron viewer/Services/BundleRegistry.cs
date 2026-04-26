using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace MauiApp_bareiron_viewer.Services;

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
                string path = FilePath;
#if ANDROID
                if (!File.Exists(path) && SafUri != null)
                {
                    path     = await Task.Run(() => ReCopyFromSaf(SafUri));
                    FilePath = path;
                }
#endif
                OpenManager = new AssetsManager();
                var bundle  = OpenManager.LoadBundleFile(path);
                int count   = bundle.file.BlockAndDirInfo.DirectoryInfos.Count;
                OpenFiles   = new AssetsFileInstance?[count];
                for (int i = 0; i < count; i++)
                {
                    try { OpenFiles[i] = OpenManager.LoadAssetsFileFromBundle(bundle, i); }
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

#if ANDROID
    private static int _reCopyCounter;
    private static string ReCopyFromSaf(string safUri)
    {
        var uri      = Android.Net.Uri.Parse(safUri)!;
        var resolver = Android.App.Application.Context.ContentResolver!;
        var idx      = Interlocked.Increment(ref _reCopyCounter);
        var tmp      = Path.Combine(
            Android.App.Application.Context.CacheDir!.AbsolutePath, $"saf_{idx}");
        using var ins  = resolver.OpenInputStream(uri)!;
        using var outs = File.Create(tmp);
        ins.CopyTo(outs);
        return tmp;
    }
#endif
}

/// <summary>
/// Central registry. Holds only compact AssetDescriptor[] at rest.
/// Full bundle files are opened on demand when an asset is clicked.
/// </summary>
public sealed class BundleRegistry : IDisposable
{
    private readonly List<BundleSlot>  _slots       = new();
    private          AssetDescriptor[] _descriptors = Array.Empty<AssetDescriptor>();
    private          int               _count       = 0;

    public ReadOnlySpan<AssetDescriptor> All   => _descriptors.AsSpan(0, _count);
    public int                           Count => _count;
    public List<string> ScanErrors { get; } = new();
    public int SkippedFontCount { get; private set; } = 0;

    public void Clear()
    {
        foreach (var s in _slots) s.Release();
        _slots.Clear();
        _descriptors = Array.Empty<AssetDescriptor>();
        _count = 0;
        ScanErrors.Clear();
        SkippedFontCount = 0;
    }

    /// <summary>
    /// Scans a file: extracts type name + asset name without full GetBaseField deserialization
    /// for unnamed asset types. Writes descriptors directly into _descriptors[] — no
    /// intermediate list. Hints GC between files so dead scan managers are collected promptly.
    /// </summary>
    public void ScanFile(string filePath, string displayName, string? safUri = null)
    {
        if (IsFontFile(filePath))
        {
            SkippedFontCount++;
            return;
        }

        int slotIndex   = _slots.Count;
        int countBefore = _count;

        try
        {
            var mgr = new AssetsManager();
            bool any = false;
            try
            {
                var bundle = mgr.LoadBundleFile(filePath);
                var dirs   = bundle.file.BlockAndDirInfo.DirectoryInfos;
                for (int i = 0; i < dirs.Count; i++)
                {
                    try
                    {
                        var af = mgr.LoadAssetsFileFromBundle(bundle, i);
                        if (af?.file?.AssetInfos == null) continue;
                        ScanSubFile(af, mgr, slotIndex, i);
                        any = true;
                    }
                    catch { }
                }
            }
            catch (Exception bundleEx)
            {
                try
                {
                    var af = mgr.LoadAssetsFile(filePath);
                    if (af?.file?.AssetInfos != null)
                    {
                        ScanSubFile(af, mgr, slotIndex, 0);
                        any = true;
                    }
                }
                catch (Exception assetsEx)
                {
                    ScanErrors.Add($"{displayName}: {bundleEx.Message} | {assetsEx.Message}");
                    _count = countBefore;
                    return;
                }
            }

            // mgr goes out of scope. Hint gen-0 GC to free decompressed bundle data
            // before the next file is scanned. Prevents N dead managers piling up.
            GC.Collect(0, GCCollectionMode.Optimized, blocking: false);

            if (!any || _count == countBefore) { _count = countBefore; return; }
        }
        catch (Exception ex)
        {
            ScanErrors.Add($"{displayName}: {ex.Message}");
            _count = countBefore;
            return;
        }

        _slots.Add(new BundleSlot(displayName, filePath, safUri));
    }

    /// <summary>
    /// Writes descriptors for one sub-file into _descriptors[].
    ///
    /// RAM optimization: call GetBaseField ONCE per unique TypeId (not per asset) to learn
    /// the type name and whether it has m_Name. A bundle has ~20-50 unique types but
    /// potentially 500k assets — so this cuts GetBaseField calls from 500k down to ~50.
    /// For each asset, only call GetBaseField again if that type has m_Name.
    /// GC.Collect(0) every 2000 named assets frees field trees mid-loop.
    /// </summary>
    private void ScanSubFile(AssetsFileInstance af, AssetsManager mgr, int slotIndex, int subFile)
    {
        var infos = af.file.AssetInfos;

        // Lazily-populated cache: TypeId -> (typeName, hasName)
        // First asset of each TypeId pays one GetBaseField; all others are free.
        var typeCache = new Dictionary<int, (string TypeName, bool HasName)>();

        EnsureCapacity(_count + infos.Count);

        int gcCounter = 0;
        foreach (var info in infos)
        {
            string type    = "Unknown";
            string name    = "";
            bool   hasName = false;

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
            type    = tc.TypeName;
            hasName = tc.HasName;

            if (hasName)
            {
                try
                {
                    var bf        = mgr.GetBaseField(af, info);
                    var nameField = bf["m_Name"];
                    name = nameField.IsDummy ? "" : (nameField.AsString ?? "");
                    bf   = null!; // drop ref so GC can collect
                }
                catch { }

                if (++gcCounter % 2000 == 0)
                    GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
            }

            _descriptors[_count++] = new AssetDescriptor(
                info.PathId, slotIndex, subFile, info.ByteSize, type, name);
        }
    }

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

    private static bool IsFontFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".ttf",  StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".otf",  StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".woff", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".woff2",StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            if (!File.Exists(filePath)) return false;
            Span<byte> magic = stackalloc byte[4];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Read(magic) < 4) return false;
            // TTF/OTF: starts with 0x00010000 or "OTTO"
            if (magic[0] == 0x00 && magic[1] == 0x01 && magic[2] == 0x00 && magic[3] == 0x00) return true;
            if (magic[0] == 'O'  && magic[1] == 'T'  && magic[2] == 'T'  && magic[3] == 'O')  return true;
            // wOFF / wOF2
            if (magic[0] == 'w'  && magic[1] == 'O'  && magic[2] == 'F'  && magic[3] == 'F')  return true;
        }
        catch { }

        return false;
    }

    public void Dispose() => Clear();
}
