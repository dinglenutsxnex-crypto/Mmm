namespace MauiApp_bareiron_viewer.Services;

/// <summary>
/// Saves files directly to the Android Downloads folder.
/// Called from C# — no JS interop, no callbacks, no DotNetObjectReference.
/// Shows a toast after a successful save so the user knows it worked.
/// </summary>
public static class AndroidDownloadService
{
#if ANDROID
    static int _tmpCounter = 0;

    public static async Task SaveFileAsync(string filename, byte[] bytes, string mimeType)
    {
        try
        {
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Q)
            {
                // Android 10+ (API 29+): MediaStore — no storage permission required
                var cv = new Android.Content.ContentValues();
                cv.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, filename);
                cv.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, mimeType);
                cv.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath,
                    Android.OS.Environment.DirectoryDownloads);

                var resolver = Android.App.Application.Context.ContentResolver!;
                var uri = resolver.Insert(
                    Android.Provider.MediaStore.Downloads.ExternalContentUri!, cv)
                    ?? throw new Exception("MediaStore insert returned null URI");

                await using var stream = resolver.OpenOutputStream(uri)
                    ?? throw new Exception("Could not open output stream");

                await stream.WriteAsync(bytes, 0, bytes.Length);
            }
            else
            {
                // Android 9 and below: direct write
                var status = await Permissions.RequestAsync<Permissions.StorageWrite>();
                if (status != PermissionStatus.Granted)
                    throw new Exception("Storage permission denied");

                var dir = Android.OS.Environment
                    .GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!
                    .AbsolutePath;

                await File.WriteAllBytesAsync(Path.Combine(dir, filename), bytes);
            }

            // ✅ Notify the user — previously silent, which caused repeat downloads
            ShowToast($"Saved to Downloads: {filename}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidDownloadService] Save failed: {ex.Message}");
            throw;
        }
    }

    static void ShowToast(string message)
    {
        try
        {
            var ctx = Android.App.Application.Context;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Android.Widget.Toast
                    .MakeText(ctx, message, Android.Widget.ToastLength.Short)!
                    .Show();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidDownloadService] Toast failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerates all files inside a SAF tree URI obtained from FolderPicker.
    /// Because Android SAF paths aren't real filesystem paths, Directory.GetFiles()
    /// will throw on them. This method uses the ContentResolver instead, copies each
    /// file to the app's cache dir, and returns (displayName, tempPath) pairs.
    /// Caller is responsible for calling CleanupTempFiles() when done loading.
    /// </summary>
    public static List<(string DisplayName, string TempPath)> GetFilesFromSafUri(string safUriString)
    {
        var results = new List<(string, string)>();

        var treeUri = Android.Net.Uri.Parse(safUriString)
            ?? throw new Exception("Invalid SAF URI");

        var childrenUri = Android.Provider.DocumentsContract.BuildChildDocumentsUriUsingTree(
            treeUri,
            Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri)!);

        var resolver = Android.App.Application.Context.ContentResolver!;

        using var cursor = resolver.Query(
            childrenUri!,
            new[]
            {
                Android.Provider.DocumentsContract.Document.ColumnDocumentId,
                Android.Provider.DocumentsContract.Document.ColumnDisplayName,
                Android.Provider.DocumentsContract.Document.ColumnMimeType
            },
            (string?)null, (string[]?)null, (string?)null);

        if (cursor == null) return results;

        int idCol   = cursor.GetColumnIndex(Android.Provider.DocumentsContract.Document.ColumnDocumentId);
        int nameCol = cursor.GetColumnIndex(Android.Provider.DocumentsContract.Document.ColumnDisplayName);
        int mimeCol = cursor.GetColumnIndex(Android.Provider.DocumentsContract.Document.ColumnMimeType);

        var cacheDir = Android.App.Application.Context.CacheDir!.AbsolutePath;

        while (cursor.MoveToNext())
        {
            var docId       = cursor.GetString(idCol)   ?? "";
            var displayName = cursor.GetString(nameCol) ?? docId;
            var mime        = cursor.GetString(mimeCol) ?? "";

            // Skip subdirectories
            if (mime == Android.Provider.DocumentsContract.Document.MimeTypeDir)
                continue;

            var fileUri = Android.Provider.DocumentsContract
                .BuildDocumentUriUsingTree(treeUri, docId);
            if (fileUri == null) continue;

            try
            {
                // Copy to a real temp path so AssetsManager can open it by path
                var idx     = System.Threading.Interlocked.Increment(ref _tmpCounter);
                var tmpName = $"saf_{idx}_{System.IO.Path.GetFileName(displayName)}";
                var tmpPath = System.IO.Path.Combine(cacheDir, tmpName);

                using var inStream  = resolver.OpenInputStream(fileUri)
                    ?? throw new Exception("Cannot open input stream");
                using var outStream = File.Create(tmpPath);
                inStream.CopyTo(outStream);

                results.Add((displayName, tmpPath));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AndroidDownloadService] Skip '{displayName}': {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Deletes temp files written by GetFilesFromSafUri.
    /// Call this in a finally block after loading completes.
    /// </summary>
    public static void CleanupTempFiles(IEnumerable<string> tempPaths)
    {
        foreach (var path in tempPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }
    }
#endif
}
