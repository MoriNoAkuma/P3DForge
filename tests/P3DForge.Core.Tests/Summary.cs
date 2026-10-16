using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using P3DForge.Core.IO;
using P3DForge.Core.Model;

namespace P3DForge.Core.Tests;

/// <summary>
/// Canonical model summary — must match tools/oracle/mlod_oracle.py <c>summary</c>
/// (docs: testdata/synthetic/*.summary.json are produced by the oracle).
/// </summary>
public static class Summary
{
    static string Hex(uint v) => v.ToString("x8");

    static string Sha1(StringBuilder sb) => Convert.ToHexStringLower(SHA1.HashData(Mlod.Latin1.GetBytes(sb.ToString())));
    static string Sha1(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var p in parts) h.AppendData(p);
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    public static JsonNode Of(P3DModel model)
    {
        var lods = new JsonArray();
        for (int li = 0; li < model.Lods.Length; li++)
        {
            var lod = model.Lods[li];
            var pos = MemoryMarshal.Cast<float, uint>(lod.Positions.AsSpan());
            var pts = new StringBuilder();
            for (int i = 0; i < lod.PointCount; i++)
                pts.Append($"{Hex(pos[i * 3])},{Hex(pos[i * 3 + 1])},{Hex(pos[i * 3 + 2])};{lod.PointFlags[i]}\n");
            var nb = MemoryMarshal.Cast<float, uint>(lod.Normals.AsSpan());
            var nrm = new StringBuilder();
            for (int i = 0; i < lod.NormalCount; i++) nrm.Append($"{Hex(nb[i * 3])},{Hex(nb[i * 3 + 1])},{Hex(nb[i * 3 + 2])}\n");
            var uv = MemoryMarshal.Cast<float, uint>(lod.FaceUV.AsSpan());
            var faces = new StringBuilder();
            var hist = new SortedDictionary<string, int>();
            var tex = new SortedSet<string>(StringComparer.Ordinal);
            var mat = new SortedSet<string>(StringComparer.Ordinal);
            for (int f = 0; f < lod.FaceCount; f++)
            {
                int nv = lod.FaceVertCount[f];
                hist[nv.ToString()] = hist.GetValueOrDefault(nv.ToString()) + 1;
                string t = lod.Texture(f), m = lod.Material(f);
                tex.Add(t);
                mat.Add(m);
                var p = lod.FacePoints;
                var n = lod.FaceNormals;
                faces.Append($"{nv};{p[f * 4]},{p[f * 4 + 1]},{p[f * 4 + 2]},{p[f * 4 + 3]};{n[f * 4]},{n[f * 4 + 1]},{n[f * 4 + 2]},{n[f * 4 + 3]};");
                for (int k = 0; k < 8; k++) faces.Append(k == 0 ? "" : ",").Append(Hex(uv[f * 8 + k]));
                faces.Append($";{lod.FaceFlags[f]};{t};{m}\n");
            }
            var tags = new JsonArray();
            foreach (var t in lod.Tags)
            {
                var data = Mlod.SerializeTagData(t, lod);
                tags.Add(new JsonObject { ["name"] = t.DiskName, ["active"] = (int)t.Active, ["size"] = data.Length, ["sha1"] = Sha1(data) });
            }
            tags.Add(new JsonObject { ["name"] = TagNames.EndOfFile, ["active"] = 1, ["size"] = 0, ["sha1"] = Sha1() });
            var histObj = new JsonObject();
            foreach (var (k, v) in hist) histObj[k] = v;
            lods.Add(new JsonObject
            {
                ["index"] = li,
                ["resolution_bits"] = Hex(BitConverter.SingleToUInt32Bits(lod.Resolution)),
                ["major"] = lod.Major,
                ["minor"] = lod.Minor,
                ["header_flags"] = lod.HeaderFlags,
                ["num_points"] = lod.PointCount,
                ["num_normals"] = lod.NormalCount,
                ["num_faces"] = lod.FaceCount,
                ["points_sha1"] = Sha1(pts),
                ["normals_sha1"] = Sha1(nrm),
                ["faces_sha1"] = Sha1(faces),
                ["face_vert_hist"] = histObj,
                ["textures"] = new JsonArray(tex.Select(s => (JsonNode)s!).ToArray()),
                ["materials"] = new JsonArray(mat.Select(s => (JsonNode)s!).ToArray()),
                ["tags"] = tags,
                ["properties"] = new JsonArray(lod.Properties.Select(p => (JsonNode)new JsonArray(p.Key, p.Value)).ToArray()),
                ["selections"] = new JsonArray(lod.Selections.Select(s => (JsonNode)new JsonObject
                {
                    ["name"] = s.Name,
                    ["points_nonzero"] = s.Points.Count(b => b != 0),
                    ["faces_nonzero"] = s.Faces.Count(b => b != 0),
                    ["data_sha1"] = Sha1(s.Points, s.Faces),
                }).ToArray()),
                ["uvsets"] = new JsonArray(lod.UVSets.Select(u => (JsonNode)u.Stage).ToArray()),
            });
        }
        // Round-trip through text so values compare like parsed oracle JSON.
        return JsonNode.Parse(new JsonObject { ["version"] = model.Version, ["lods"] = lods }.ToJsonString())!;
    }
}
