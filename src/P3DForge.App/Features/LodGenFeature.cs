using System.Globalization;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>
/// LOD ▸ Generate LODs…: simplified resolution LODs and hull-based Shadow Volume / Geometry / Fire / View Geometry
/// from one visual LOD (<see cref="LodGenerator"/>), computed in the background and added in one undo step.
/// model.cfg bones get hulls of their own so animated parts keep moving collision.
/// </summary>
public sealed class LodGenFeature : IFeature
{
    static EditorState Ed => AppServices.Editor;
    static bool running;

    public int Order => 61;

    public void Install()
    {
        Commands.Register(new Command
        {
            Id = "lod.generate",
            Label = "_Generate LODs…",
            Execute = Generate,
            CanExecute = () => !running && Ed.Model.Lods.Any(l => LodNames.TypeOf(l.Resolution).Type.Category == LodCategory.Visual && l.FaceCount > 0),
            Description = "Makes simplified resolution LODs and a Shadow Volume, Geometry (with mass), Fire and View Geometry from convex hulls of a visual LOD",
        });
    }

    static async void Generate()
    {
        var model = Ed.Model;
        var visual = Enumerable.Range(0, model.Lods.Length).Where(i => LodNames.TypeOf(model.Lods[i].Resolution).Type.Category == LodCategory.Visual && model.Lods[i].FaceCount > 0).ToList();
        int source = visual.Contains(Ed.ActiveLod) ? Ed.ActiveLod : visual[0];
        bool Has(string id) => model.Lods.Any(l => LodNames.TypeOf(l.Resolution).Type.Id == id);
        double mass = model.Lods.FirstOrDefault(l => LodNames.IsGeometry(l.Resolution)) is { } g && Mass.Total(g) > 0 ? Mass.Total(g) : 10;
        var (skeleton, animated) = Services.DayZContext.Bones();
        var form = new Dialogs.Ops.Form("Generate LODs",
                "Builds LODs from one visual LOD. Resolution LODs are simplified copies (texture borders, UV seams and open edges stay in place); " +
                "the Shadow Volume and the collision LODs are convex hulls of the parts. Everything is added in one undo step.")
            .Choice("source", "From LOD", [.. visual.Select(i => $"{LodNames.Name(model.Lods[i].Resolution)} ({model.Lods[i].FaceCount:N0} faces)")], visual.IndexOf(source))
            .Text("ratios", "Resolution LODs (% of the faces)", "50, 25, 12", t => ParseRatios(t) is null ? "Percentages between 1 and 99, separated by commas (or empty)" : null)
            .Check("shadow", "Shadow Volume (closed, sharp edges)", !Has("shadowVolume"))
            .Check("geometry", "Geometry (collision, mass)", !Has("geometry"))
            .Check("fire", "Fire Geometry (bullets)", !Has("fireGeometry"))
            .Check("view", "View Geometry (sight)", false)
            .Number("mass", "Mass (kg)", mass, 0, 1e6)
            .Integer("hulls", "At most this many hulls", 10, 1, 64)
            .Number("minpart", "Leave out parts smaller than (% of the size)", 3, 0, 50)
            .Check("replace", "Replace existing LODs of these types / resolutions", false);
        if (animated.Count > 0) form.Check("bones", $"Own hulls for the {animated.Count} animated part{(animated.Count == 1 ? "" : "s")} of model.cfg ({string.Join(", ", animated.Take(4))}{(animated.Count > 4 ? ", …" : "")})", true);
        if (!form.Show("Generate")) return;
        var opts = new LodGenOptions(
            visual[form.Index("source")],
            ParseRatios(form.Str("ratios")) ?? [],
            form.Bool("shadow"), form.Bool("geometry"), form.Bool("fire"), form.Bool("view"),
            form.Num("mass"), form.Num("minpart") / 100, form.Bool("replace"), form.Int("hulls"),
            animated.Count > 0 && form.Bool("bones") ? animated : []);
        running = true;
        Ed.Log("Generating LODs…");
        try
        {
            var result = await Task.Run(() => LodGenerator.Generate(model, opts));
            if (!ReferenceEquals(Ed.Model, model))
            {
                Ed.Log("Generate LODs: the model changed meanwhile — nothing was added, run it again", LogLevel.Warn);
                return;
            }
            Ed.Edit("Generate LODs", _ => result.Model);
            foreach (var line in result.Report) Ed.Log("Generate LODs: " + line);
        }
        catch (Exception ex)
        {
            Dlg.Fail("Generate LODs: " + ex.Message);
        }
        finally
        {
            running = false;
        }
    }

    /// <summary>"50, 25, 12" → [0.5, 0.25, 0.12]; empty → []; null when invalid.</summary>
    static double[]? ParseRatios(string text)
    {
        var list = new List<double>();
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v is < 1 or > 99) return null;
            list.Add(v / 100);
        }
        return [.. list];
    }
}
