using System.Text;

namespace UAV.Services;

public static class MeshExporter
{
    public static string ExportToObj(MeshParseResult meshData, string assetName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Exported from bareiron viewer");
        sb.AppendLine($"# Asset: {assetName}");
        sb.AppendLine($"# Vertices: {meshData.VertexCount}");
        sb.AppendLine($"# Triangles: {meshData.TriangleCount}");
        sb.AppendLine();

        sb.AppendLine("o " + SanitizeObjName(assetName));
        sb.AppendLine();

        if (meshData.Vertices != null)
        {
            for (int i = 0; i < meshData.Vertices.Length; i += 3)
            {
                float x = meshData.Vertices[i];
                float y = meshData.Vertices[i + 1];
                float z = meshData.Vertices[i + 2];
                sb.AppendLine($"v {x:F6} {y:F6} {z:F6}");
            }
        }

        if (meshData.Normals != null && meshData.Normals.Length > 0)
        {
            sb.AppendLine();
            for (int i = 0; i < meshData.Normals.Length; i += 3)
            {
                float x = meshData.Normals[i];
                float y = meshData.Normals[i + 1];
                float z = meshData.Normals[i + 2];
                sb.AppendLine($"vn {x:F6} {y:F6} {z:F6}");
            }
        }

        if (meshData.Colors != null && meshData.Colors.Length > 0)
        {
            sb.AppendLine();
            for (int i = 0; i < meshData.Colors.Length; i += 4)
            {
                float r = meshData.Colors[i];
                float g = meshData.Colors[i + 1];
                float b = meshData.Colors[i + 2];
                float a = meshData.Colors[i + 3];
                sb.AppendLine($"vc {r:F4} {g:F4} {b:F4} {a:F4}");
            }
        }

        if (meshData.UVs != null)
        {
            for (int uv = 0; uv < meshData.UVs.Length; uv++)
            {
                var uvData = meshData.UVs[uv];
                if (uvData == null || uvData.Length == 0) continue;
                
                sb.AppendLine();
                string vtPrefix = uv == 0 ? "vt" : $"vt{uv}";
                int uvDim = uvData.Length / meshData.VertexCount;
                
                for (int i = 0; i < uvData.Length; i += uvDim)
                {
                    if (uvDim >= 1) sb.Append($"{vtPrefix} {uvData[i]:F6}");
                    if (uvDim >= 2) sb.Append($" {uvData[i + 1]:F6}");
                    if (uvDim >= 3) sb.Append($" {uvData[i + 2]:F6}");
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine();

        if (meshData.Indices != null)
        {
            for (int i = 0; i < meshData.Indices.Length; i += 3)
            {
                int i1 = meshData.Indices[i] + 1;
                int i2 = meshData.Indices[i + 1] + 1;
                int i3 = meshData.Indices[i + 2] + 1;
                sb.AppendLine($"f {i1} {i2} {i3}");
            }
        }

        return sb.ToString();
    }

    private static string SanitizeObjName(string name)
    {
        return new string(name.Where(c => !char.IsControl(c) && c != ' ').ToArray());
    }
}