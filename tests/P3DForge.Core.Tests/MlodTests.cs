using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using P3DForge.Core.IO;
using P3DForge.Core.Model;

namespace P3DForge.Core.Tests;

public class MlodTests(ITestOutputHelper output)
{
    static int FirstDiff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        int i = a.AsSpan(0, n).CommonPrefixLength(b.AsSpan(0, n));
        return i < n ? i : a.Length == b.Length ? -1 : n;
    }

    [Fact]
    public void Weights_encode_decode_like_object_builder()
    {
        Assert.Equal(0f, Weights.Decode(0));
        Assert.Equal(1f, Weights.Decode(1));
        Assert.Equal(1 / 255f, Weights.Decode(255), 5);
        for (int b = 0; b < 256; b++) Assert.Equal((byte)b, Weights.Encode(Weights.Decode((byte)b)));
    }

    [Fact]
    public void Lod_names_match_object_builder()
    {
        Assert.Equal("1.000", LodNames.Name(1));
        Assert.Equal("Geometry", LodNames.Name(1e13f));
        Assert.Equal("Memory", LodNames.Name(1e15f));
        Assert.Equal("Fire Geometry", LodNames.Name(7e15f));
        Assert.Equal("View - Cargo 1.000", LodNames.Name(1201));
        Assert.Equal("ShadowVolume 0.000", LodNames.Name(10000));
        Assert.Equal("viewGeometry", LodNames.TypeOf(LodNames.ResolutionFor("viewGeometry")).Type.Id);
    }

    [Fact]
    public void Empty_model_round_trips()
    {
        var m = new P3DModel([Lod.Empty(1), Lod.Empty(1e15f)]);
        var bytes = Mlod.Write(m);
        var back = Mlod.Read(bytes);
        Assert.Equal(2, back.Lods.Length);
        Assert.Equal(bytes, Mlod.Write(back));
    }

    public static TheoryData<string> SyntheticFiles()
    {
        var data = new TheoryData<string>();
        var manifest = Path.Combine(TestData.SyntheticDir, "manifest.json");
        // testdata is not in the repository: one row that skips (a theory without rows would fail)
        if (!File.Exists(manifest))
        {
            data.Add("manifest.json");
            return data;
        }
        foreach (var e in JsonNode.Parse(File.ReadAllText(manifest))!.AsArray()) data.Add(e!["file"]!.GetValue<string>());
        return data;
    }

    [Theory]
    [MemberData(nameof(SyntheticFiles))]
    public void Synthetic_fixture_round_trips_byte_exact(string file)
    {
        var bytes = File.ReadAllBytes(TestData.Need(Path.Combine(TestData.SyntheticDir, file)));
        var warnings = new List<string>();
        var outBytes = Mlod.Write(Mlod.Read(bytes, warnings));
        Assert.True(FirstDiff(bytes, outBytes) == -1, $"{file}: first difference at {FirstDiff(bytes, outBytes)}; warnings: {string.Join("; ", warnings)}");
    }

    [Theory]
    [MemberData(nameof(SyntheticFiles))]
    public void Synthetic_fixture_summary_matches_oracle(string file)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(TestData.Need(Path.Combine(TestData.SyntheticDir, "manifest.json"))))!.AsArray();
        var entry = manifest.First(e => e!["file"]!.GetValue<string>() == file)!;
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(TestData.SyntheticDir, entry["summary"]!.GetValue<string>())));
        var actual = Summary.Of(Mlod.Read(File.ReadAllBytes(TestData.Need(Path.Combine(TestData.SyntheticDir, file)))));
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{file}: summary differs from the oracle");
    }

    [Fact]
    public void Corpus_round_trips_byte_exact()
    {
        var corpus = TestData.Corpus();
        Assert.SkipWhen(corpus.Count == 0, "P:\\ corpus not available");
        long total = 0;
        double readMs = 0, writeMs = 0;
        var failures = new List<string>();
        foreach (var path in corpus)
        {
            var bytes = File.ReadAllBytes(path);
            total += bytes.Length;
            var warnings = new List<string>();
            var sw = Stopwatch.StartNew();
            var model = Mlod.Read(bytes, warnings);
            readMs += sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var outBytes = Mlod.Write(model);
            writeMs += sw.Elapsed.TotalMilliseconds;
            int d = FirstDiff(bytes, outBytes);
            if (d != -1 || warnings.Count > 0) failures.Add($"{path}: diff@{d} warnings={string.Join("; ", warnings)}");
        }
        output.WriteLine($"corpus: {corpus.Count} files, {total / 1e6:F1} MB, read {readMs:F0} ms, write {writeMs:F0} ms");
        Assert.Empty(failures);
    }

    [Fact]
    public void Corpus_summary_matches_python_oracle()
    {
        var corpus = TestData.Corpus();
        var oracle = TestData.Path_("tools", "oracle", "mlod_oracle.py");
        Assert.SkipWhen(corpus.Count == 0 || !File.Exists(oracle), "corpus or oracle not available");
        var mismatches = new List<string>();
        foreach (var path in corpus)
        {
            var psi = new ProcessStartInfo("python", ["-X", "utf8", oracle, "summary", path]) { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string json = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var expected = JsonNode.Parse(json);
            var actual = Summary.Of(Mlod.ReadFile(path));
            if (!JsonNode.DeepEquals(expected, actual)) mismatches.Add(path);
        }
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Odol_files_are_rejected_with_a_clear_message()
    {
        var bytes = "ODOL"u8.ToArray().Concat(new byte[64]).ToArray();
        var ex = Assert.Throws<MlodException>(() => Mlod.Read(bytes));
        Assert.Contains("ODOL", ex.Message);
    }
}
