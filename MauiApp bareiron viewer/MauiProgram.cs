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