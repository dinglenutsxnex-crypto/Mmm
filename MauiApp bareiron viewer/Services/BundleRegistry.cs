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

        const long BigBundleThreshold = 30L * 1024 * 1024;
        bool isBigBundle = false;
        try { isBigBundle = new FileInfo(filePath).Length > BigBundleThreshold; } catch { }

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
                                ScanSubFile(af, mgr, slotIndex, i, isBigBundle);
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
                            ScanSubFile(af, mgr, slotIndex, i, isBigBundle);
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
                        ScanSubFile(af, mgr, slotIndex, 0, isBigBundle);
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

            // For bundles larger than 30 MB, null the manager before the GC hint
            // so its buffers are actually reclaimable. Small bundles skip this.
            if (isBigBundle)
                mgr = null!;

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
    /// No GetBaseField calls — type name is read from TypeTree metadata,
    /// asset name is read by seeking directly to the raw asset bytes.
    /// This means zero field trees are built or retained during scan,
    /// so RAM stays flat regardless of how many bundles are processed.
    /// </summary>
    private void ScanSubFile(AssetsFileInstance af, AssetsManager mgr, int slotIndex, int subFile, bool bigBundle = false)
    {
        var infos    = af.file.AssetInfos;
        var metadata = af.file.Metadata;

        // TypeId -> (typeName, hasName) — built from TypeTree metadata only.
        // No GetBaseField calls here, so no field trees are built or retained.
        var typeCache = new Dictionary<int, (string TypeName, bool HasName)>();

        EnsureCapacity(_count + infos.Count);

        // Types known to have m_Name as their first field.
        // This list covers the vast majority of named Unity assets.
        var namedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Texture2D", "Sprite", "AudioClip", "Mesh", "Material", "Shader",
            "AnimationClip", "AnimatorController", "GameObject", "MonoBehaviour",
            "Font", "TextAsset", "ScriptableObject", "RenderTexture", "Cubemap",
            "Texture3D", "Texture2DArray", "ComputeShader", "VideoClip",
        };

        foreach (var info in infos)
        {
            string typeName = "Unknown";
            bool   hasName  = false;

            if (!typeCache.TryGetValue(info.TypeId, out var tc))
            {
                // Read type name directly from the TypeTree — zero allocation in AssetsTools.
                try
                {
                    // AssetsTools 3.x: TypeTreeType has a TypeName property.
                    // TypeId in AssetFileInfo maps into the type tree array.
                    var types = metadata.TypeTreeTypes;
                    AssetsTools.NET.AssetsFileType? treeType = null;
                    foreach (var t in types)
                    {
                        if (t.TypeId == info.TypeId) { treeType = t; break; }
                    }

                    if (treeType != null)
                    {
                        // TypeName lives on the first TypeField node.
                        typeName = treeType.TypeTree?.Nodes?.Count > 0
                            ? (treeType.TypeTree.Nodes[0].TypeName ?? "Unknown")
                            : "Unknown";
                    }
                    else
                    {
                        // Fallback for types without a TypeTree (e.g. stripped assets).
                        // Use the class database if available.
                        typeName = mgr.ClassDatabase?.FindAssetClassByID(info.TypeId)
                            ?.Name?.GetString(mgr.ClassDatabase) ?? "Unknown";
                    }
                }
                catch { typeName = "Unknown"; }

                hasName = namedTypes.Contains(typeName);
                tc = (typeName, hasName);
                typeCache[info.TypeId] = tc;
            }
            typeName = tc.TypeName;
            hasName  = tc.HasName;

            string name = "";
            if (hasName && info.ByteSize > 8)
            {
                // Read m_Name directly from raw asset bytes.
                // Unity serializes m_Name as the FIRST field for all named types:
                //   bytes 0-3  : int32 string length (little-endian)
                //   bytes 4..N : UTF-8 chars (no null terminator)
                // This avoids building a field tree entirely.
                try
                {
                    var reader = af.file.Reader;
                    reader.Position = info.AbsoluteByteStart;
                    int nameLen = reader.ReadInt32();
                    if (nameLen > 0 && nameLen <= 512) // sanity cap
                    {
                        var nameBytes = reader.ReadBytes(nameLen);
                        name = System.Text.Encoding.UTF8.GetString(nameBytes);
                    }
                }
                catch { name = ""; }
            }

            _descriptors[_count++] = new AssetDescriptor(
                info.PathId, slotIndex, subFile, info.ByteSize, typeName, name);
        }

        typeCache.Clear();
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
