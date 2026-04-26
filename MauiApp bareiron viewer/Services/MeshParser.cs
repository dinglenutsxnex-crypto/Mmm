using System;
using System.IO;
using System.Linq;
using System.Buffers;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UAV.Services;

public class MeshParseResult
{
    public float[]? Vertices { get; set; }
    public int[]? Indices { get; set; }
    public int VertexCount { get; set; }
    public int TriangleCount { get; set; }
    public int SubMeshCount { get; set; }
    public float[]? Normals { get; set; }
    public float[]? Tangents { get; set; }
    public float[]? Colors { get; set; }
    public float[][]? UVs { get; set; }
    public string? Error { get; set; }
    public bool Success => Vertices != null && Indices != null && Error == null;
}

public static class MeshParser
{
    public static MeshParseResult Parse(AssetTypeValueField field, AssetsFileInstance? assetsFileInst = null, AssetFileInfo? assetInfo = null)
    {
        try
        {
            string unityVersion = assetsFileInst?.file?.Metadata?.UnityVersion ?? "5.0";
            return ParseImpl(field, assetsFileInst, unityVersion);
        }
        catch (Exception ex)
        {
            return new MeshParseResult { Error = ex.Message };
        }
    }

    private static MeshParseResult ParseImpl(AssetTypeValueField field, AssetsFileInstance? fileInst, string unityVersion)
    {
        int subMeshCount = 0;
        var subMeshesField = field["m_SubMeshes"];
        if (subMeshesField != null && !subMeshesField.IsDummy)
        {
            var arr = subMeshesField["Array"];
            if (arr != null && arr.Children != null)
                subMeshCount = arr.Children.Count;
        }

        var indices = ReadIndices(field);
        if (indices == null)
            return new MeshParseResult { Error = "No indices", SubMeshCount = subMeshCount };

        var channels = ReadAllChannels(field, fileInst, unityVersion);
        if (channels.Vertices == null)
            return new MeshParseResult { Error = "No vertices", SubMeshCount = subMeshCount };

        int vertexCount = channels.VertexCount;
        int triangleCount = indices.Length / 3;

        return new MeshParseResult
        {
            Vertices = channels.Vertices,
            Indices = indices,
            VertexCount = vertexCount,
            TriangleCount = triangleCount,
            SubMeshCount = subMeshCount,
            Normals = channels.Normals,
            Tangents = channels.Tangents,
            Colors = channels.Colors,
            UVs = channels.UVs
        };
    }

    private class MeshChannels
    {
        public float[]? Vertices { get; set; }
        public float[]? Normals { get; set; }
        public float[]? Tangents { get; set; }
        public float[]? Colors { get; set; }
        public float[][]? UVs { get; set; }
        public int VertexCount { get; set; }
    }

    private static MeshChannels ReadAllChannels(AssetTypeValueField field, AssetsFileInstance? fileInst, string unityVersion)
    {
        var result = new MeshChannels();

        var vertexDataField = field["m_VertexData"];
        if (vertexDataField == null || vertexDataField.IsDummy)
            return result;

        var vertexCountField = vertexDataField["m_VertexCount"];
        int vertexCount = vertexCountField?.AsInt ?? 0;
        if (vertexCount == 0)
            return result;

        result.VertexCount = vertexCount;

        byte[]? vertexBytes = GetVertexData(field, fileInst);
        if (vertexBytes == null || vertexBytes.Length == 0)
            return result;

        var channelsField = vertexDataField["m_Channels"];
        if (channelsField == null || channelsField.IsDummy)
            return result;

        var channelsArr = channelsField["Array"];
        if (channelsArr == null || channelsArr.Children == null || channelsArr.Children.Count == 0)
            return result;

        var channels = channelsArr.Children
            .Where(c => c != null && !c.IsDummy)
            .Select(c => new ChannelInfo(c))
            .ToList();

        if (channels.Count == 0)
            return result;

        var streamLengths = CalculateStreamLengths(channels, unityVersion);
        var ver = ParseVersion(unityVersion);
        bool is2018Plus = ver[0] >= 2018;

        for (int chnIdx = 0; chnIdx < channels.Count; chnIdx++)
        {
            var channel = channels[chnIdx];
            if (channel.dimension == 0) continue;

            int streamLength = streamLengths[channel.stream];
            int format = (int)ToVertexFormatV2(channel.format, unityVersion);
            int stride = GetFormatSize((VertexFormat)format) * (channel.dimension & 0xf);

            int startPos = 0;
            for (int s = 0; s < channel.stream; s++)
            {
                startPos += streamLengths[s] * vertexCount;
            }

            int offset = startPos + channel.offset;
            int dimension = channel.dimension & 0xf;

            float[]? channelData = null;
            byte[] vertData = ArrayPool<byte>.Shared.Rent(stride);

            try
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    int pos = offset + v * streamLength;
                    if (pos + stride > vertexBytes.Length) break;

                    Buffer.BlockCopy(vertexBytes, pos, vertData, 0, stride);

                    var floatItems = ConvertFloatArray(vertData, dimension, (VertexFormat)format);

                    if (channelData == null)
                        channelData = new float[vertexCount * dimension];

                    for (int d = 0; d < dimension; d++)
                    {
                        channelData[v * dimension + d] = floatItems[d];
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(vertData);
            }

            if (channelData == null) continue;

            int channelType = chnIdx;
            if (is2018Plus)
            {
                switch ((ChannelTypeV3)channelType)
                {
                    case ChannelTypeV3.Vertex: result.Vertices = channelData; break;
                    case ChannelTypeV3.Normal: result.Normals = channelData; break;
                    case ChannelTypeV3.Tangent: result.Tangents = channelData; break;
                    case ChannelTypeV3.Color: result.Colors = channelData; break;
                    case ChannelTypeV3.TexCoord0:
                    case ChannelTypeV3.TexCoord1:
                    case ChannelTypeV3.TexCoord2:
                    case ChannelTypeV3.TexCoord3:
                    case ChannelTypeV3.TexCoord4:
                    case ChannelTypeV3.TexCoord5:
                    case ChannelTypeV3.TexCoord6:
                    case ChannelTypeV3.TexCoord7:
                        if (result.UVs == null) result.UVs = new float[8][];
                        result.UVs[(int)channelType - (int)ChannelTypeV3.TexCoord0] = channelData;
                        break;
                }
            }
            else
            {
                switch ((ChannelTypeV2)channelType)
                {
                    case ChannelTypeV2.Vertex: result.Vertices = channelData; break;
                    case ChannelTypeV2.Normal: result.Normals = channelData; break;
                    case ChannelTypeV2.Color: result.Colors = channelData; break;
                    case ChannelTypeV2.TexCoord0:
                    case ChannelTypeV2.TexCoord1:
                    case ChannelTypeV2.TexCoord2:
                    case ChannelTypeV2.TexCoord3:
                        if (result.UVs == null) result.UVs = new float[4][];
                        result.UVs[(int)channelType - (int)ChannelTypeV2.TexCoord0] = channelData;
                        break;
                    case ChannelTypeV2.Tangent: result.Tangents = channelData; break;
                }
            }
        }

        return result;
    }

    private enum ChannelTypeV3 { Vertex = 0, Normal = 1, Tangent = 2, Color = 3, TexCoord0 = 4, TexCoord1 = 5, TexCoord2 = 6, TexCoord3 = 7, TexCoord4 = 8, TexCoord5 = 9, TexCoord6 = 10, TexCoord7 = 11, BlendWeight = 12, BlendIndices = 13 }
    private enum ChannelTypeV2 { Vertex = 0, Normal = 1, Color = 2, TexCoord0 = 3, TexCoord1 = 4, TexCoord2 = 5, TexCoord3 = 6, Tangent = 7 }

    private static int[]? ReadIndices(AssetTypeValueField field)
    {
        var indexBufferField = field["m_IndexBuffer.Array"];
        if (indexBufferField == null || indexBufferField.IsDummy)
        {
            indexBufferField = field["m_IndexBuffer"];
            if (indexBufferField == null || indexBufferField.IsDummy)
                return null;
        }

        byte[]? indexBytes = indexBufferField.AsByteArray;
        if (indexBytes == null || indexBytes.Length < 2)
            return null;

        int count = indexBytes.Length / 2;
        int[] indices = new int[count];

        for (int i = 0; i < count; i++)
        {
            indices[i] = indexBytes[i * 2] | (indexBytes[i * 2 + 1] << 8);
        }

        return indices;
    }

    private static float[]? ReadVertices(AssetTypeValueField field, AssetsFileInstance? fileInst, string unityVersion)
    {
        var vertexDataField = field["m_VertexData"];
        if (vertexDataField == null || vertexDataField.IsDummy)
            return null;

        var vertexCountField = vertexDataField["m_VertexCount"];
        int vertexCount = vertexCountField?.AsInt ?? 0;
        if (vertexCount == 0)
            return null;

        byte[]? vertexBytes = GetVertexData(field, fileInst);
        if (vertexBytes == null || vertexBytes.Length == 0)
            return null;

        var channelsField = vertexDataField["m_Channels"];
        if (channelsField == null || channelsField.IsDummy)
            return null;

        var channelsArr = channelsField["Array"];
        if (channelsArr == null || channelsArr.Children == null || channelsArr.Children.Count == 0)
            return null;

        var channels = channelsArr.Children
            .Where(c => c != null && !c.IsDummy)
            .Select(c => new ChannelInfo(c))
            .ToList();

        if (channels.Count == 0)
            return null;

        var posChannel = channels.FirstOrDefault(c => c.dimension > 0);
        if (posChannel == null)
            return null;

        var streamLengths = CalculateStreamLengths(channels, unityVersion);
        int startPos = 0;
        for (int s = 0; s < posChannel.stream; s++)
        {
            startPos += streamLengths[s] * vertexCount;
        }

        int streamLength = streamLengths[posChannel.stream];
        int format = (int)ToVertexFormatV2(posChannel.format, unityVersion);
        int stride = GetFormatSize((VertexFormat)format) * (posChannel.dimension & 0xf);

        float[] vertices = new float[vertexCount * 3];
        int posOffset = startPos + posChannel.offset;

        for (int v = 0; v < vertexCount; v++)
        {
            int pos = posOffset + v * streamLength;
            if (pos + stride > vertexBytes.Length) break;

            byte[] vertData = new byte[stride];
            Buffer.BlockCopy(vertexBytes, pos, vertData, 0, stride);

            var floatItems = ConvertFloatArray(vertData, 3, (VertexFormat)format);
            vertices[v * 3] = floatItems[0];
            vertices[v * 3 + 1] = floatItems[1];
            vertices[v * 3 + 2] = floatItems[2];
        }

        return vertices;
    }

    private static float[] ConvertFloatArray(byte[] data, int dimension, VertexFormat format)
    {
        var items = new float[dimension];
        int size = GetFormatSize(format);

        for (int i = 0; i < dimension; i++)
        {
            int offset = i * size;
            items[i] = format switch
            {
                VertexFormat.Float => BitConverter.ToSingle(data, offset),
                VertexFormat.Float16 => HalfToFloat(BitConverter.ToUInt16(data, offset)),
                VertexFormat.UNorm8 => data[offset] / 255f,
                VertexFormat.SNorm8 => Math.Max((sbyte)data[offset] / 127f, -1f),
                VertexFormat.UNorm16 => (data[offset] | (data[offset + 1] << 8)) / 65535f,
                VertexFormat.SNorm16 => Math.Max((short)(data[offset] | (data[offset + 1] << 8)) / 32767f, -1f),
                _ => BitConverter.ToSingle(data, offset)
            };
        }
        return items;
    }

    private static VertexFormat ToVertexFormatV2(int format, string unityVersion)
    {
        var ver = ParseVersion(unityVersion);
        if (ver[0] >= 2019)
            return (VertexFormat)format;
        if (ver[0] >= 2017)
        {
            return (VertexFormatV1)format switch
            {
                VertexFormatV1.Float => VertexFormat.Float,
                VertexFormatV1.Float16 => VertexFormat.Float16,
                VertexFormatV1.Color => VertexFormat.UNorm8,
                VertexFormatV1.UNorm8 => VertexFormat.UNorm8,
                VertexFormatV1.SNorm8 => VertexFormat.SNorm8,
                VertexFormatV1.UNorm16 => VertexFormat.UNorm16,
                VertexFormatV1.SNorm16 => VertexFormat.SNorm16,
                VertexFormatV1.UInt8 => VertexFormat.UInt8,
                VertexFormatV1.SInt8 => VertexFormat.SInt8,
                VertexFormatV1.UInt16 => VertexFormat.UInt16,
                VertexFormatV1.SInt16 => VertexFormat.SInt16,
                VertexFormatV1.UInt32 => VertexFormat.UInt32,
                VertexFormatV1.SInt32 => VertexFormat.SInt32,
                _ => VertexFormat.Float
            };
        }
        return (VertexChannelFormat)format switch
        {
            VertexChannelFormat.Float => VertexFormat.Float,
            VertexChannelFormat.Float16 => VertexFormat.Float16,
            VertexChannelFormat.Color => VertexFormat.UNorm8,
            VertexChannelFormat.Byte => VertexFormat.UInt8,
            VertexChannelFormat.UInt32 => VertexFormat.UInt32,
            _ => VertexFormat.Float
        };
    }

    private static byte[]? GetVertexData(AssetTypeValueField field, AssetsFileInstance? fileInst)
    {
        uint offset = 0;
        uint size = 0;
        string path = "";

        var streamData = field["m_StreamData"];
        if (streamData != null && !streamData.IsDummy)
        {
            offset = streamData["offset"]?.AsUInt ?? 0;
            size = streamData["size"]?.AsUInt ?? 0;
            path = streamData["path"]?.AsString ?? "";
        }

        bool usesStreamData = size > 0 && !string.IsNullOrEmpty(path);

        if (usesStreamData && fileInst?.parentBundle != null)
        {
            return ReadStreamData(fileInst, path, offset, size);
        }

        var vertexDataField = field["m_VertexData"]["m_DataSize"];
        return vertexDataField?.AsByteArray;
    }

    private static byte[]? ReadStreamData(AssetsFileInstance fileInst, string path, uint offset, uint size)
    {
        var bundle = fileInst.parentBundle.file;
        var dirInfo = bundle.BlockAndDirInfo.DirectoryInfos;

        string archiveTrimmedPath = path.StartsWith("archive:/") ? path.Substring(9) : path;
        archiveTrimmedPath = Path.GetFileName(archiveTrimmedPath);

        var reader = bundle.DataReader;
        for (int i = 0; i < dirInfo.Count; i++)
        {
            var info = dirInfo[i];
            if (info.Name == archiveTrimmedPath)
            {
                reader.Position = info.Offset + offset;
                return reader.ReadBytes((int)size);
            }
        }

        return null;
    }

    private static int[] CalculateStreamLengths(System.Collections.Generic.List<ChannelInfo> channels, string unityVersion)
    {
        var streamLengths = new int[channels.Max(c => c.stream) + 1];
        if (channels.Count == 0) return streamLengths;

        int streamCount = channels.Max(c => c.stream) + 1;

        for (int s = 0; s < streamCount; s++)
        {
            int maxEndOffset = 0;
            for (int j = 0; j < channels.Count; j++)
            {
                var channel = channels[j];
                if (channel.stream == s && channel.dimension > 0)
                {
                    int fmt = (int)ToVertexFormat(channel.format, unityVersion);
                    int size = GetFormatSize((VertexFormat)fmt);
                    int endOffset = channel.offset + ((channel.dimension & 0xF) * size);
                    maxEndOffset = Math.Max(maxEndOffset, endOffset);
                }
            }
            streamLengths[s] = maxEndOffset;
        }

        return streamLengths;
    }

    private static int GetFormatSize(VertexFormat format)
    {
        return format switch
        {
            VertexFormat.Float => 4,
            VertexFormat.Float16 => 2,
            VertexFormat.UNorm8 => 1,
            VertexFormat.SNorm8 => 1,
            VertexFormat.UNorm16 => 2,
            VertexFormat.SNorm16 => 2,
            VertexFormat.UInt8 => 1,
            VertexFormat.SInt8 => 1,
            VertexFormat.UInt16 => 2,
            VertexFormat.SInt16 => 2,
            VertexFormat.UInt32 => 4,
            VertexFormat.SInt32 => 4,
            _ => 4
        };
    }

    private static VertexFormat ToVertexFormat(int format, string unityVersion)
    {
        var ver = ParseVersion(unityVersion);
        if (ver[0] >= 2019)
            return (VertexFormat)format;
        if (ver[0] >= 2017)
        {
            return (VertexFormatV1)format switch
            {
                VertexFormatV1.Float => VertexFormat.Float,
                VertexFormatV1.Float16 => VertexFormat.Float16,
                VertexFormatV1.Color => VertexFormat.UNorm8,
                VertexFormatV1.UNorm8 => VertexFormat.UNorm8,
                VertexFormatV1.SNorm8 => VertexFormat.SNorm8,
                VertexFormatV1.UNorm16 => VertexFormat.UNorm16,
                VertexFormatV1.SNorm16 => VertexFormat.SNorm16,
                VertexFormatV1.UInt8 => VertexFormat.UInt8,
                VertexFormatV1.SInt8 => VertexFormat.SInt8,
                VertexFormatV1.UInt16 => VertexFormat.UInt16,
                VertexFormatV1.SInt16 => VertexFormat.SInt16,
                VertexFormatV1.UInt32 => VertexFormat.UInt32,
                VertexFormatV1.SInt32 => VertexFormat.SInt32,
                _ => VertexFormat.Float
            };
        }
        return (VertexChannelFormat)format switch
        {
            VertexChannelFormat.Float => VertexFormat.Float,
            VertexChannelFormat.Float16 => VertexFormat.Float16,
            VertexChannelFormat.Color => VertexFormat.UNorm8,
            VertexChannelFormat.Byte => VertexFormat.UInt8,
            VertexChannelFormat.UInt32 => VertexFormat.UInt32,
            _ => VertexFormat.Float
        };
    }

    private static int[] ParseVersion(string version)
    {
        var parts = version.Split('.');
        int[] v = new int[3];
        if (parts.Length > 0) int.TryParse(parts[0], out v[0]);
        if (parts.Length > 1) int.TryParse(parts[1], out v[1]);
        if (parts.Length > 2) int.TryParse(parts[2].Split('-')[0].Split('f')[0], out v[2]);
        return v;
    }

    private static float ReadFloat(byte[] data, int offset, VertexFormat format)
    {
        if (offset + 2 > data.Length)
            return 0;

        return format switch
        {
            VertexFormat.Float => BitConverter.ToSingle(data, offset),
            VertexFormat.Float16 => HalfToFloat(BitConverter.ToUInt16(data, offset)),
            VertexFormat.UNorm8 => data[offset] / 255f,
            VertexFormat.SNorm8 => Math.Max((sbyte)data[offset] / 127f, -1f),
            VertexFormat.UNorm16 => (data[offset] | (data[offset + 1] << 8)) / 65535f,
            VertexFormat.SNorm16 => Math.Max((short)(data[offset] | (data[offset + 1] << 8)) / 32767f, -1f),
            _ => BitConverter.ToSingle(data, offset)
        };
    }

    private static float HalfToFloat(ushort half)
    {
        int exp = (half >> 10) & 0x1F;
        int mantissa = half & 0x3FF;
        int sign = (half >> 15) & 1;

        if (exp == 0)
            return mantissa / 16777216f * (sign == 1 ? -1 : 1);
        if (exp == 31)
            return mantissa == 0 ? float.PositiveInfinity : float.NaN;

        float result = (float)Math.Pow(2, exp - 15) * (1f + mantissa / 1024f);
        return sign == 1 ? -result : result;
    }

    private class ChannelInfo
    {
        public byte stream;
        public byte offset;
        public byte format;
        public byte dimension;

        public ChannelInfo(AssetTypeValueField field)
        {
            stream = (byte)(field["stream"]?.AsInt ?? 0);
            offset = (byte)(field["offset"]?.AsInt ?? 0);
            format = (byte)(field["format"]?.AsInt ?? 0);
            dimension = (byte)((field["dimension"]?.AsInt ?? 0) & 0xF);
        }
    }

    private enum VertexChannelFormat { Float = 0, Float16 = 1, Color = 2, Byte = 3, UInt32 = 4 }
    private enum VertexFormatV1 { Float = 0, Float16 = 1, Color = 2, UNorm8 = 3, SNorm8 = 4, UNorm16 = 5, SNorm16 = 6, UInt8 = 7, SInt8 = 8, UInt16 = 9, SInt16 = 10, UInt32 = 11, SInt32 = 12 }
    private enum VertexFormat { Float = 0, Float16 = 1, UNorm8 = 2, SNorm8 = 3, UNorm16 = 4, SNorm16 = 5, UInt8 = 6, SInt8 = 7, UInt16 = 8, SInt16 = 9, UInt32 = 10, SInt32 = 11 }
}