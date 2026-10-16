using System.Globalization;
using System.Numerics;
using System.Text;

namespace P3DForge.Core.Materials;

/*
 * ── API summary (P3DForge.Core.Materials) ── public members are frozen: only ADD from now on ──────────────────
 * Rvmat                     an .rvmat: typed accessors over a lossless config tree (Root). Unknown entries/classes
 *                           (Fresnel, plantWind, nextPass, ...) and their order survive Parse → ToText → Parse.
 *   Parse(text, path?) · FromBytes(bytes, path?) (text or binarized "\0raP") · Load(path) (throws on IO errors)
 *   ToText() (BI style, CRLF) · ToBytes() · ToRap() · Save(path, keepBackup) (atomic, optional .bak) · Clone()
 *   Path · WasBinarized · Warnings
 *   PixelShaderID · VertexShaderID · Ambient · Diffuse · ForcedDiffuse · Emmisive · Specular (Rgba) · SpecularPower
 *   RenderFlags (string[]) · HasRenderFlag(f) · SetRenderFlag(f, on) · SurfaceInfo
 *   Stages · Stage(n) · AddStage(n) · RemoveStage(n) · NextStageIndex · TexGens · TexGen(n) · AddTexGen(n)
 *   FindStageBySuffix("_nohq", ...) · FindStage(StageRole) · RoleOf(stage) · EffectiveUvTransform(stage) · EffectiveUvSource(stage)
 * RvmatStage                Class ("StageN" ConfigClass) · Name · Index · Texture · UvSource · UvTransform? · TexGen? · Filter
 *                           · Suffix · Role · EffectiveUvTransform · EffectiveUvSource · EffectiveUvSet
 * RvmatTexGen               Class ("TexGenN") · Name · Index · UvSource · UvTransform?
 * UvTransform               Aside/Up/Dir/Pos (Vector3) · Identity · IsIdentity · Apply(uv) · ToMatrix3x2() (Vector2.Transform(uv, m))
 *                           · FromComponents(scaleU, scaleV, rotDeg, offU, offV) · Decompose(...)
 *                           mapping: u' = u*Aside.X + v*Up.X + Pos.X ; v' = u*Aside.Y + v*Up.Y + Pos.Y
 * Rgba                      R G B A floats · White/Black/Zero · ToVector4()
 * ProceduralTexture         IsProcedural · TryParseColor("#(argb,8,8,3)color(r,g,b,a,TAG)") · TryParseFresnel("#(ai,..)fresnel(n,k)")
 * TextureNames              Suffix(texture) ("_nohq"; "_dt" from a procedural tag; "fresnel") · RoleOf(texture) · UvSetIndex(uvSource)
 * StageRole                 Unknown Diffuse Normal Detail Macro AmbientShadow Specular Fresnel Environment
 * RvmatShaders              known PixelShaderIDs / VertexShaderIDs / RenderFlags / UvSources / Filters (editor combos)
 * MaterialStore (MaterialStore.cs)  Shared · ProjectRoot / ProjectRootProvider · Resolve(path) · Key(path) · Get(path)
 *                           (override ?? cached file, null when missing/broken) · TryGet · GetError · LoadFromDisk · Version(key)
 *                           · SetOverride / ClearOverride / HasOverride / OverrideKeys · Invalidate · CheckForChanges · Changed(key)
 * Config tree               ConfigClass / ConfigProperty / ConfigDelete / ConfigValue · ConfigText.Parse/Write · Rap.Read/Write/IsRap
 * Rules: objects returned by MaterialStore are shared and read-only — Clone() before editing, then SetOverride(path, copy).
 * ────────────────────────────────────────────────────────────────────────────────────────────────────────────
 */

/// <summary>An RGBA colour as stored in rvmats (ambient[]={r,g,b,a}).</summary>
public readonly record struct Rgba(float R, float G, float B, float A)
{
    public static Rgba White => new(1, 1, 1, 1);
    public static Rgba Black => new(0, 0, 0, 1);
    public static Rgba Zero => new(0, 0, 0, 0);
    public Vector4 ToVector4() => new(R, G, B, A);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{R}, {G}, {B}, {A}");
}

/// <summary>Texture coordinate transform of a stage / TexGen (class uvTransform {aside, up, dir, pos}).</summary>
public readonly record struct UvTransform(Vector3 Aside, Vector3 Up, Vector3 Dir, Vector3 Pos)
{
    public static UvTransform Identity => new(Vector3.UnitX, Vector3.UnitY, Vector3.Zero, Vector3.Zero);

    /// <summary>True when the 2D mapping is the identity (dir is ignored: it only matters for 3D sources).</summary>
    public bool IsIdentity => Aside.X == 1 && Aside.Y == 0 && Up.X == 0 && Up.Y == 1 && Pos.X == 0 && Pos.Y == 0;

    /// <summary>u' = u*Aside.X + v*Up.X + Pos.X ; v' = u*Aside.Y + v*Up.Y + Pos.Y.</summary>
    public Vector2 Apply(Vector2 uv) =>
        new(uv.X * Aside.X + uv.Y * Up.X + Pos.X, uv.X * Aside.Y + uv.Y * Up.Y + Pos.Y);

    /// <summary>The 2D part as a matrix for <see cref="Vector2.Transform(Vector2, Matrix3x2)"/> (same result as <see cref="Apply"/>).</summary>
    public Matrix3x2 ToMatrix3x2() => new(Aside.X, Aside.Y, Up.X, Up.Y, Pos.X, Pos.Y);

    /// <summary>Scale, then rotate (degrees, counter-clockwise in UV space), then offset. dir = (0,0,0) like the BI templates.</summary>
    public static UvTransform FromComponents(float scaleU, float scaleV, float rotationDegrees, float offsetU, float offsetV, Vector3? dir = null)
    {
        var r = rotationDegrees * MathF.PI / 180f;
        var c = Clean(MathF.Cos(r));
        var s = Clean(MathF.Sin(r));
        return new(new(Clean(scaleU * c), Clean(scaleU * s), 0), new(Clean(-scaleV * s), Clean(scaleV * c), 0),
            dir ?? Vector3.Zero, new(offsetU, offsetV, 0));
    }

    /// <summary>Inverse of <see cref="FromComponents"/> (exact for scale/rotate/offset transforms; skew is dropped).</summary>
    public void Decompose(out float scaleU, out float scaleV, out float rotationDegrees, out float offsetU, out float offsetV)
    {
        scaleU = MathF.Sqrt(Aside.X * Aside.X + Aside.Y * Aside.Y);
        var r = scaleU > 1e-12f ? MathF.Atan2(Aside.Y, Aside.X) : MathF.Atan2(-Up.X, Up.Y);
        rotationDegrees = Clean(r * 180f / MathF.PI);
        var c = MathF.Cos(r);
        var s = MathF.Sin(r);
        scaleV = Clean(-Up.X * s + Up.Y * c);
        scaleU = Clean(scaleU);
        offsetU = Pos.X;
        offsetV = Pos.Y;
    }

    static float Clean(float v)
    {
        if (MathF.Abs(v) < 1e-6f) return 0;
        var r = MathF.Round(v);
        return MathF.Abs(v - r) < 1e-5f ? r : v;
    }

    internal static UvTransform? FromClass(ConfigClass? c)
    {
        if (c is null || c.IsExtern) return null;
        var id = Identity;
        return new UvTransform(Vec(c, "aside", id.Aside), Vec(c, "up", id.Up), Vec(c, "dir", id.Dir), Vec(c, "pos", id.Pos));
    }

    static Vector3 Vec(ConfigClass c, string name, Vector3 fallback)
    {
        var v = c.Get(name);
        if (v is null) return fallback;
        var f = v.AsFloats();
        return new(f.Length > 0 ? f[0] : fallback.X, f.Length > 1 ? f[1] : fallback.Y, f.Length > 2 ? f[2] : fallback.Z);
    }

    internal void WriteTo(ConfigClass c)
    {
        c.Set("aside", ConfigValue.Numbers(Aside.X, Aside.Y, Aside.Z));
        c.Set("up", ConfigValue.Numbers(Up.X, Up.Y, Up.Z));
        c.Set("dir", ConfigValue.Numbers(Dir.X, Dir.Y, Dir.Z));
        c.Set("pos", ConfigValue.Numbers(Pos.X, Pos.Y, Pos.Z));
    }
}

/// <summary>What a stage texture is for.</summary>
public enum StageRole
{
    Unknown,
    Diffuse,
    Normal,
    Detail,
    Macro,
    AmbientShadow,
    Specular,
    Fresnel,
    Environment,
}

/// <summary>Procedural textures ("#(argb,8,8,3)color(0.5,0.5,0.5,1,DT)", "#(ai,64,64,1)fresnel(1.36,0.001)").</summary>
public static class ProceduralTexture
{
    public static bool IsProcedural(string? texture) => texture is not null && texture.TrimStart().StartsWith('#');

    /// <summary>color(r,g,b[,a][,TAG]) → colour (alpha 1 when missing) and the tag (DT, MC, AS, NOHQ, SMDI, CO, ...).</summary>
    public static bool TryParseColor(string? texture, out Rgba color, out string? tag)
    {
        color = default;
        tag = null;
        if (!IsProcedural(texture)) return false;
        var args = Args(texture!, "color(");
        if (args is null) return false;
        var nums = new List<float>(4);
        foreach (var a in args)
        {
            var t = a.Trim();
            if (nums.Count < 4 && float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) nums.Add(f);
            else if (t.Length > 0 && tag is null) tag = t;
        }
        switch (nums.Count)
        {
            case 0:
                return false;
            case 1:
            case 2:
                color = new Rgba(nums[0], nums[0], nums[0], nums.Count == 2 ? nums[1] : 1);
                break;
            default:
                color = new Rgba(nums[0], nums[1], nums[2], nums.Count > 3 ? nums[3] : 1);
                break;
        }
        return true;
    }

    /// <summary>fresnel(n,k) → refraction index and extinction; fresnelGlass(n) → (n, 0).</summary>
    public static bool TryParseFresnel(string? texture, out float n, out float k)
    {
        n = k = 0;
        if (!IsProcedural(texture)) return false;
        var args = Args(texture!, "fresnel(") ?? Args(texture!, "fresnelGlass(");
        if (args is null || args.Length == 0) return false;
        if (!float.TryParse(args[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return false;
        if (args.Length > 1) float.TryParse(args[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out k);
        return true;
    }

    static string[]? Args(string texture, string func)
    {
        var i = texture.IndexOf(func, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var start = i + func.Length;
        var end = texture.IndexOf(')', start);
        if (end < 0) end = texture.Length;
        return texture[start..end].Split(',');
    }
}

/// <summary>Texture naming conventions (suffixes) and uvSource helpers.</summary>
public static class TextureNames
{
    /// <summary>Lower-case suffix of a texture: "_nohq" for "...\x_nohq.paa", "_dt" for "#(argb,8,8,3)color(0.5,0.5,0.5,1,DT)",
    /// "fresnel" for a fresnel() texture, "" when unknown.</summary>
    public static string Suffix(string? texture)
    {
        if (string.IsNullOrWhiteSpace(texture)) return "";
        var t = texture.Trim();
        if (t.StartsWith('#'))
        {
            if (t.Contains("fresnel", StringComparison.OrdinalIgnoreCase)) return "fresnel";
            return ProceduralTexture.TryParseColor(t, out _, out var tag) && !string.IsNullOrEmpty(tag) ? "_" + tag.ToLowerInvariant() : "";
        }
        var name = FileStem(t);
        var us = name.LastIndexOf('_');
        return us < 0 ? "" : name[us..].ToLowerInvariant();
    }

    /// <summary>File name without folder and extension.</summary>
    public static string FileStem(string path)
    {
        var slash = path.LastIndexOfAny(['\\', '/']);
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>Role of a texture by name: env_* → Environment, _nohq → Normal, _dt → Detail, _mc → Macro, _as/_ads → AmbientShadow,
    /// _smdi/_sm/_dtsmdi → Specular, fresnel → Fresnel, _co/_ca → Diffuse.</summary>
    public static StageRole RoleOf(string? texture)
    {
        if (string.IsNullOrWhiteSpace(texture)) return StageRole.Unknown;
        if (!ProceduralTexture.IsProcedural(texture) && FileStem(texture.Trim()).StartsWith("env_", StringComparison.OrdinalIgnoreCase))
            return StageRole.Environment;
        return Suffix(texture) switch
        {
            "_nohq" or "_no" or "_normal" or "_nmap" => StageRole.Normal,
            "_dt" or "_detail" or "_cdt" => StageRole.Detail,
            "_mc" => StageRole.Macro,
            "_as" or "_ads" => StageRole.AmbientShadow,
            "_smdi" or "_sm" or "_spec" or "_dtsmdi" => StageRole.Specular,
            "fresnel" => StageRole.Fresnel,
            "_co" or "_ca" => StageRole.Diffuse,
            "_env" => StageRole.Environment,
            _ => StageRole.Unknown,
        };
    }

    /// <summary>"tex" → 0, "tex1" → 1, "texN" → N; -1 for "none", positions/normals and anything else.</summary>
    public static int UvSetIndex(string? uvSource)
    {
        if (string.IsNullOrWhiteSpace(uvSource)) return 0;
        var s = uvSource.Trim();
        if (s.Equals("tex", StringComparison.OrdinalIgnoreCase)) return 0;
        if (s.Length > 3 && s.StartsWith("tex", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        return -1;
    }
}

/// <summary>Known values for the editor (free text stays allowed).</summary>
public static class RvmatShaders
{
    public static readonly string[] PixelShaders =
    [
        "Super", "Multi", "Skin", "SuperHair", "Glass", "Normal", "NormalDXTA", "NormalMap", "NormalMapDiffuse",
        "NormalMapSpecularMap", "NormalMapSpecularDIMap", "NormalMapDetailSpecularMap", "NormalMapDetailSpecularDIMap",
        "NormalMapMacroASSpecularDIMap", "NormalMapDetailMacroASSpecularDIMap", "NormalMapMacroAS", "NormalMapDiffuseMacroAS",
        "NormalMapThrough", "NormalMapSpecularThrough", "Detail", "DetailMacroAS", "AlphaShadow", "AlphaNoShadow",
        "White", "WhiteAlpha", "Tree", "TreeAdv", "TreeAdvTrunk", "Grass", "Water", "CalmWater", "Sprite", "NonTL",
    ];

    public static readonly string[] VertexShaders =
    [
        "Super", "Multi", "Skin", "Basic", "BasicAS", "NormalMap", "NormalMapAS", "NormalMapDiffuse", "NormalMapDiffuseAS",
        "NormalMapThrough", "NormalMapSpecularThrough", "Glass", "Tree", "TreeNoFade", "TreeAdv", "TreeAdvTrunk", "Grass",
        "Water", "CalmWater", "Sprite",
    ];

    public static readonly string[] RenderFlags =
    [
        "NoZWrite", "NoColorWrite", "NoAlphaWrite", "AddBlend", "AlphaTest32", "AlphaTest64", "AlphaTest128",
        "NoReceiveShadow", "AlwaysInShadow", "LandShadow", "NoTiWrite", "Road",
    ];

    public static readonly string[] UvSources = ["tex", "tex1", "none", "pos", "norm", "worldPos", "worldNorm", "texWaterAnim"];

    public static readonly string[] Filters = ["Default", "Point", "Linear", "Trilinear", "Anizotropic"];

    /// <summary>Super-family shaders: stage slots 1..7 = normal, detail, macro, ambient shadow, specular, fresnel, environment.</summary>
    public static bool IsSuperFamily(string? pixelShader) =>
        pixelShader is not null && (pixelShader.Equals("Super", StringComparison.OrdinalIgnoreCase) ||
                                    pixelShader.Equals("SuperExt", StringComparison.OrdinalIgnoreCase) ||
                                    pixelShader.Equals("SuperHair", StringComparison.OrdinalIgnoreCase) ||
                                    pixelShader.Equals("Skin", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A texture stage (class StageN) of an rvmat. A thin view: reads/writes go to <see cref="Class"/>.</summary>
public sealed class RvmatStage
{
    internal RvmatStage(Rvmat owner, ConfigClass cls)
    {
        Owner = owner;
        Class = cls;
    }

    public Rvmat Owner { get; }
    public ConfigClass Class { get; }
    public string Name => Class.Name;
    public int Index => Rvmat.IndexOf(Class.Name, "Stage");

    public string Texture
    {
        get => Class.GetString("texture") ?? "";
        set => Class.Set("texture", ConfigValue.Str(value ?? ""));
    }

    /// <summary>uvSource ("tex", "tex1", "none", ...); null when not set.</summary>
    public string? UvSource
    {
        get => Class.GetString("uvSource");
        set
        {
            if (value is null) Class.Remove("uvSource");
            else Class.Set("uvSource", ConfigValue.Str(value));
        }
    }

    /// <summary>The stage's own class uvTransform; null when it has none.</summary>
    public UvTransform? UvTransform
    {
        get => Materials.UvTransform.FromClass(Class.Class("uvTransform"));
        set
        {
            if (value is null) Class.Remove("uvTransform");
            else value.Value.WriteTo(Class.EnsureClass("uvTransform"));
        }
    }

    /// <summary>texGen="N" reference to class TexGenN; null when not set.</summary>
    public int? TexGen
    {
        get
        {
            var v = Class.Get("texGen");
            if (v is null) return null;
            var i = v.AsInt(int.MinValue);
            return i == int.MinValue ? null : i;
        }
        set
        {
            if (value is null)
            {
                Class.Remove("texGen");
                return;
            }
            var old = Class.Get("texGen");
            Class.Set("texGen", old is { IsNumber: true } ? ConfigValue.Int(value.Value)
                : ConfigValue.Str(value.Value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    public string? Filter
    {
        get => Class.GetString("Filter");
        set
        {
            if (string.IsNullOrEmpty(value)) Class.Remove("Filter");
            else Class.Set("Filter", ConfigValue.Str(value));
        }
    }

    public string Suffix => TextureNames.Suffix(Texture);
    public StageRole Role => Owner.RoleOf(this);
    public UvTransform EffectiveUvTransform => Owner.EffectiveUvTransform(this);
    public string EffectiveUvSource => Owner.EffectiveUvSource(this);

    /// <summary>UV set used by the stage (0 = tex, 1 = tex1, -1 = none/other).</summary>
    public int EffectiveUvSet => TextureNames.UvSetIndex(EffectiveUvSource);

    public override string ToString() => $"{Name}: {Texture}";
}

/// <summary>A texture coordinate generator (class TexGenN) referenced by stages through texGen="N".</summary>
public sealed class RvmatTexGen
{
    internal RvmatTexGen(ConfigClass cls) => Class = cls;

    public ConfigClass Class { get; }
    public string Name => Class.Name;
    public int Index => Rvmat.IndexOf(Class.Name, "TexGen");

    public string? UvSource
    {
        get => Class.GetString("uvSource");
        set
        {
            if (value is null) Class.Remove("uvSource");
            else Class.Set("uvSource", ConfigValue.Str(value));
        }
    }

    public UvTransform? UvTransform
    {
        get => Materials.UvTransform.FromClass(Class.Class("uvTransform"));
        set
        {
            if (value is null) Class.Remove("uvTransform");
            else value.Value.WriteTo(Class.EnsureClass("uvTransform"));
        }
    }
}

/// <summary>An .rvmat material. See the API summary at the top of this file.</summary>
public sealed class Rvmat
{
    public Rvmat() : this(new ConfigClass("")) { }

    public Rvmat(ConfigClass root) => Root = root;

    /// <summary>The whole document (root class, Name "").</summary>
    public ConfigClass Root { get; }

    /// <summary>File the material was loaded from / saved to (absolute), if any.</summary>
    public string? Path { get; set; }

    /// <summary>Loaded from a binarized (raP) file; saving writes text.</summary>
    public bool WasBinarized { get; init; }

    /// <summary>Text layout used by <see cref="ToText"/>: the source file's own (newline, "a = 1" spacing) when parsed
    /// from text, BI style otherwise.</summary>
    public ConfigTextStyle TextStyle { get; set; } = ConfigTextStyle.Bi;

    /// <summary>Parser warnings (skipped input, ignored #include, dropped comments).</summary>
    public List<string> Warnings { get; } = [];

    // ── IO ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static Rvmat Parse(string text, string? path = null)
    {
        var warnings = new List<string>();
        var root = ConfigText.Parse(text, warnings);
        var m = new Rvmat(root) { Path = path, TextStyle = ConfigTextStyle.Detect(text) };
        m.Warnings.AddRange(warnings);
        return m;
    }

    /// <summary>Text or binarized (raP) rvmat. Throws <see cref="InvalidDataException"/> for a corrupt raP.</summary>
    public static Rvmat FromBytes(byte[] data, string? path = null)
    {
        if (Rap.IsRap(data)) return new Rvmat(Rap.Read(data)) { Path = path, WasBinarized = true };
        return Parse(DecodeText(data), path);
    }

    public static Rvmat Load(string path) => FromBytes(File.ReadAllBytes(path), System.IO.Path.GetFullPath(path));

    static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    public string ToText() => ConfigText.Write(Root, TextStyle);
    public byte[] ToBytes() => new UTF8Encoding(false).GetBytes(ToText());
    public byte[] ToRap() => Rap.Write(Root);

    /// <summary>Writes the text form atomically (temp file + move); <paramref name="keepBackup"/> copies the old file to path.bak first.</summary>
    public void Save(string path, bool keepBackup = false)
    {
        var full = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = full + ".tmp";
        File.WriteAllBytes(tmp, ToBytes());
        if (keepBackup && File.Exists(full)) File.Copy(full, full + ".bak", overwrite: true);
        File.Move(tmp, full, overwrite: true);
        Path = full;
    }

    public Rvmat Clone()
    {
        var m = new Rvmat(Root.DeepClone()) { Path = Path, WasBinarized = WasBinarized, TextStyle = TextStyle };
        m.Warnings.AddRange(Warnings);
        return m;
    }

    /// <summary>Same content (strict: same spelling of numbers).</summary>
    public bool ContentEquals(Rvmat? other) => other is not null && Root.DeepEquals(other.Root);

    // ── header ──────────────────────────────────────────────────────────────────────────────────────────────

    public string PixelShaderID
    {
        get => Root.GetString("PixelShaderID") ?? "";
        set => Root.Set("PixelShaderID", ConfigValue.Str(value ?? ""));
    }

    public string VertexShaderID
    {
        get => Root.GetString("VertexShaderID") ?? "";
        set => Root.Set("VertexShaderID", ConfigValue.Str(value ?? ""));
    }

    public Rgba Ambient
    {
        get => GetColor("ambient", Rgba.White);
        set => SetColor("ambient", value);
    }

    public Rgba Diffuse
    {
        get => GetColor("diffuse", Rgba.White);
        set => SetColor("diffuse", value);
    }

    public Rgba ForcedDiffuse
    {
        get => GetColor("forcedDiffuse", Rgba.Zero);
        set => SetColor("forcedDiffuse", value);
    }

    /// <summary>emmisive[] (BI spelling; "emissive" is read as a fallback).</summary>
    public Rgba Emmisive
    {
        get => Root.Property("emmisive") is null && Root.Property("emissive") is not null
            ? GetColor("emissive", Rgba.Black)
            : GetColor("emmisive", Rgba.Black);
        set => SetColor("emmisive", value);
    }

    public Rgba Specular
    {
        get => GetColor("specular", Rgba.Zero);
        set => SetColor("specular", value);
    }

    public float SpecularPower
    {
        get => (float)(Root.GetNumber("specularPower") ?? 0);
        set => Root.Set("specularPower", ConfigValue.Num(value));
    }

    public string? SurfaceInfo
    {
        get => Root.GetString("surfaceInfo");
        set
        {
            if (string.IsNullOrEmpty(value)) Root.Remove("surfaceInfo");
            else Root.Set("surfaceInfo", ConfigValue.Str(value));
        }
    }

    /// <summary>renderFlags[] (any spelling of the key). Setting an empty list removes the entry.</summary>
    public string[] RenderFlags
    {
        get
        {
            var v = Root.Get("renderFlags");
            if (v is null) return [];
            if (!v.IsArray) return v.AsString().Length > 0 ? [v.AsString()] : [];
            var r = new string[v.Items.Count];
            for (int i = 0; i < r.Length; i++) r[i] = v.Items[i].AsString();
            return r;
        }
        set
        {
            if (value is null || value.Length == 0) Root.Remove("renderFlags");
            else
            {
                var p = Root.Property("renderFlags");
                if (p is not null)
                {
                    p.Value = ConfigValue.Strings(value);
                    p.ArrayOp = 0;
                }
                else Root.Set("renderFlags", ConfigValue.Strings(value));
            }
        }
    }

    public bool HasRenderFlag(string flag)
    {
        foreach (var f in RenderFlags)
            if (f.Equals(flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public void SetRenderFlag(string flag, bool on)
    {
        var list = RenderFlags.ToList();
        var had = list.RemoveAll(f => f.Equals(flag, StringComparison.OrdinalIgnoreCase)) > 0;
        if (on) list.Add(flag);
        if (on || had) RenderFlags = [.. list];
    }

    Rgba GetColor(string name, Rgba fallback)
    {
        var v = Root.Get(name);
        if (v is null) return fallback;
        var f = v.AsFloats();
        return f.Length switch
        {
            0 => fallback,
            1 or 2 => new Rgba(f[0], f[0], f[0], f.Length == 2 ? f[1] : 1),
            3 => new Rgba(f[0], f[1], f[2], 1),
            _ => new Rgba(f[0], f[1], f[2], f[3]),
        };
    }

    void SetColor(string name, Rgba c)
    {
        var p = Root.Property(name);
        var v = ConfigValue.Numbers(c.R, c.G, c.B, c.A);
        if (p is not null)
        {
            p.Value = v;
            p.ArrayOp = 0;
        }
        else Root.Set(name, v);
    }

    // ── stages / texgens ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>N of a class named prefix+N ("Stage5" → 5), -1 otherwise.</summary>
    public static int IndexOf(string name, string prefix) =>
        name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var i)
            ? i
            : -1;

    /// <summary>Stage classes in document order.</summary>
    public IReadOnlyList<RvmatStage> Stages
    {
        get
        {
            var r = new List<RvmatStage>();
            foreach (var e in Root.Entries)
                if (e is ConfigClass { IsExtern: false } c && IndexOf(c.Name, "Stage") >= 0) r.Add(new RvmatStage(this, c));
            return r;
        }
    }

    public RvmatStage? Stage(int index)
    {
        foreach (var e in Root.Entries)
            if (e is ConfigClass { IsExtern: false } c && IndexOf(c.Name, "Stage") == index) return new RvmatStage(this, c);
        return null;
    }

    /// <summary>One more than the highest stage number (1 when there is none).</summary>
    public int NextStageIndex
    {
        get
        {
            var max = 0;
            foreach (var s in Stages) max = Math.Max(max, s.Index);
            return max + 1;
        }
    }

    /// <summary>The stage with this number; created after the last stage when missing
    /// (texture "", uvSource "tex", identity uvTransform).</summary>
    public RvmatStage AddStage(int index, string texture = "")
    {
        if (Stage(index) is { } existing) return existing;
        var c = new ConfigClass($"Stage{index}");
        c.Entries.Add(new ConfigProperty("texture", ConfigValue.Str(texture)));
        c.Entries.Add(new ConfigProperty("uvSource", ConfigValue.Str("tex")));
        Materials.UvTransform.Identity.WriteTo(c.EnsureClass("uvTransform"));
        var last = Root.Entries.FindLastIndex(e => e is ConfigClass sc && IndexOf(sc.Name, "Stage") >= 0);
        if (last < 0) Root.Entries.Add(c);
        else Root.Entries.Insert(last + 1, c);
        return new RvmatStage(this, c);
    }

    public bool RemoveStage(int index) =>
        Root.Entries.RemoveAll(e => e is ConfigClass c && IndexOf(c.Name, "Stage") == index) > 0;

    public IReadOnlyList<RvmatTexGen> TexGens
    {
        get
        {
            var r = new List<RvmatTexGen>();
            foreach (var e in Root.Entries)
                if (e is ConfigClass { IsExtern: false } c && IndexOf(c.Name, "TexGen") >= 0) r.Add(new RvmatTexGen(c));
            return r;
        }
    }

    public RvmatTexGen? TexGen(int index)
    {
        foreach (var e in Root.Entries)
            if (e is ConfigClass { IsExtern: false } c && IndexOf(c.Name, "TexGen") == index) return new RvmatTexGen(c);
        return null;
    }

    /// <summary>The TexGen with this number; created (uvSource "tex", identity) before the first stage when missing.</summary>
    public RvmatTexGen AddTexGen(int index)
    {
        if (TexGen(index) is { } existing) return existing;
        var c = new ConfigClass($"TexGen{index}");
        c.Entries.Add(new ConfigProperty("uvSource", ConfigValue.Str("tex")));
        Materials.UvTransform.Identity.WriteTo(c.EnsureClass("uvTransform"));
        var lastTg = Root.Entries.FindLastIndex(e => e is ConfigClass tc && IndexOf(tc.Name, "TexGen") >= 0);
        var firstStage = Root.Entries.FindIndex(e => e is ConfigClass sc && IndexOf(sc.Name, "Stage") >= 0);
        var at = lastTg >= 0 ? lastTg + 1 : firstStage >= 0 ? firstStage : Root.Entries.Count;
        Root.Entries.Insert(at, c);
        return new RvmatTexGen(c);
    }

    /// <summary>First stage whose texture suffix is one of <paramref name="suffixes"/> ("_nohq", "_smdi", "_as", "_mc", "_dt", "fresnel", ...;
    /// procedural textures match by tag: color(...,NOHQ) → "_nohq").</summary>
    public RvmatStage? FindStageBySuffix(params string[] suffixes)
    {
        foreach (var s in Stages)
        {
            var suf = s.Suffix;
            foreach (var want in suffixes)
            {
                var w = want.StartsWith('_') || want.Equals("fresnel", StringComparison.OrdinalIgnoreCase) ? want : "_" + want;
                if (suf.Equals(w, StringComparison.OrdinalIgnoreCase)) return s;
            }
        }
        return null;
    }

    /// <summary>Role of a stage: Super-family shaders by slot (1 normal, 2 detail, 3 macro, 4 AS, 5 specular, 6 fresnel, 7 env),
    /// other shaders (Multi, NormalMap..., unknown slots) by texture name.</summary>
    public StageRole RoleOf(RvmatStage stage)
    {
        if (RvmatShaders.IsSuperFamily(PixelShaderID))
        {
            var bySlot = stage.Index switch
            {
                0 => StageRole.Diffuse,
                1 => StageRole.Normal,
                2 => StageRole.Detail,
                3 => StageRole.Macro,
                4 => StageRole.AmbientShadow,
                5 => StageRole.Specular,
                6 => StageRole.Fresnel,
                7 => StageRole.Environment,
                _ => StageRole.Unknown,
            };
            if (bySlot != StageRole.Unknown) return bySlot;
        }
        return TextureNames.RoleOf(stage.Texture);
    }

    /// <summary>First stage with this role (see <see cref="RoleOf"/>).</summary>
    public RvmatStage? FindStage(StageRole role)
    {
        foreach (var s in Stages)
            if (RoleOf(s) == role) return s;
        return null;
    }

    /// <summary>uvTransform applied to the stage: its TexGen's (texGen="N") when it references one, else its own; identity when none.</summary>
    public UvTransform EffectiveUvTransform(RvmatStage stage)
    {
        if (stage.TexGen is int g && TexGen(g) is { } tg) return tg.UvTransform ?? Materials.UvTransform.Identity;
        return stage.UvTransform ?? Materials.UvTransform.Identity;
    }

    /// <summary>uvSource of the stage: its TexGen's when it references one, else its own; "tex" when none.</summary>
    public string EffectiveUvSource(RvmatStage stage)
    {
        if (stage.TexGen is int g && TexGen(g) is { } tg && tg.UvSource is { } src) return src;
        return stage.UvSource ?? "tex";
    }

    public override string ToString() => $"Rvmat {Path} ({PixelShaderID}, {Stages.Count} stages)";
}
