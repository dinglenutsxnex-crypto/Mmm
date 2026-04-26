# Important Things for UAV

## Texture2D Fields (from AssetsTools.NET)

When reading Texture2D assets, use these exact field names:

- `m_Width` (int) - texture width
- `m_Height` (int) - texture height
- `m_TextureFormat` (int) - Unity format enum value
- `image data` (TypelessData) - raw texture data (may be null/empty)
- `m_StreamData` - external texture reference (if present, image data is stored externally)
- `m_MipCount` (int) - number of mipmaps
- `m_ColorSpace` (int) - color space (0=gamma, 1=linear)
- `m_PlatformBlob` - platform-specific blob for some formats

### Checking if image data is available
```csharp
var imageDataField = inspectorFields["image data"];
if (imageDataField == null || imageDataField.IsDummy)
{
    // Check if external
    var streamData = inspectorFields["m_StreamData"];
    // If streamData exists and has path, texture data is external
}
```

## Unity Texture Formats

Format enum values used by decoder service:
```csharp
public enum TextureFormat
{
    RGBA32 = 4,
    BGRA32 = 13,
    RGB24 = 3,
    BGR24 = 12,
    Alpha8 = 1,
    DXT1 = 7,
    DXT5 = 10,
    BC4 = 32,
    BC5 = 33,
    BC6H = 34,
    BC7 = 35,
    ETC_RGB4 = 45,
    ETC2_RGB4 = 47,
    ETC2_RGBA1 = 48,
    ETC2_RGBA8 = 49,
    ASTC_RGBA_4x4 = 54,
    PVRTC_RGBA4 = 63,
    // ... etc
}
```

## Decoder Service API

```csharp
using UAV.Services;

TextureDecodeResult result = TextureDecoderService.Decode(
    byte[] imageData,   // raw compressed texture data
    int width,          // texture width
    int height,         // texture height
    int format          // Unity TextureFormat enum value
);

// Result has:
// - result.Success (bool)
// - result.RgbaData (byte[] - RGBA8888 format, or null if failed)
// - result.Width, result.Height
// - result.Error (string - error message if failed)
```

## Supported Decoders

Uses `AssetRipper.TextureDecoder` package (pure C#):
- DXT1, DXT5 (BC1, BC3)
- BC4, BC5, BC6H, BC7
- ETC1, ETC2, ETC2_RGB4, ETC2_RGBA1, ETC2_RGBA8
- RGB24, RGBA32, BGRA32, BGR24, Alpha8

Not yet supported (need native libs):
- ASTC (all variants)
- PVRTC (all variants)
- ATC
- Crunch compressed formats

## Component Structure

- `Home.razor` - main page, loads bundles, lists assets
- `TexturePreview.razor` - displays decoded textures
- `Services/TextureDecoderService.cs` - texture decoding logic
- `Services/AssetService.cs` - (future) asset loading/decoding
- `Services/MeshService.cs` - (future) mesh decoding
- `Services/JsonViewer.razor` - (future) JSON display component
