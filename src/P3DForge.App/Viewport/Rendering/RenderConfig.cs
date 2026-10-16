using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using P3DForge.App.Editor;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>A lighting preset ("Buldozer", "In-game", "Inventory", "Headlight").</summary>
public sealed record LightPreset(string Id, string Label, float Azimuth, float Elevation, string SunColor, float Intensity,
    string SkyColor, string GroundColor, float Ambient, float Fill, bool CameraRelative, float Exposure);

/// <summary>
/// Render options as editor UI flags (so commands, the Render panel, automation and other features share them),
/// persisted in <c>Settings.Extra["render.options"]</c>. Converts them into per-frame light / shadow / Final params.
/// </summary>
public static class RenderConfig
{
    static EditorState Ed => AppServices.Editor;

    // ---- flag keys ----
    public const string Preset = "view.light.preset";
    public const string Azimuth = "view.light.azimuth";
    public const string Elevation = "view.light.elevation";
    public const string SunColor = "view.light.sunColor";
    public const string Intensity = "view.light.intensity";
    public const string SkyColor = "view.light.skyColor";
    public const string GroundColor = "view.light.groundColor";
    public const string Ambient = "view.light.ambient";
    public const string Specular = "view.light.specular";
    public const string FinalDiffuse = "view.final.diffuse";
    public const string FinalSpecular = "view.final.specular";
    public const string FinalAmbient = "view.final.ambient";
    public const string FinalEnv = "view.final.env";
    public const string FinalEmissive = "view.final.emissive";
    public const string FinalShadows = "view.final.shadows";
    public const string FinalWire = "view.final.wire";
    public const string FinalGrid = "view.final.grid";
    public const string FinalPoints = "view.final.points";      // vertices in Final (default off; memory LODs always)
    public const string FinalBackFaces = "view.final.backFaces"; // "auto" | "show" | "cull"
    public const string FinalStage = "view.final.stage";         // "final" | "co" | "nohq" | "mc" | "as" | "dt" | "smdi" | "mask" | "fresnel" | "env"
    public const string FinalFlipGreen = "view.final.flipGreen";
    public const string ShadowAllModes = "view.shadow.allModes";
    public const string ShadowSoft = "view.shadow.soft";
    public const string ShadowSize = "view.shadow.size";
    public const string ShadowStrength = "view.shadow.strength";
    public const string Exposure = "view.post.exposure";          // EV bias
    public const string AutoExposure = "view.post.autoExposure";
    public const string Fov = "view.fov";                        // "default" | "game"
    public const string Stats = "view.stats";
    public const string StatsContinuous = "view.stats.continuous";

    public static readonly string[] Stages = ["final", "co", "nohq", "mc", "as", "dt", "smdi", "mask", "fresnel", "env"];
    public static readonly string[] StageLabels = ["Final", "_co (diffuse)", "_nohq (normal)", "_mc (macro)", "_as (ambient shadow)", "_dt (detail)", "_smdi (specular)", "_mask", "Fresnel", "Environment"];

    public static readonly LightPreset[] Presets =
    [
        new("buldozer", "Buldozer", 135, 40, "#FFF4E5", 1.0f, "#8E9BB0", "#5C564E", 1.0f, 0.18f, false, 0f),
        new("ingame", "In-game", 160, 28, "#FFE6C4", 1.25f, "#7C90B4", "#4A4237", 0.9f, 0f, false, 0f),
        new("inventory", "Inventory", -35, 35, "#FFFFFF", 1.0f, "#A0A4AC", "#6A6660", 1.1f, 0.12f, true, 0.2f),
        new("headlight", "Headlight (classic)", 0, 90, "#FFFFFF", 0f, "#666666", "#383838", 1.0f, 0.68f, true, 0f),
    ];

    /// <summary>Game FOV: DayZ / Arma camera "initFov" 0.75 rad (≈ 43° vertical).</summary>
    public const float GameFovY = 0.75f;
    public const float DefaultFovY = MathF.PI / 4;

    /// <summary>Every persisted flag with its default.</summary>
    public static readonly (string Key, object Default)[] Persisted =
    [
        (Preset, "buldozer"), (Azimuth, 135.0), (Elevation, 40.0), (SunColor, "#FFF4E5"), (Intensity, 1.0),
        (SkyColor, "#8E9BB0"), (GroundColor, "#5C564E"), (Ambient, 1.0), (Specular, 0.18),
        (FinalDiffuse, true), (FinalSpecular, true), (FinalAmbient, true), (FinalEnv, true), (FinalEmissive, true),
        (FinalShadows, true), (FinalWire, false), (FinalGrid, false), (FinalPoints, false), (FinalBackFaces, "auto"), (FinalStage, "final"), (FinalFlipGreen, false),
        (ShadowAllModes, false), (ShadowSoft, true), (ShadowSize, 2048.0), (ShadowStrength, 0.85),
        (Exposure, 0.0), (AutoExposure, false), (Fov, "default"), (Stats, false),
    ];

    const string SettingsKey = "render.options";

    // ---- typed flag readers (automation passes numbers as double) ----
    public static bool Bool(string key) => Ed.Flag<object?>(key, null) is bool b ? b : DefaultOf(key) is bool d && d;

    public static float Float(string key)
    {
        object? v = Ed.Flag<object?>(key, null) ?? DefaultOf(key);
        return v switch
        {
            double d => (float)d,
            float f => f,
            int i => i,
            long l => l,
            string s when float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) => x,
            _ => 0f,
        };
    }

    public static string Str(string key) => Ed.Flag<object?>(key, null) as string ?? DefaultOf(key) as string ?? "";

    static object? DefaultOf(string key)
    {
        foreach (var (k, d) in Persisted)
            if (k == key) return d;
        return null;
    }

    public static LightPreset CurrentPreset
    {
        get
        {
            var id = Str(Preset);
            foreach (var p in Presets)
                if (p.Id == id) return p;
            return Presets[0];
        }
    }

    /// <summary>Applies a preset: writes its sun, colours and ambient into the flags.</summary>
    public static void ApplyPreset(string id)
    {
        var p = Array.Find(Presets, x => x.Id == id) ?? Presets[0];
        Ed.SetFlag(Preset, p.Id);
        Ed.SetFlag(Azimuth, (double)p.Azimuth);
        Ed.SetFlag(Elevation, (double)p.Elevation);
        Ed.SetFlag(SunColor, p.SunColor);
        Ed.SetFlag(Intensity, (double)p.Intensity);
        Ed.SetFlag(SkyColor, p.SkyColor);
        Ed.SetFlag(GroundColor, p.GroundColor);
        Ed.SetFlag(Ambient, (double)p.Ambient);
        Ed.SetFlag(Exposure, (double)p.Exposure);
    }

    public static Vector3 ParseColor(string s, Vector3 fallback)
    {
        s = s.Trim().TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            return new Vector3(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
        return fallback;
    }

    public static string FormatColor(Vector3 c)
    {
        static int B(float x) => Math.Clamp((int)MathF.Round(x * 255f), 0, 255);
        return $"#{B(c.X):X2}{B(c.Y):X2}{B(c.Z):X2}";
    }

    static Vector3 ToLinear(Vector3 c) => new(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f));

    /// <summary>
    /// Direction towards the sun for a camera (RENDER). Camera-relative presets (Inventory / Headlight) rotate with
    /// the view; the others are fixed in the world. <paramref name="azimuth"/>/<paramref name="elevation"/> override
    /// the flags (live sun drag).
    /// </summary>
    public static Vector3 SunDirection(Vector3 camForward, Vector3 camUp, float? azimuth = null, float? elevation = null)
    {
        float az = azimuth ?? Float(Azimuth), el = elevation ?? Float(Elevation);
        return CurrentPreset.CameraRelative
            ? RenderMath.CameraLightDirection(az, el, camForward, camUp)
            : RenderMath.SunDirection(az, el);
    }

    /// <summary>Light of the editing modes (display-space values, readable rather than physical).</summary>
    public static LightParams EditLight(Vector3 sunDir)
    {
        var p = CurrentPreset;
        float intensity = Float(Intensity), amb = Float(Ambient);
        var sun = ParseColor(Str(SunColor), Vector3.One) * intensity * 0.58f;
        var sky = ParseColor(Str(SkyColor), new(0.55f)) * amb * 0.62f;
        var ground = ParseColor(Str(GroundColor), new(0.35f)) * amb * 0.55f;
        if (p.Id == "headlight") return LightParams.Headlight with { SunDir = sunDir, Specular = 0.12f * Float(Specular) / 0.18f };
        return new LightParams(sunDir, sun, sky, ground, p.Fill, Float(Specular));
    }

    /// <summary>Light of the Final mode (linear HDR values; exposure is applied in the shader).</summary>
    public static LightParams FinalLight(Vector3 sunDir)
    {
        var p = CurrentPreset;
        float intensity = Float(Intensity), amb = Float(Ambient);
        var sun = ToLinear(ParseColor(Str(SunColor), Vector3.One)) * intensity * 2.4f;
        var sky = ToLinear(ParseColor(Str(SkyColor), new(0.55f))) * amb * 1.9f; // sun : shade ≈ 6 : 1 (game-like, readable)
        var ground = ToLinear(ParseColor(Str(GroundColor), new(0.35f))) * amb * 1.5f;
        if (p.Id == "headlight") sun = new Vector3(1.6f);
        return new LightParams(sunDir, sun, sky, ground, p.Id == "headlight" ? 0.25f : 0f, 0f);
    }

    public static FinalParams Final()
    {
        int t = 0;
        if (Bool(FinalDiffuse)) t |= FinalParams.Diffuse;
        if (Bool(FinalSpecular)) t |= FinalParams.Specular;
        if (Bool(FinalAmbient)) t |= FinalParams.Ambient;
        if (Bool(FinalEnv)) t |= FinalParams.Env;
        if (Bool(FinalEmissive)) t |= FinalParams.Emissive;
        if (Bool(FinalShadows)) t |= FinalParams.Shadows;
        int stage = Math.Max(0, Array.IndexOf(Stages, Str(FinalStage)));
        int back = Str(FinalBackFaces) switch { "show" => 1, "cull" => 2, _ => 0 };
        return new FinalParams(t, stage, RenderMath.ExposureScale(Float(Exposure)), Bool(FinalFlipGreen), back);
    }

    /// <summary>Shadow map size clamped to 1024–4096 (power of two).</summary>
    public static int ShadowMapSize
    {
        get
        {
            float s = Float(ShadowSize);
            return s >= 3072 ? 4096 : s >= 1536 ? 2048 : 1024;
        }
    }

    // ---- persistence ----
    static string lastSaved = "";

    /// <summary>Restores the persisted flags (call once at startup, before the first frame).</summary>
    public static void Load()
    {
        try
        {
            if (AppServices.Settings.Extra.TryGetValue(SettingsKey, out var node) && node is JsonObject o)
            {
                foreach (var (key, def) in Persisted)
                {
                    if (o[key] is not JsonValue v) continue;
                    object? val = def switch
                    {
                        bool => v.TryGetValue(out bool b) ? b : null,
                        double => v.TryGetValue(out double d) ? d : null,
                        string => v.TryGetValue(out string? s) ? s : null,
                        _ => null,
                    };
                    if (val is not null) Ed.SetFlag(key, val);
                }
            }
        }
        catch
        {
            // corrupt settings → defaults
        }
        lastSaved = Snapshot().ToJsonString();
    }

    static JsonObject Snapshot()
    {
        var o = new JsonObject();
        foreach (var (key, def) in Persisted)
        {
            o[key] = def switch
            {
                bool => JsonValue.Create(Bool(key)),
                double => JsonValue.Create(Math.Round(Float(key), 4)),
                _ => JsonValue.Create(Str(key)),
            };
        }
        return o;
    }

    /// <summary>Saves the flags when they changed since the last save (cheap; call on UI changes).</summary>
    public static void SaveIfChanged()
    {
        var snap = Snapshot();
        var json = snap.ToJsonString();
        if (json == lastSaved) return;
        lastSaved = json;
        AppServices.Settings.Extra[SettingsKey] = snap;
        AppServices.Settings.Save();
    }
}
