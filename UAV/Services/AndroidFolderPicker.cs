namespace UAV.Services;

/// <summary>
/// Launches Android's ACTION_OPEN_DOCUMENT_TREE and returns the selected
/// SAF tree URI string, or null if the user cancelled.
/// No CommunityToolkit dependency required.
/// </summary>
public static class AndroidFolderPicker
{
#if ANDROID
    static TaskCompletionSource<string?>? _tcs;

    public static Task<string?> PickFolderAsync()
    {
        _tcs = new TaskCompletionSource<string?>();

        var intent = new Android.Content.Intent(Android.Content.Intent.ActionOpenDocumentTree);
        intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission |
                        Android.Content.ActivityFlags.GrantPersistableUriPermission);

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
            ?? throw new InvalidOperationException("No current Activity");

        activity.StartActivityForResult(intent, RequestCode);

        return _tcs.Task;
    }

    public const int RequestCode = 9901;

    /// <summary>
    /// Call this from your MainActivity's OnActivityResult override.
    /// </summary>
    public static void OnActivityResult(int requestCode, Android.App.Result resultCode, Android.Content.Intent? data)
    {
        if (requestCode != RequestCode) return;

        if (resultCode == Android.App.Result.Ok && data?.Data != null)
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            activity?.ContentResolver?.TakePersistableUriPermission(
                data.Data,
                Android.Content.ActivityFlags.GrantReadUriPermission);

            var uri = data.Data.ToString();
            _tcs?.TrySetResult(uri);
        }
        else
        {
            _tcs?.TrySetResult(null);
        }

        _tcs = null;
    }
#endif
}
