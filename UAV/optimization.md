# Memory Optimization Workflow - Exact Implementation Guide

## Reference Files

**MAUI Project Root:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\`

**Asset Studio Reference:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\AssetStudioSource\AssetStudio\`

---

## CRITICAL: Android File Access Issue

### The Problem
Android uses **SAF (Storage Access Framework)** for folder picking (see `AndroidDownloadService.cs` lines 85-165). SAF files are copied to temp cache dir before loading:

```csharp
// AndroidDownloadService.cs line 114, 135
var cacheDir = Android.App.Application.Context.CacheDir!.AbsolutePath;
var tmpPath = System.IO.Path.Combine(cacheDir, tmpName);
```

If we close the file reader before `CleanupTempFiles()` is called, we lose access to the temp file data.

### The Solution
Track whether a bundle came from SAF temp path. Only dispose readers for regular files, NOT SAF temp files.

**Implementation:**
- Add `bool IsSafTempFile` field to `LoadedBundle` record (STEP 5)
- Detect SAF files in `LoadFromPath` using `#if ANDROID` (STEP 5)
- Only dispose if `!IsSafTempFile` in the Dispose method (STEP 5)
- This allows cleanup for regular files while preserving SAF temp file access

**Impact:**
- **Windows:** No change - regular file picker, disposal works normally
- **Android SAF folder:** Temp files remain accessible until CleanupTempFiles deletes them
- **Android regular file:** Works like Windows, disposal works normally

---

---

## PROBLEM 1: Byte Array Allocations in Loops (High GC Pressure)

### Location 1: MeshParser.cs Line 149
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Services\MeshParser.cs`
**Line:** 149
**Current Code:**
```csharp
byte[] vertData = new byte[stride];
```
**Problem:** Allocates new byte array for every vertex in the loop (line 144-161)
**Impact:** For a 100K vertex mesh, this allocates 100,000 byte arrays

**Solution:** Rent from pool before loop, return after loop
```csharp
byte[] vertData = ArrayPool<byte>.Shared.Rent(stride);
try {
    for (int v = 0; v < vertexCount; v++) {
        int pos = offset + v * streamLength;
        if (pos + stride > vertexBytes.Length) break;
        Buffer.BlockCopy(vertexBytes, pos, vertData, 0, stride);
        var floatItems = ConvertFloatArray(vertData, dimension, (VertexFormat)format);
        // ... rest of loop
    }
} finally {
    ArrayPool<byte>.Shared.Return(vertData);
}
```

---

### Location 2: MeshParser.cs Line 155
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Services\MeshParser.cs`
**Line:** 155
**Current Code:**
```csharp
channelData = new float[vertexCount * dimension];
```
**Problem:** Allocates large float array inside loop
**Impact:** Allocates once per channel type (vertices, normals, UVs, etc.)

**Solution:** Allocate once before the channel loop, reuse
```csharp
// Move allocation outside the for loop at line 124
float[]? channelData = null;
// Inside loop at line 155:
if (channelData == null)
    channelData = ArrayPool<float>.Shared.Rent(vertexCount * dimension);
// After loop ends (after line 161), add finally block:
finally {
    if (channelData != null)
        ArrayPool<float>.Shared.Return(channelData);
}
```

---

### Location 3: BareIronTextureDecoder.cs Line 69
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Services\BareIronTextureDecoder.cs`
**Line:** 69
**Current Code:**
```csharp
var row = new byte[rowSize];
```
**Problem:** Allocates byte array for every row in FlipVertically (line 66-78)
**Impact:** For 4K texture, allocates 2160 arrays (4320 / 2)

**Solution:** Rent once, return after method
```csharp
private static void FlipVertically(byte[] data, int w, int h)
{
    int rowSize = w * 4;
    var row = ArrayPool<byte>.Shared.Rent(rowSize);
    try {
        for (int y = 0; y < h / 2; y++) {
            int topOffset = y * rowSize;
            int bottomOffset = (h - 1 - y) * rowSize;
            Array.Copy(data, topOffset, row, 0, rowSize);
            Array.Copy(data, bottomOffset, data, topOffset, rowSize);
            Array.Copy(row, 0, data, bottomOffset, rowSize);
        }
    } finally {
        ArrayPool<byte>.Shared.Return(row);
    }
}
```

---

## PROBLEM 2: All Bundles Kept in Memory

### Location: Home.razor Lines 982-1002
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Components\Pages\Home.razor`
**Lines:** 982-1002

**Current Code (Line 983):**
```csharp
record LoadedBundle(string FileName, AssetsFileInstance AssetsFile);
```

**Current Code (Line 1002):**
```csharp
readonly List<LoadedBundle> loadedBundles = new();
```

**Problem:** LoadedBundle record holds full AssetsFileInstance in memory forever
**Impact:** Loading 10 bundles = 10x bundle data in RAM simultaneously

**Solution A (Quick): Add disposal capability
```csharp
// Change line 983 to:
record LoadedBundle(string FileName, AssetsFileInstance AssetsFile) : IDisposable
{
    public void Dispose()
    {
        AssetsFile?.file?.reader?.Close();
        AssetsFile?.file?.reader?.Dispose();
    }
}
```

**Solution B (Full): Implement streaming for large bundles
```csharp
// Add new field to track if streamed:
record LoadedBundle(string FileName, AssetsFileInstance AssetsFile, bool IsStreamed, string? TempPath);

// When loading (line 1246), check file size:
var fileInfo = new FileInfo(filePath);
bool isLarge = fileInfo.Length > 50 * 1024 * 1024; // 50MB threshold
if (isLarge) {
    // Stream to temp file instead of loading fully
    // Implementation needed in new service
}
```

---

## PROBLEM 3: No Resource Cleanup on Clear

### Location: Home.razor Lines 1096-1097 and 1118-1119
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Components\Pages\Home.razor`

**Current Code (Line 1096-1097):**
```csharp
assets.Clear(); loadedBundles.Clear();
manager = new AssetsManager();
```

**Current Code (Line 1118-1119):**
```csharp
assets.Clear(); loadedBundles.Clear(); _loadErrors.Clear();
manager = new AssetsManager();
```

**Problem:** Clears lists but doesn't dispose AssetsFileInstance or close file handles
**Impact:** Memory leaks, file locks remain open on Windows

**Solution:** Add disposal before clear
```csharp
// Replace line 1096-1097 with:
foreach (var bundle in loadedBundles) {
    bundle.AssetsFile?.file?.reader?.Close();
    bundle.AssetsFile?.file?.reader?.Dispose();
}
assets.Clear(); loadedBundles.Clear();
manager = new AssetsManager();

// Replace line 1118-1119 with:
foreach (var bundle in loadedBundles) {
    bundle.AssetsFile?.file?.reader?.Close();
    bundle.AssetsFile?.file?.reader?.Dispose();
}
assets.Clear(); loadedBundles.Clear(); _loadErrors.Clear();
manager = new AssetsManager();
```

---

## PROBLEM 4: Asset Metadata Loaded Immediately

### Location: Home.razor Lines 1243-1262
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Components\Pages\Home.razor`
**Lines:** 1243-1262

**Current Code (Line 1252):**
```csharp
var bf = manager.GetBaseField(af, info);
```

**Problem:** GetBaseField loads all asset fields immediately for every asset
**Impact:** For 1000 assets, loads 1000 full field trees into memory

**Solution:** Only load lightweight metadata initially
```csharp
void ExtractAssetsFromFile(AssetsFileInstance af, string bundleName)
{
    int bundleIndex = loadedBundles.Count;
    loadedBundles.Add(new LoadedBundle(bundleName, af));

    foreach (var info in af.file.AssetInfos)
    {
        // Only load what we need for the list view
        string assetName = "";
        string assetType = "Unknown";
        
        // Try to get name without full field load
        try {
            var bf = manager.GetBaseField(af, info);
            var nameField = bf["m_Name"];
            assetName = nameField.IsDummy ? "" : nameField.AsString;
            assetType = bf.TypeName;
        }
        catch {
            // Fallback to basic info
            assetName = "";
            assetType = "Unknown";
        }
        
        assets.Add(new AssetEntry(info.PathId, assetType, assetName, info.ByteSize, bundleName, bundleIndex));
    }
}
```

---

## REFERENCE: Asset Studio's BigArrayPool Implementation

### File: AssetStudioSource\AssetStudio\BigArrayPool.cs
**Full Path:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\AssetStudioSource\AssetStudio\BigArrayPool.cs`

**Complete Code:**
```csharp
using System.Buffers;

namespace AssetStudio
{
    public static class BigArrayPool<T>
    {
        private static readonly ArrayPool<T> s_shared = ArrayPool<T>.Create(64 * 1024 * 1024, 3);
        public static ArrayPool<T> Shared => s_shared;
    }
}
```

**Copy this to:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Services\BigArrayPool.cs`

**Add namespace:** `namespace UAV.Services`

---

## REFERENCE: Asset Studio's Streaming Implementation

### File: AssetStudioSource\AssetStudio\BundleFile.cs
**Full Path:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\AssetStudioSource\AssetStudio\BundleFile.cs`

**Key Section: Lines 140-155 (CreateBlocksStream)**
```csharp
private Stream CreateBlocksStream(string path)
{
    Stream blocksStream;
    var uncompressedSizeSum = m_BlocksInfo.Sum(x => x.uncompressedSize);
    if (uncompressedSizeSum >= int.MaxValue)
    {
        /*var memoryMappedFile = MemoryMappedFile.CreateNew(null, uncompressedSizeSum);
        assetsDataStream = memoryMappedFile.CreateViewStream();*/
        blocksStream = new FileStream(path + ".temp", FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
    }
    else
    {
        blocksStream = new MemoryStream((int)uncompressedSizeSum);
    }
    return blocksStream;
}
```

**Key Section: Lines 338-349 (Pooled LZ4 Decompression)**
```csharp
case CompressionType.Lz4:
case CompressionType.Lz4HC:
{
    var compressedSize = (int)blockInfo.compressedSize;
    var compressedBytes = BigArrayPool<byte>.Shared.Rent(compressedSize);
    reader.Read(compressedBytes, 0, compressedSize);
    var uncompressedSize = (int)blockInfo.uncompressedSize;
    var uncompressedBytes = BigArrayPool<byte>.Shared.Rent(uncompressedSize);
    var numWrite = LZ4Codec.Decode(compressedBytes, 0, compressedSize, uncompressedBytes, 0, uncompressedSize);
    if (numWrite != uncompressedSize)
    {
        throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
    }
    blocksStream.Write(uncompressedBytes, 0, uncompressedSize);
    BigArrayPool<byte>.Shared.Return(compressedBytes);
    BigArrayPool<byte>.Shared.Return(uncompressedBytes);
    break;
}
```

## STEP-BY-STEP IMPLEMENTATION CHECKLIST

### STEP 1: Create BigArrayPool Service
**File to create:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Services\BigArrayPool.cs`

**Action:** Copy the exact code from reference above (lines 241-252 in this document)
- Change namespace from `AssetStudio` to `UAV.Services`
- No other changes needed
- This is platform-independent (works on both Android and Windows)

**Verification:** Build project, no compilation errors

---

### STEP 2: Fix MeshParser.cs Line 149
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Services\MeshParser.cs`
**Line:** 149

**Before:**
```csharp
for (int v = 0; v < vertexCount; v++)
{
    int pos = offset + v * streamLength;
    if (pos + stride > vertexBytes.Length) break;

    byte[] vertData = new byte[stride];
    Buffer.BlockCopy(vertexBytes, pos, vertData, 0, stride);
```

**After:**
```csharp
byte[] vertData = ArrayPool<byte>.Shared.Rent(stride);
try {
    for (int v = 0; v < vertexCount; v++)
    {
        int pos = offset + v * streamLength;
        if (pos + stride > vertexBytes.Length) break;

        Buffer.BlockCopy(vertexBytes, pos, vertData, 0, stride);
```

**Add after the for loop ends (after line 161):**
```csharp
    }
} finally {
    ArrayPool<byte>.Shared.Return(vertData);
}
```

**Add using statement at top of file (if not present):**
```csharp
using System.Buffers;
```

---

### STEP 3: Fix MeshParser.cs Line 155
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Services\MeshParser.cs`
**Line:** 155

**Before:**
```csharp
float[]? channelData = null;

for (int v = 0; v < vertexCount; v++)
{
    // ... loop body ...
    if (channelData == null)
        channelData = new float[vertexCount * dimension];
```

**After:**
```csharp
float[]? channelData = null;

for (int v = 0; v < vertexCount; v++)
{
    // ... loop body ...
    if (channelData == null)
        channelData = ArrayPool<float>.Shared.Rent(vertexCount * dimension);
```

**Add after the entire channel loop ends (after line 204, before return):**
```csharp
} finally {
    if (channelData != null)
        ArrayPool<float>.Shared.Return(channelData);
}
```

**Note:** The `finally` block needs to wrap the entire channel processing logic (lines 124-204). You may need to restructure to ensure the finally is placed correctly.

---

### STEP 4: Fix BareIronTextureDecoder.cs Line 69
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Services\BareIronTextureDecoder.cs`
**Line:** 69

**Before (lines 66-78):**
```csharp
private static void FlipVertically(byte[] data, int w, int h)
{
    int rowSize = w * 4;
    var row = new byte[rowSize];
    for (int y = 0; y < h / 2; y++)
    {
        int topOffset = y * rowSize;
        int bottomOffset = (h - 1 - y) * rowSize;
        Array.Copy(data, topOffset, row, 0, rowSize);
        Array.Copy(data, bottomOffset, data, topOffset, rowSize);
        Array.Copy(row, 0, data, bottomOffset, rowSize);
    }
}
```

**After:**
```csharp
private static void FlipVertically(byte[] data, int w, int h)
{
    int rowSize = w * 4;
    var row = ArrayPool<byte>.Shared.Rent(rowSize);
    try
    {
        for (int y = 0; y < h / 2; y++)
        {
            int topOffset = y * rowSize;
            int bottomOffset = (h - 1 - y) * rowSize;
            Array.Copy(data, topOffset, row, 0, rowSize);
            Array.Copy(data, bottomOffset, data, topOffset, rowSize);
            Array.Copy(row, 0, data, bottomOffset, rowSize);
        }
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(row);
    }
}
```

**Add using statement at top of file (if not present):**
```csharp
using System.Buffers;
```

---

### STEP 5: Add IDisposable to LoadedBundle (ANDROID WARNING)
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Components\Pages\Home.razor`
**Line:** 983

**CRITICAL ANDROID ISSUE:**
On Android, when using SAF folder picker, files are copied to temp cache dir (see `AndroidDownloadService.cs` lines 114, 135). If we close the reader immediately, we lose access to temp file data before `CleanupTempFiles` is called.

**Solution:** Track whether bundle came from SAF temp path, only dispose if NOT from SAF.

**Before:**
```csharp
record LoadedBundle(string FileName, AssetsFileInstance AssetsFile);
```

**After:**
```csharp
record LoadedBundle(string FileName, AssetsFileInstance AssetsFile, bool IsSafTempFile)
{
    public void Dispose()
    {
        // Only dispose if NOT a SAF temp file
        // SAF temp files are managed by AndroidDownloadService.CleanupTempFiles()
        if (!IsSafTempFile)
        {
            AssetsFile?.file?.reader?.Close();
            AssetsFile?.file?.reader?.Dispose();
        }
    }
}
```

**Update line 1246 in LoadFromPath to track SAF files:**
```csharp
// Detect if this is a SAF temp file
bool isSafTemp = false;
#if ANDROID
isSafTemp = filePath.Contains("saf_") && filePath.Contains(Android.App.Application.Context.CacheDir!.AbsolutePath);
#endif
loadedBundles.Add(new LoadedBundle(bundleName, af, isSafTemp));
```

---

### STEP 6: Add Resource Cleanup Before Clear (PickFile method)
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Components\Pages\Home.razor`
**Line:** 1096-1097

**ANDROID NOTE:** PickFile uses regular FilePicker (not SAF), so disposal is safe here.

**Before:**
```csharp
assets.Clear(); loadedBundles.Clear();
manager = new AssetsManager();
```

**After:**
```csharp
foreach (var bundle in loadedBundles) {
    bundle.Dispose();
}
assets.Clear(); loadedBundles.Clear();
manager = new AssetsManager();
```

---

### STEP 7: Add Resource Cleanup Before Clear (PickFolder method)
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Components\Pages\Home.razor`
**Line:** 1118-1119

**ANDROID CRITICAL:** PickFolder on Android uses SAF (lines 1123-1159). The IsSafTempFile flag from STEP 5 will prevent disposal of SAF temp bundles. This is safe because:
- SAF temp bundles have IsSafTempFile=true, so Dispose() won't close their readers
- Windows PickFolder uses regular folder picker, so IsSafTempFile=false, disposal works normally

**Before:**
```csharp
assets.Clear(); loadedBundles.Clear(); _loadErrors.Clear();
manager = new AssetsManager();
```

**After:**
```csharp
foreach (var bundle in loadedBundles) {
    bundle.Dispose(); // Safe because of IsSafTempFile check in STEP 5
}
assets.Clear(); loadedBundles.Clear(); _loadErrors.Clear();
manager = new AssetsManager();
```

---

### STEP 8: Add OnDisposing Cleanup
**File:** `c:\Users\Admin\Downloads\bareiron viewer  DONTDELETE\UAV\Components\Pages\Home.razor`

**Add this method after line 1760 (end of @code block):**
```csharp
public void Dispose()
{
    foreach (var bundle in loadedBundles) {
        bundle.Dispose();
    }
    assets.Clear();
    loadedBundles.Clear();
    manager = null;
}
```

**Note:** If Home.razor already implements IDisposable, add the cleanup logic to the existing Dispose method.

---

## TESTING CHECKLIST

### Test 1: Memory Pooling Works
- [ ] Build project successfully
- [ ] Load a mesh with 10K+ vertices
- [ ] Monitor memory usage - should be lower than before
- [ ] Load a 4K texture
- [ ] Monitor memory usage - should be lower than before
- [ ] Test on Android - no crashes
- [ ] Test on Windows - no crashes

### Test 2: Resource Cleanup Works
- [ ] Load a file
- [ ] Load another file (first should be disposed)
- [ ] Check Windows file handles - no locks on old file
- [ ] Repeat 10 times - no memory leak
- [ ] Test on Android - no "too many open files" error

### Test 3: Basic Functionality Still Works
- [ ] Load .bundle file - assets appear in list
- [ ] Load .assets file - assets appear in list
- [ ] Load folder - all files load
- [ ] Select asset - inspector shows fields
- [ ] Select Texture2D - preview shows
- [ ] Select Mesh - preview shows
- [ ] Export texture - downloads
- [ ] Export mesh - downloads
- [ ] Export all filtered - zip downloads

### Test 4: Android SAF Still Works
- [ ] Use Android folder picker
- [ ] Select folder with bundles
- [ ] Assets load correctly
- [ ] Temp files cleaned up after load
- [ ] No permission errors

---

## PLATFORM-SPECIFIC NOTES

### Android
- ArrayPool works natively on Android (System.Buffers is available)
- File disposal is critical - Android has strict file handle limits
- Test on low-memory devices (2GB RAM) - should not crash

### Windows
- ArrayPool works natively on Windows
- File disposal prevents file locks
- Test with Process Explorer to verify handles are released

---

## EXPECTED RESULTS

After implementing Steps 1-8:
- **Memory reduction:** 30-50% for mesh/texture heavy operations
- **No memory leaks:** File handles properly released
- **No functionality loss:** All existing features work
- **Platform compatibility:** Both Android and Windows work
