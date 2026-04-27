using Microsoft.JSInterop;

namespace UAV.Services;

public static class MobileDownloader
{
    public static async Task DownloadFileAsync(IJSRuntime js, string filename, byte[] data, string mimeType)
    {
        try
        {
            var isMobile = await js.InvokeAsync<bool>("eval", @"
                /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent)
            ");

            if (isMobile)
            {
                await js.InvokeVoidAsync("MeshViewer.saveBase64", filename, Convert.ToBase64String(data), mimeType);
            }
            else
            {
                var base64 = Convert.ToBase64String(data);
                await js.InvokeVoidAsync("MeshViewer.downloadBase64", filename, base64, mimeType);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Mobile download error: {ex.Message}");
        }
    }
}