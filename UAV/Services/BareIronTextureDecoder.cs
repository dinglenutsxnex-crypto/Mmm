using System;
using System.Buffers;
using AssetRipper.TextureDecoder.Astc;
using AssetRipper.TextureDecoder.Bc;
using AssetRipper.TextureDecoder.Dxt;
using AssetRipper.TextureDecoder.Etc;

namespace UAV.Services;

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
    public static byte[] Decode(byte[] data, int width, int height, int format, bool swapRB = false, int scaleDiv = 1)
    {
        if (scaleDiv > 1)
        {
            int newW = Math.Max(1, width / scaleDiv);
            int newH = Math.Max(1, height / scaleDiv);
            var full = Decode(data, width, height, format, swapRB, 1);
            return DownScale(full, width, height, newW, newH);
        }

        format = GuessActualFormat(data, width, height, format);

        int expectedCompressed = GetExpectedCompressedSize(width, height, format);
        if (data.Length > expectedCompressed)
            data = data[..expectedCompressed];

        System.Diagnostics.Debug.WriteLine($"[Decoder] Format={format} ({GetFormatName(format)}) | {width}x{height}");

        byte[] output = null;
        bool success = format switch
        {
            3 => DecodeRGB24(data, width, height, out output),
            4 => DecodeRGBA32(data, width, height, out output),
            5 => DecodeARGB32(data, width, height, out output),
            13 => DecodeBGRA32(data, width, height, out output),
            7 => DecodeDXT1(data, width, height, out output),
            10 => DecodeDXT5(data, width, height, out output),
            34 => DecodeBC6H(data, width, height, out output),
            45 => DecodeETC(data, width, height, out output),
            46 => DecodeETC(data, width, height, out output),
            47 => DecodeETC2(data, width, height, out output),
            48 => DecodeETC2A1(data, width, height, out output),
            49 => DecodeETC2A8(data, width, height, out output),
            52 => DecodeBC4(data, width, height, out output),
            53 => DecodeBC5(data, width, height, out output),
            54 => DecodeBC6H(data, width, height, out output),
            55 => DecodeBC7(data, width, height, out output),
            62 => DecodeBC4(data, width, height, out output),
            63 => DecodeBC5(data, width, height, out output),
            74 => DecodeASTC(data, width, height, 4, 4, out output),
            77 => DecodeASTC(data, width, height, 8, 8, out output),
            _ => throw new NotSupportedException($"Format {format} not supported")
        };

        if (!success || output == null)
            throw new Exception($"Decode failed for format {format}");

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

    private static byte[] DownScale(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        float xRatio = (float)srcW / dstW;
        float yRatio = (float)srcH / dstH;

        for (int y = 0; y < dstH; y++)
        {
            int srcY = (int)(y * yRatio);
            for (int x = 0; x < dstW; x++)
            {
                int srcX = (int)(x * xRatio);
                int srcIdx = (srcY * srcW + srcX) * 4;
                int dstIdx = (y * dstW + x) * 4;
                dst[dstIdx] = src[srcIdx];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
                dst[dstIdx + 3] = src[srcIdx + 3];
            }
        }
        return dst;
    }

    private static int GuessActualFormat(byte[] data, int w, int h, int reported)
    {
        if (reported is not (45 or 46 or 47 or 48 or 49))
            return reported;

        int sizeAstc8x8 = GetExpectedCompressedSize(w, h, 77);
        int sizeEtc2Rgba8 = GetExpectedCompressedSize(w, h, 49);
        int sizeEtc2Rgb4 = GetExpectedCompressedSize(w, h, 47);

        if (data.Length == sizeAstc8x8 && sizeAstc8x8 != sizeEtc2Rgb4)
            return 77;
        if (data.Length >= sizeEtc2Rgba8)
            return 49;
        if (data.Length >= sizeEtc2Rgb4)
            return 47;

        return reported;
    }

    public static int GetExpectedCompressedSize(int w, int h, int format) => format switch
    {
        3 => w * h * 3,
        4 => w * h * 4,
        5 => w * h * 4,
        13 => w * h * 4,
        7 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        10 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        34 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        45 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        46 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        47 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        48 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        49 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        52 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        53 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        54 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        55 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        62 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        63 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        74 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        77 => Math.Max(1, (w + 7) / 8) * Math.Max(1, (h + 7) / 8) * 16,
        _ => w * h * 4
    };

    private static string GetFormatName(int format) => format switch
    {
        3 => "RGB24", 4 => "RGBA32", 5 => "ARGB32", 13 => "BGRA32",
        7 => "DXT1", 10 => "DXT5", 34 => "BC6H",
        45 => "ETC1", 46 => "ETC1_3DS", 47 => "ETC2_RGB4", 48 => "ETC2_RGBA1", 49 => "ETC2_RGBA8",
        52 => "BC4", 53 => "BC5", 54 => "BC6H_2", 55 => "BC7",
        62 => "BC4_2", 63 => "BC5_2", 74 => "ASTC_4x4", 77 => "ASTC_8x8",
        _ => $"UNKNOWN_{format}"
    };

    private static bool DecodeRGB24(byte[] input, int w, int h, out byte[] output)
    {
        output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            output[i * 4] = input[i * 3];
            output[i * 4 + 1] = input[i * 3 + 1];
            output[i * 4 + 2] = input[i * 3 + 2];
            output[i * 4 + 3] = 255;
        }
        return true;
    }

    private static bool DecodeRGBA32(byte[] input, int w, int h, out byte[] output)
    {
        output = new byte[w * h * 4];
        Array.Copy(input, output, Math.Min(input.Length, output.Length));
        return true;
    }

    private static bool DecodeARGB32(byte[] input, int w, int h, out byte[] output)
    {
        output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            output[i * 4] = input[i * 4 + 1];
            output[i * 4 + 1] = input[i * 4 + 2];
            output[i * 4 + 2] = input[i * 4 + 3];
            output[i * 4 + 3] = input[i * 4];
        }
        return true;
    }

    private static bool DecodeBGRA32(byte[] input, int w, int h, out byte[] output)
    {
        output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            output[i * 4] = input[i * 4 + 2];
            output[i * 4 + 1] = input[i * 4 + 1];
            output[i * 4 + 2] = input[i * 4];
            output[i * 4 + 3] = input[i * 4 + 3];
        }
        return true;
    }

    private static bool DecodeDXT1(byte[] data, int w, int h, out byte[] output)
    {
        output = null;
        DxtDecoder.DecompressDXT1(data, w, h, out output);
        if (output != null) SwapRBtoRGBA(output);
        return output != null;
    }

    private static bool DecodeDXT5(byte[] data, int w, int h, out byte[] output)
    {
        output = null;
        DxtDecoder.DecompressDXT5(data, w, h, out output);
        if (output != null) SwapRBtoRGBA(output);
        return output != null;
    }

    private static bool DecodeBC4(byte[] data, int w, int h, out byte[] output)
    {
        output = null;
        Bc4.Decompress(data, w, h, out output);
        return output != null;
    }

    private static bool DecodeBC5(byte[] data, int w, int h, out byte[] output)
    {
        output = null;
        Bc5.Decompress(data, w, h, out output);
        if (output != null) SwapRBtoRGBA(output);
        return output != null;
    }

    private static bool DecodeBC6H(byte[] data, int w, int h, out byte[] output)
    {
        output = null;
        Bc6h.Decompress(data, w, h, false, out output);
        if (output != null) SwapRBtoRGBA(output);
        return output != null;
    }

    private static bool DecodeBC7(byte[] data, int w, int h, out byte[] output)
    {
        output = null;
        Bc7.Decompress(data, w, h, out output);
        if (output != null) SwapRBtoRGBA(output);
        return output != null;
    }

    private static bool DecodeETC(byte[] data, int w, int h, out byte[] output)
    {
        output = null;
        EtcDecoder.DecompressETC(data, w, h, out output);
        if (output != null) SwapRBtoRGBA(output);
        return output != null;
    }

    private static bool DecodeETC2(byte[] data, int w, int h, out byte[] output)
    {
        output = null;
        EtcDecoder.DecompressETC2(data, w, h, out output);
        if (output != null) SwapRBtoRGBA(output);
        return output != null;
    }

    private static bool DecodeETC2A1(byte[] data, int w, int h, out byte[] output)
    {
        output = null;
        EtcDecoder.DecompressETC2A1(data, w, h, out output);
        if (output != null) SwapRBtoRGBA(output);
        return output != null;
    }

    private static bool DecodeETC2A8(byte[] data, int w, int h, out byte[] output)
    {
        output = null;
        EtcDecoder.DecompressETC2A8(data, w, h, out output);
        if (output != null) SwapRBtoRGBA(output);
        return output != null;
    }

    private static bool DecodeASTC(byte[] data, int w, int h, int blockW, int blockH, out byte[] output)
    {
        output = null;
        AstcDecoder.DecodeASTC(data, w, h, blockW, blockH, out output);
        if (output != null) SwapRBtoRGBA(output);
        return output != null;
    }

    private static void SwapRBtoRGBA(byte[] bgra)
    {
        if (bgra == null || bgra.Length == 0)
            return;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        }
    }
}