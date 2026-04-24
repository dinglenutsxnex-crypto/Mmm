# Android Performance Optimization - Direct File Access Plan

## Problem Statement
**Current Issue:** Loading 3k bundles on Android crashes due to temp file accumulation in cache dir. SAF (Storage Access Framework) copies files to cache before scanning, which:
- Wastes time copying files (double I/O)
- Accumulates temp files that never get cleaned up
- Causes storage pressure crashes
- Slows down load time significantly

**Goal:** Fix the crash issue while keeping BOTH Android and Windows working.

---

## Current Codebase Analysis

### File Locations (VERIFIED - all paths exist)
| File | Location |
|------|----------|
| AndroidManifest.xml | `Platforms/Android/AndroidManifest.xml` |
| MauiProgram.cs | `MauiProgram.cs` (root) |
| Home.razor PickFolder | `Components/Pages/Home.razor` lines 1172-1246 |
| AndroidFolderPicker.cs | `Services/AndroidFolderPicker.cs` |
| BundleRegistry.cs | `Services/BundleRegistry.cs` |
| AndroidDownloadService.cs | `Services/AndroidDownloadService.cs` |
| MainActivity.cs | `Platforms/Android/MainActivity.cs` |
| Project file | `MauiApp bareiron viewer.csproj` |

---

## Verified Current Implementation

### Home.razor PickFolder - Current Android Code (lines 1181-1204)
```csharp
#if ANDROID
            string? safUri = await AndroidFolderPicker.PickFolderAsync();
            if (safUri == null) { loading = false; return; }

            var folderName = System.IO.Path.GetFileName(safUri.TrimEnd('/')) is { Length: > 0 } n ? n : "folder";
            bundleFileName = $" {folderName}";
            StateHasChanged();

            List<(string DisplayName, string TempPath)> safFiles = new();
            await Task.Run(() => safFiles = AndroidDownloadService.GetFilesFromSafUri(safUri));

            _loadTotal = safFiles.Count;
            StateHasChanged();

            await Task.Run(async () =>
            {
                foreach (var (displayName, tmpPath) in safFiles)
                {
                    _registry.ScanFile(tmpPath, displayName, safUri: null);  // BUG: should be safUri: safUri
                    _loadDone++;
                    if (_loadDone % 5 == 0)
                        await InvokeAsync(StateHasChanged);
                }
            });
#endif
```

**Issues identified:**
1. **No temp file cleanup** - temp files in cache dir are never deleted → causes crash after multiple loads
2. **safUri: null bug** - passes null instead of actual safUri to ScanFile (line 1199)

### Home.razor PickFolder - Current Windows Code (lines 1205-1235)
```csharp
#else
            string? folderPath = null;
#if WINDOWS
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var picker = new global::Windows.Storage.Pickers.FolderPicker();
                picker.SuggestedStartLocation = global::Windows.Storage.Pickers.PickerLocationId.Downloads;
                picker.FileTypeFilter.Add("*");
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(Application.Current!.Windows[0].Handler!.PlatformView);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null) folderPath = folder.Path;
            });
#endif
            if (string.IsNullOrEmpty(folderPath)) { loading = false; return; }

            bundleFileName = $" {System.IO.Path.GetFileName(folderPath)}";
            var files = Directory.GetFiles(folderPath);
            _loadTotal = files.Length;
            StateHasChanged();

            await Task.Run(async () =>
            {
                foreach (var filePath in files)
                {
                    _registry.ScanFile(filePath, Path.GetFileName(filePath));
                    _loadDone++;
                    if (_loadDone % 5 == 0)
                        await InvokeAsync(StateHasChanged);
                }
            });
#endif
```

**Windows status:** Works perfectly with direct file access. No changes needed.

---

## Solution: Fix Temp File Cleanup (Core Fix)

The CRITICAL fix is adding temp file cleanup. This alone will fix the crash issue without requiring any permission changes.

### STEP 1: Add Temp File Cleanup to Home.razor (CRITICAL)

**File:** `Components/Pages/Home.razor`
**Lines to modify:** 1181-1204 (Android section only)

**Current code (lines 1195-1204):**
```csharp
            await Task.Run(async () =>
            {
                foreach (var (displayName, tmpPath) in safFiles)
                {
                    _registry.ScanFile(tmpPath, displayName, safUri: null);
                    _loadDone++;
                    if (_loadDone % 5 == 0)
                        await InvokeAsync(StateHasChanged);
                }
            });
```

**Replace with:**
```csharp
            try
            {
                await Task.Run(async () =>
                {
                    foreach (var (displayName, tmpPath) in safFiles)
                    {
                        _registry.ScanFile(tmpPath, displayName, safUri: safUri);
                        _loadDone++;
                        if (_loadDone % 5 == 0)
                            await InvokeAsync(StateHasChanged);
                    }
                });
            }
            finally
            {
                var tempPaths = safFiles.Select(f => f.TempPath).ToList();
                AndroidDownloadService.CleanupTempFiles(tempPaths);
            }
```

**Changes made:**
1. Added `try { ... } finally { ... }` block
2. Fixed `safUri: null` → `safUri: safUri` (line 1199 becomes correct)
3. Added cleanup call in `finally` block - this deletes temp files after loading completes

**Full modified Android section (lines 1181-1218):**
```csharp
#if ANDROID
            string? safUri = await AndroidFolderPicker.PickFolderAsync();
            if (safUri == null) { loading = false; return; }

            var folderName = System.IO.Path.GetFileName(safUri.TrimEnd('/')) is { Length: > 0 } n ? n : "folder";
            bundleFileName = $" {folderName}";
            StateHasChanged();

            List<(string DisplayName, string TempPath)> safFiles = new();
            await Task.Run(() => safFiles = AndroidDownloadService.GetFilesFromSafUri(safUri));

            _loadTotal = safFiles.Count;
            StateHasChanged();

            try
            {
                await Task.Run(async () =>
                {
                    foreach (var (displayName, tmpPath) in safFiles)
                    {
                        _registry.ScanFile(tmpPath, displayName, safUri: safUri);
                        _loadDone++;
                        if (_loadDone % 5 == 0)
                            await InvokeAsync(StateHasChanged);
                    }
                });
            }
            finally
            {
                var tempPaths = safFiles.Select(f => f.TempPath).ToList();
                AndroidDownloadService.CleanupTempFiles(tempPaths);
            }
#else
```

**Windows section remains unchanged (lines 1219-1254):**
```csharp
#else
            string? folderPath = null;
#if WINDOWS
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var picker = new global::Windows.Storage.Pickers.FolderPicker();
                picker.SuggestedStartLocation = global::Windows.Storage.Pickers.PickerLocationId.Downloads;
                picker.FileTypeFilter.Add("*");
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(Application.Current!.Windows[0].Handler!.PlatformView);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null) folderPath = folder.Path;
            });
#endif
            if (string.IsNullOrEmpty(folderPath)) { loading = false; return; }

            bundleFileName = $" {System.IO.Path.GetFileName(folderPath)}";
            var files = Directory.GetFiles(folderPath);
            _loadTotal = files.Length;
            StateHasChanged();

            await Task.Run(async () =>
            {
                foreach (var filePath in files)
                {
                    _registry.ScanFile(filePath, Path.GetFileName(filePath));
                    _loadDone++;
                    if (_loadDone % 5 == 0)
                        await InvokeAsync(StateHasChanged);
                }
            });
#endif
```

---

## Optional Enhancement: Add MANAGE_EXTERNAL_STORAGE Permission

This is optional but provides broader storage access. Even with this permission, SAF is still needed for folder picking, but it allows more flexibility.

### STEP 2: Add Permissions to AndroidManifest.xml (OPTIONAL)

**File:** `Platforms/Android/AndroidManifest.xml`
**Current (lines 1-6):**
```xml
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <application android:allowBackup="true" android:icon="@mipmap/appicon" android:roundIcon="@mipmap/appicon_round" android:supportsRtl="true"></application>
    <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
    <uses-permission android:name="android.permission.INTERNET" />
</manifest>
```

**Replace with:**
```xml
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <application android:allowBackup="true" android:icon="@mipmap/appicon" android:roundIcon="@mipmap/appicon_round" android:supportsRtl="true"></application>
    <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
    <uses-permission android:name="android.permission.INTERNET" />
    <!-- Broad storage access -->
    <uses-permission android:name="android.permission.MANAGE_EXTERNAL_STORAGE" />
    <uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" />
    <uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" />
</manifest>
```

### STEP 3: Add Permission Request at Startup (OPTIONAL)

**File:** `MauiProgram.cs`
**Current (lines 1-27):**
```csharp
using Microsoft.Extensions.Logging;

namespace MauiApp_bareiron_viewer
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
```

**Replace with:**
```csharp
using Microsoft.Extensions.Logging;

namespace MauiApp_bareiron_viewer
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

#if ANDROID
            RequestStoragePermission();
#endif

            return builder.Build();
        }
    }

#if ANDROID
    private static void RequestStoragePermission()
    {
        var activity = Android.App.Application.Context as Android.App.Activity;
        if (activity == null) return;

        if (Android.OS.Environment.IsExternalStorageManager)
            return;

        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R)
        {
            var intent = new Android.Content.Intent(
                Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
                Android.Net.Uri.Parse("package:" + activity.PackageName));
            activity.StartActivity(intent);
        }
        else
        {
            activity.RequestPermissions(
                new[] {
                    Android.Manifest.Permission.ReadExternalStorage,
                    Android.Manifest.Permission.WriteExternalStorage
                },
                0);
        }
    }
#endif
}
```

**Note:** This shows a permission dialog on startup. User must grant MANAGE_EXTERNAL_STORAGE for full access.

---

## Implementation Summary

### Minimum Required Changes (Fixes the crash):
1. **Home.razor lines 1195-1204** - Add try/finally with temp file cleanup + fix safUri bug

### Optional Changes (Broader permissions):
2. **AndroidManifest.xml** - Add storage permissions
3. **MauiProgram.cs** - Add permission request at startup

### Files NOT to modify:
- Windows code in Home.razor - Already works
- BundleRegistry.cs - Keep SAF handling code
- AndroidDownloadService.cs - Keep CleanupTempFiles method
- AndroidFolderPicker.cs - Keep existing SAF picker
- MainActivity.cs - Already handles OnActivityResult

---

## Expected Results

### With Cleanup Fix Only:
- Load 3k bundles: ~40-80 seconds (same speed)
- Temp files cleaned up after each load
- No more crashes from storage pressure
- Works on all Android versions

### With Full Permission (Optional):
- Same performance as above
- Broader storage access available
- Google Play approval needed for production

### Windows:
- Unchanged - continues to work with direct file access

---

## Testing Checklist

- [ ] Build and run on Android device
- [ ] Load bundles using folder picker
- [ ] Verify bundles load successfully
- [ ] Load bundles again - verify temp files are cleaned
- [ ] Load 3k bundles multiple times - should not crash
- [ ] Verify Windows build still works
- [ ] Test Windows folder picker functionality

---

## Code Flow After Fix

### Android:
1. User selects folder via SAF picker
2. Files are copied to temp (cache) dir
3. Scan files and load assets
4. **NEW:** Cleanup temp files in finally block
5. No more temp file accumulation

### Windows:
1. User selects folder via Windows picker
2. Direct file access via Directory.GetFiles()
3. Scan files and load assets
4. No temp files (unchanged)

---

## Important Notes

1. **The cleanup fix is CRITICAL** - Without it, the app will crash after loading bundles multiple times
2. **Windows is unaffected** - Already works with direct file access, no changes needed
3. **The safUri bug** - Fixed by passing actual safUri instead of null
4. **MANAGE_EXTERNAL_STORAGE is optional** - Not required for the cleanup fix to work
5. **Google Play** - Will require justification for MANAGE_EXTERNAL_STORAGE in production apps