using System;
using System.Buffers;
using AssetRipper.TextureDecoder.Astc;
using AssetRipper.TextureDecoder.Bc;
using AssetRipper.TextureDecoder.Dxt;
using AssetRipper.TextureDecoder.Etc;

namespace MauiApp_bareiron_viewer.Services;

public class TextureDecodeResult
{
    public byte[]? RgbaData { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FormatId { get; set; }
    public string? FormatName { get; set; }
    public bool SwapRB { get; set; } = false;
    public string? Error { get; set; }
    public bool Success => RgbaData != null && Error == null;
}

public static class BareIronTextureDecoder
{
    public static byte[] Decode(byte[] data, int width, int height, int format)
    {
        int expectedCompressed = GetExpectedCompressedSize(width, height, format);
        if (data.Length > expectedCompressed)
            data = data[..expectedCompressed];

        System.Diagnostics.Debug.WriteLine($"[Decoder] Format={format} ({GetFormatName(format)}) | {width}x{height} | dataLen={data.Length}");

        byte[]? output = format switch
        {
            1  => DecodeAlpha8(data, width, height),
            2  => DecodeARGB4444(data, width, height),
            3  => DecodeRGB24(data, width, height),
            4  => DecodeRGBA32(data, width, height),
            5  => DecodeARGB32(data, width, height),
            7  => DecodeRGB565(data, width, height),
            10 => DecodeR16(data, width, height),
            12 => DecodeDXT1(data, width, height),
            14 => DecodeDXT5(data, width, height),
            15 => DecodeRGBA4444(data, width, height),
            16 => DecodeBGRA32(data, width, height),
            20 => DecodeRHalf(data, width, height),
            22 => DecodeRGBAHalf(data, width, height),
            34 => DecodeBC6H(data, width, height),
            35 => DecodeBC7(data, width, height),
            36 => DecodeBC4(data, width, height),
            37 => DecodeBC5(data, width, height),
            45 => DecodeETC(data, width, height),
            46 => DecodeETC2(data, width, height),
            47 => DecodeETC2A1(data, width, height),
            48 => DecodeETC2A8(data, width, height),
            62 => DecodeASTC(data, width, height, 4, 4),
            63 => DecodeASTC(data, width, height, 5, 5),
            64 => DecodeASTC(data, width, height, 6, 6),
            65 => DecodeASTC(data, width, height, 8, 8),
            66 => DecodeASTC(data, width, height, 10, 10),
            67 => DecodeASTC(data, width, height, 12, 12),
            72 => DecodeETC(data, width, height),
            73 => DecodeETC2A8(data, width, height),
            _  => throw new NotSupportedException($"Unity TextureFormat {format} ({GetFormatName(format)}) is not supported")
        };

        if (output == null)
            throw new Exception($"Decoder returned null for format {format}");

        FlipVertically(output, width, height);
        return output;
    }

    private static void FlipVertically(byte[] data, int w, int h)
    {
        int rowSize = w * 4;
        var row = ArrayPool<byte>.Shared.Rent(rowSize);
        try
        {
            for (int y = 0; y < h / 2; y++)
            {
                int topOffset    = y * rowSize;
                int bottomOffset = (h - 1 - y) * rowSize;
                Array.Copy(data, topOffset, row, 0, rowSize);
                Array.Copy(data, bottomOffset, data, topOffset, rowSize);
                Array.Copy(row, 0, data, bottomOffset, rowSize);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(row); }
    }

    public static int GetExpectedCompressedSize(int w, int h, int format) => format switch
    {
        1  => w * h,
        2  => w * h * 2,
        3  => w * h * 3,
        4  => w * h * 4,
        5  => w * h * 4,
        7  => w * h * 2,
        10 => w * h * 2,
        12 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        14 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        15 => w * h * 2,
        16 => w * h * 4,
        20 => w * h * 2,
        22 => w * h * 8,
        34 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        35 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        36 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        37 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        45 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        46 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        47 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        48 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        62 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        63 => Math.Max(1, (w + 4) / 5) * Math.Max(1, (h + 4) / 5) * 16,
        64 => Math.Max(1, (w + 5) / 6) * Math.Max(1, (h + 5) / 6) * 16,
        65 => Math.Max(1, (w + 7) / 8) * Math.Max(1, (h + 7) / 8) * 16,
        66 => Math.Max(1, (w + 9) / 10) * Math.Max(1, (h + 9) / 10) * 16,
        67 => Math.Max(1, (w + 11) / 12) * Math.Max(1, (h + 11) / 12) * 16,
        72 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        73 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        _  => w * h * 4
    };

    public static string GetFormatName(int format) => format switch
    {
        1  => "Alpha8",
        2  => "ARGB4444",
        3  => "RGB24",
        4  => "RGBA32",
        5  => "ARGB32",
        7  => "RGB565",
        10 => "R16",
        12 => "DXT1",
        14 => "DXT5",
        15 => "RGBA4444",
        16 => "BGRA32",
        20 => "RHalf",
        22 => "RGBAHalf",
        34 => "BC6H",
        35 => "BC7",
        36 => "BC4",
        37 => "BC5",
        45 => "ETC_RGB4",
        46 => "ETC2_RGB",
        47 => "ETC2_RGBA1",
        48 => "ETC2_RGBA8",
        62 => "ASTC_4x4",
        63 => "ASTC_5x5",
        64 => "ASTC_6x6",
        65 => "ASTC_8x8",
        66 => "ASTC_10x10",
        67 => "ASTC_12x12",
        72 => "ETC_RGB4_3DS",
        73 => "ETC_RGBA8_3DS",
        _  => $"Unknown_{format}"
    };

    private static byte[] DecodeAlpha8(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
            output[i * 4 + 3] = input[i];
        return output;
    }

    private static byte[] DecodeARGB4444(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            ushort px = (ushort)(input[i * 2] | (input[i * 2 + 1] << 8));
            output[i * 4]     = (byte)(((px >> 8) & 0xF) * 17);
            output[i * 4 + 1] = (byte)(((px >> 4) & 0xF) * 17);
            output[i * 4 + 2] = (byte)((px & 0xF) * 17);
            output[i * 4 + 3] = (byte)(((px >> 12) & 0xF) * 17);
        }
        return output;
    }

    private static byte[] DecodeRGB24(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            output[i * 4]     = input[i * 3];
            output[i * 4 + 1] = input[i * 3 + 1];
            output[i * 4 + 2] = input[i * 3 + 2];
            output[i * 4 + 3] = 255;
        }
        return output;
    }

    private static byte[] DecodeRGBA32(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        Array.Copy(input, output, Math.Min(input.Length, output.Length));
        return output;
    }

    private static byte[] DecodeARGB32(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            output[i * 4]     = input[i * 4 + 1];
            output[i * 4 + 1] = input[i * 4 + 2];
            output[i * 4 + 2] = input[i * 4 + 3];
            output[i * 4 + 3] = input[i * 4];
        }
        return output;
    }

    private static byte[] DecodeRGB565(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            ushort px = (ushort)(input[i * 2] | (input[i * 2 + 1] << 8));
            output[i * 4]     = (byte)(((px >> 11) & 0x1F) * 255 / 31);
            output[i * 4 + 1] = (byte)(((px >> 5) & 0x3F) * 255 / 63);
            output[i * 4 + 2] = (byte)((px & 0x1F) * 255 / 31);
            output[i * 4 + 3] = 255;
        }
        return output;
    }

    private static byte[] DecodeR16(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte v = (byte)((input[i * 2] | (input[i * 2 + 1] << 8)) >> 8);
            output[i * 4]     = v;
            output[i * 4 + 1] = v;
            output[i * 4 + 2] = v;
            output[i * 4 + 3] = 255;
        }
        return output;
    }

    private static byte[] DecodeRGBA4444(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            ushort px = (ushort)(input[i * 2] | (input[i * 2 + 1] << 8));
            output[i * 4]     = (byte)(((px >> 12) & 0xF) * 17);
            output[i * 4 + 1] = (byte)(((px >> 8) & 0xF) * 17);
            output[i * 4 + 2] = (byte)(((px >> 4) & 0xF) * 17);
            output[i * 4 + 3] = (byte)((px & 0xF) * 17);
        }
        return output;
    }

    private static byte[] DecodeBGRA32(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            output[i * 4]     = input[i * 4 + 2];
            output[i * 4 + 1] = input[i * 4 + 1];
            output[i * 4 + 2] = input[i * 4];
            output[i * 4 + 3] = input[i * 4 + 3];
        }
        return output;
    }

    private static byte[] DecodeRHalf(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte v = HalfToByte((ushort)(input[i * 2] | (input[i * 2 + 1] << 8)));
            output[i * 4]     = v;
            output[i * 4 + 1] = v;
            output[i * 4 + 2] = v;
            output[i * 4 + 3] = 255;
        }
        return output;
    }

    private static byte[] DecodeRGBAHalf(byte[] input, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            output[i * 4]     = HalfToByte((ushort)(input[i * 8]     | (input[i * 8 + 1] << 8)));
            output[i * 4 + 1] = HalfToByte((ushort)(input[i * 8 + 2] | (input[i * 8 + 3] << 8)));
            output[i * 4 + 2] = HalfToByte((ushort)(input[i * 8 + 4] | (input[i * 8 + 5] << 8)));
            output[i * 4 + 3] = HalfToByte((ushort)(input[i * 8 + 6] | (input[i * 8 + 7] << 8)));
        }
        return output;
    }

    private static byte HalfToByte(ushort half)
    {
        float f = (float)BitConverter.UInt16BitsToHalf(half);
        return (byte)Math.Clamp((int)(f * 255f), 0, 255);
    }

    private static byte[]? DecodeDXT1(byte[] data, int w, int h)
    {
        DxtDecoder.DecompressDXT1(data, w, h, out var output);
        if (output != null) BgrToRgba(output);
        return output;
    }

    private static byte[]? DecodeDXT5(byte[] data, int w, int h)
    {
        DxtDecoder.DecompressDXT5(data, w, h, out var output);
        if (output != null) BgrToRgba(output);
        return output;
    }

    private static byte[]? DecodeBC4(byte[] data, int w, int h)
    {
        Bc4.Decompress(data, w, h, out var output);
        return output;
    }

    private static byte[]? DecodeBC5(byte[] data, int w, int h)
    {
        Bc5.Decompress(data, w, h, out var output);
        if (output != null) BgrToRgba(output);
        return output;
    }

    private static byte[]? DecodeBC6H(byte[] data, int w, int h)
    {
        Bc6h.Decompress(data, w, h, false, out var output);
        if (output != null) BgrToRgba(output);
        return output;
    }

    private static byte[]? DecodeBC7(byte[] data, int w, int h)
    {
        Bc7.Decompress(data, w, h, out var output);
        if (output != null) BgrToRgba(output);
        return output;
    }

    private static byte[]? DecodeETC(byte[] data, int w, int h)
    {
        EtcDecoder.DecompressETC(data, w, h, out var output);
        if (output != null) BgrToRgba(output);
        return output;
    }

    private static byte[]? DecodeETC2(byte[] data, int w, int h)
    {
        EtcDecoder.DecompressETC2(data, w, h, out var output);
        if (output != null) BgrToRgba(output);
        return output;
    }

    private static byte[]? DecodeETC2A1(byte[] data, int w, int h)
    {
        EtcDecoder.DecompressETC2A1(data, w, h, out var output);
        if (output != null) BgrToRgba(output);
        return output;
    }

    private static byte[]? DecodeETC2A8(byte[] data, int w, int h)
    {
        EtcDecoder.DecompressETC2A8(data, w, h, out var output);
        if (output != null) BgrToRgba(output);
        return output;
    }

    private static byte[]? DecodeASTC(byte[] data, int w, int h, int blockW, int blockH)
    {
        AstcDecoder.DecodeASTC(data, w, h, blockW, blockH, out var output);
        if (output != null) BgrToRgba(output);
        return output;
    }

    private static void BgrToRgba(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
    }
}
