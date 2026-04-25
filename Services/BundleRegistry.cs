using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
#if ANDROID
                if (SafUri != null)
                {
                    OpenManager = new AssetsManager();
                    using var stream = AndroidDownloadService.OpenSafStream(SafUri);
                    var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    var bundle = OpenManager.LoadBundleFile(ms, FilePath, unpackIfPacked: true);
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

/// <summary>
/// Central registry. Holds only compact AssetDescriptor[] at rest.
/// Full bundle files are opened on demand when an asset is clicked.
/// </summary>
public sealed class BundleRegistry : IDisposable
{
    private readonly List<BundleSlot>  _slots       = new();
    private          AssetDescriptor[] _descriptors = Array.Empty<AssetDescriptor>();
    private          int               _count       = 0;
    private          int               _scansSinceLastGc = 0;

    public ReadOnlySpan<AssetDescriptor> All   => _descriptors.AsSpan(0, _count);
    public int                           Count => _count;
    public List<string> ScanErrors { get; } = new();

    public void Clear()
    {
        foreach (var s in _slots) s.Release();
        _slots.Clear();
        _descriptors = Array.Empty<AssetDescriptor>();
        _count = 0;
        _scansSinceLastGc = 0;
        ScanErrors.Clear();
    }

    /// <summary>
    /// Scans a file: extracts type name + asset name without full GetBaseField deserialization
    /// for unnamed asset types. Writes descriptors directly into _descriptors[] — no
    /// intermediate list. Hints GC between files so dead scan managers are collected promptly.
    /// </summary>
    public void ScanFile(string filePath, string displayName, string? safUri = null)
    {
        int slotIndex   = _slots.Count;
        int countBefore = _count;

        try
        {
            var mgr = new AssetsManager();
            bool any = false;
            try
            {
#if ANDROID
                if (safUri != null)
                {
                    MemoryStream? ms = null;
                    try
                    {
                        using var stream = AndroidDownloadService.OpenSafStream(safUri);
                        ms = new MemoryStream();
                        stream.CopyTo(ms);
                        ms.Position = 0;
                        var bundle = mgr.LoadBundleFile(ms, filePath, unpackIfPacked: true);
                        var dirs   = bundle.file.BlockAndDirInfo.DirectoryInfos;
                        for (int i = 0; i < dirs.Count; i++)
                        {
                            try
                            {
                                var af = mgr.LoadAssetsFileFromBundle(bundle, i);
                                if (af?.file?.AssetInfos == null) continue;
                                ScanSubFile(af, slotIndex, i);
                                any = true;
                            }
                            catch { }
                        }
                    }
                    finally { ms?.Dispose(); }
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
                            ScanSubFile(af, slotIndex, i);
                            any = true;
                        }
                        catch { }
                    }
                }
            }
            catch (Exception bundleEx)
            {
                try
                {
                    var af = mgr.LoadAssetsFile(filePath);
                    if (af?.file?.AssetInfos != null)
                    {
                        ScanSubFile(af, slotIndex, 0);
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

            _scansSinceLastGc++;
            if (_scansSinceLastGc >= 50)
            {
                _scansSinceLastGc = 0;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            else
            {
                GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
            }

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
    private void ScanSubFile(AssetsFileInstance af, int slotIndex, int subFile)
    {
        var infos = af.file.AssetInfos;
        EnsureCapacity(_count + infos.Count);

        var typeTree = af.file.Metadata?.TypeTreeTypes;

        var typeNameCache = new Dictionary<int, string>();
        var nameFieldOffsetCache = new Dictionary<int, int>();

        foreach (var info in infos)
        {
            string typeName = "Unknown";
            string name     = "";

            if (!typeNameCache.TryGetValue(info.TypeId, out var tn))
            {
                tn = "Unknown";
                var tt = typeTree?.Find(t => t.TypeId == info.TypeId);
                if (tt != null)
                    tn = tt.Nodes?.Count > 0 ? (tt.Nodes[0].GetTypeString(tt.StringBuffer) ?? "Unknown") : "Unknown";
                typeNameCache[info.TypeId] = tn;
            }
            typeName = tn;

            if (!nameFieldOffsetCache.TryGetValue(info.TypeId, out int nameOffset))
            {
                nameOffset = -1;
                var tt = typeTree?.Find(t => t.TypeId == info.TypeId);
                if (tt?.Nodes != null)
                {
                    int byteOff = 0;
                    for (int ni = 1; ni < tt.Nodes.Count; ni++)
                    {
                        var node = tt.Nodes[ni];
                        string nname = node.GetNameString(tt.StringBuffer) ?? "";
                        string ntype = node.GetTypeString(tt.StringBuffer) ?? "";
                        if (nname == "m_Name" && ntype == "string")
                        {
                            nameOffset = byteOff;
                            break;
                        }
                        if (node.Level == 1)
                        {
                            int sz = node.ByteSize;
                            if (sz > 0) byteOff += sz;
                            else { byteOff = -1; break; }
                        }
                    }
                }
                nameFieldOffsetCache[info.TypeId] = nameOffset;
            }

            if (nameOffset >= 0)
            {
                try
                {
                    var reader = af.file.Reader;
                    reader.Position = info.AbsoluteByteStart + nameOffset;
                    int strLen = reader.ReadInt32();
                    if (strLen > 0 && strLen < 512)
                        name = new string(reader.ReadChars(strLen));
                }
                catch { }
            }

            _descriptors[_count++] = new AssetDescriptor(
                info.PathId, slotIndex, subFile, info.ByteSize, typeName, name);
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

    public void ReleaseSlot(int slot)
    {
        if (slot >= 0 && slot < _slots.Count)
            _slots[slot].Release();
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _descriptors.Length) return;
        int newSize = Math.Max(needed, Math.Max(1024, _descriptors.Length * 2));
        Array.Resize(ref _descriptors, newSize);
    }

    public void Dispose() => Clear();
}
