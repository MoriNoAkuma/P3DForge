using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using P3DForge.App.Editor;

namespace P3DForge.App.Services;

/// <summary>A binarized model opened through a converter or the built-in reader (not yet saved as a source model).</summary>
/// <param name="Original">The ODOL file that was opened (never changed).</param>
/// <param name="ModelCfg">model.cfg the converter recovered (skeleton, animations), or null.</param>
/// <param name="ModelName">The CfgModels class name of the original (its file name).</param>
/// <param name="Editable">Converted to MLOD (named selections etc.); false for the built-in view.</param>
public sealed record ConvertedModel(string Original, string? ModelCfg, string ModelName, bool Editable);

/// <summary>
/// An external ODOL → MLOD converter (DeODOL55UNI.exe: "tool.exe file.p3d" writes file_mlod.p3d and model.cfg next to
/// the file). It runs on a copy in a cache folder, so the original and any model.cfg beside it are never touched.
/// The tool is found in File ▸ Preferences, else as DeODOL55UNI.exe in the work drive's root.
/// </summary>
public static class Deodol
{
    public const string SettingKey = "tools.deodol";
    public const string DefaultName = "DeODOL55UNI.exe";

    /// <summary>The converter to use, or null when none is configured / found.</summary>
    public static string? ToolPath()
    {
        var configured = AppServices.Settings.Get<string?>(SettingKey, null);
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var guess = Path.Combine(AppServices.Settings.ProjectRoot, DefaultName);
        return File.Exists(guess) ? guess : null;
    }

    /// <summary>Converts a copy of <paramref name="odol"/>; returns the MLOD file and the recovered model.cfg (if any). Throws on failure.</summary>
    public static (string Mlod, string? ModelCfg, string Log) Convert(string tool, string odol)
    {
        var name = Path.GetFileNameWithoutExtension(odol);
        var hash = Convert8(Path.GetFullPath(odol).ToLowerInvariant());
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "P3DForge", "deodol", $"{name}_{hash}");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, Path.GetFileName(odol));
        File.Copy(odol, copy);
        var psi = new ProcessStartInfo(tool)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(copy);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{tool} did not start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
            throw new TimeoutException($"{Path.GetFileName(tool)} did not finish in 3 minutes");
        }
        var log = (stdout.Result + stderr.Result).Trim();
        var mlod = Path.Combine(dir, name + "_mlod.p3d");
        if (!File.Exists(mlod))
            mlod = Directory.EnumerateFiles(dir, "*.p3d").FirstOrDefault(f => !string.Equals(f, copy, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidDataException($"{Path.GetFileName(tool)} wrote no MLOD (exit code {p.ExitCode}):\n{Last(log, 600)}");
        var cfg = Path.Combine(dir, "model.cfg");
        return (mlod, File.Exists(cfg) ? cfg : null, log);
    }

    static string Convert8(string s) => System.Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();

    static string Last(string s, int n) => s.Length <= n ? s : "…" + s[^n..];
}
