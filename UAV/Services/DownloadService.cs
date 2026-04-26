using Microsoft.JSInterop;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace UAV.Services;

public class DownloadService
{
    private readonly IJSRuntime _js;
    
    public DownloadService(IJSRuntime js)
    {
        _js = js;
    }

    public static async Task SaveAndOpenFile(string filename, byte[] data, string mimeType)
    {
        try
        {
            var cacheDir = FileSystem.CacheDirectory;
            var filePath = Path.Combine(cacheDir, filename);
            
            await File.WriteAllBytesAsync(filePath, data);
            
            await Launcher.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(filePath)
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Download error: {ex.Message}");
        }
    }
}

public static class DownloadJsInterop
{
    [JSInvokable]
    public static async Task DownloadFile(string filename, string base64Data, string mimeType)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64Data);
            var cacheDir = FileSystem.CacheDirectory;
            var filePath = Path.Combine(cacheDir, filename);
            
            await File.WriteAllBytesAsync(filePath, bytes);
            
            await Launcher.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(filePath)
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Download error: {ex.Message}");
        }
    }
}