namespace UAV.Services;

/// <summary>
/// Saves files directly to the Android Downloads folder.
/// Called from C# — no JS interop, no callbacks, no DotNetObjectReference.
/// Shows a toast after a successful save so the user knows it worked.
/// </summary>
public static class AndroidDownloadService
{
#if ANDROID
    public static async Task SaveFileAsync(string filename, byte[] bytes, string mimeType)
    {
        try
        {
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
    /// Returns (displayName, documentUriString) pairs — no temp copying.
    /// The document URI can be opened via ContentResolver.OpenInputStream at any time
    /// as long as the persisted permission is held.
    /// </summary>
    public static List<(string DisplayName, string DocumentUri)> GetFilesFromSafUri(string safUriString)
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

        while (cursor.MoveToNext())
        {
            var docId       = cursor.GetString(idCol)   ?? "";
            var displayName = cursor.GetString(nameCol) ?? docId;
            var mime        = cursor.GetString(mimeCol) ?? "";

            if (mime == Android.Provider.DocumentsContract.Document.MimeTypeDir)
                continue;

            var fileUri = Android.Provider.DocumentsContract
                .BuildDocumentUriUsingTree(treeUri, docId);
            if (fileUri == null) continue;

            results.Add((displayName, fileUri.ToString()!));
        }

        return results;
    }

    /// <summary>
    /// Opens an input stream for a SAF document URI string.
    /// </summary>
    public static System.IO.Stream OpenSafStream(string documentUriString)
    {
        var uri      = Android.Net.Uri.Parse(documentUriString)
            ?? throw new Exception($"Invalid document URI: {documentUriString}");
        var resolver = Android.App.Application.Context.ContentResolver!;
        return resolver.OpenInputStream(uri)
            ?? throw new Exception($"Cannot open input stream for: {documentUriString}");
    }
#endif
}
