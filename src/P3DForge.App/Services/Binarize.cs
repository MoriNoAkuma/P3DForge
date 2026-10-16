using System.IO;
using Microsoft.Win32;

namespace P3DForge.App.Services;

/// <summary>DayZ Tools binarize.exe: where it is (File ▸ Preferences ▸ Tools, else the DayZ Tools installation).</summary>
public static partial class Binarize
{
    public const string SettingKey = "tools.binarize";

    /// <summary>binarize.exe to use, or null when none is configured / installed.</summary>
    public static string? ToolPath()
    {
        var configured = AppServices.Settings.Get<string?>(SettingKey, null);
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        return Installed();
    }

    /// <summary>binarize.exe of the installed DayZ Tools (registry "Bohemia Interactive\Dayz Tools" path), or null.</summary>
    public static string? Installed()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var key in new[] { @"Software\Bohemia Interactive\Dayz Tools", @"SOFTWARE\WOW6432Node\Bohemia Interactive\Dayz Tools" })
            {
                try
                {
                    using var k = root.OpenSubKey(key);
                    if (k?.GetValue("path") is string dir && Path.Combine(dir, "Bin", "Binarize", "binarize.exe") is var exe && File.Exists(exe)) return exe;
                }
                catch
                {
                    // no access: try the next
                }
            }
        return null;
    }
}

/// <summary>Result of a binarize test run.</summary>
/// <param name="Output">The ODOL file binarize wrote (null when none).</param>
/// <param name="Result">The binarized model read back (null when it could not be read).</param>
public sealed record BinarizeRun(string WorkDir, string? Output, string LogFile, int ExitCode, TimeSpan Time,
    List<P3DForge.Core.IO.BinarizeMessage> Messages, P3DForge.Core.Model.P3DModel? Result, bool Cancelled);

public static partial class Binarize
{
    /// <summary>
    /// Binarizes <paramref name="model"/> (the document as it is now, unsaved edits included) in a temporary folder:
    /// the model is written there under its work-drive path together with the model.cfg files binarize would read
    /// (its folder and the parents), then "binarize -norecurse -always -silent src out" runs. Nothing next to the
    /// model is touched. Textures and rvmats are read from the work drive (P:) by binarize itself.
    /// </summary>
    public static async Task<BinarizeRun> RunAsync(string tool, P3DForge.Core.Model.P3DModel model, string? modelPath, string? modelCfg,
        string name, string projectRoot, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        name = string.Concat(Path.GetFileNameWithoutExtension(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        if (name.Length == 0) name = "model";
        char sep = Path.DirectorySeparatorChar;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)) + sep;
        bool Inside(string d) => (Path.TrimEndingDirectorySeparator(d) + sep).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        bool IsRoot(string d) => string.Equals(Path.TrimEndingDirectorySeparator(d) + sep, root, StringComparison.OrdinalIgnoreCase);
        string rel = modelPath is not null && Path.GetFullPath(modelPath).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.GetFullPath(modelPath)[root.Length..]) ?? ""
            : name;
        var hash = System.Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes((modelPath ?? name).ToLowerInvariant())))[..8].ToLowerInvariant();
        var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "P3DForge", "binarize", $"{name}_{hash}");
        var src = Path.Combine(work, "src", rel);
        var dst = Path.Combine(work, "out");
        await Task.Run(() =>
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllBytes(Path.Combine(src, name + ".p3d"), P3DForge.Core.IO.Mlod.Write(model));
            // model.cfg as binarize finds it: the model's folder and every parent up to the work drive
            if (modelPath is not null)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(modelPath));
                var mirror = src;
                while (dir is not null)
                {
                    var cfg = Path.Combine(dir, "model.cfg");
                    if (File.Exists(cfg)) File.Copy(cfg, Path.Combine(mirror, "model.cfg"), overwrite: true);
                    // up to the work drive's root; a model outside the drive only gets its own folder's model.cfg
                    if (!Inside(dir) || IsRoot(dir) || Path.GetDirectoryName(mirror) is not { } up) break;
                    dir = Path.GetDirectoryName(dir);
                    mirror = up;
                }
            }
            else if (modelCfg is not null && File.Exists(modelCfg)) File.Copy(modelCfg, Path.Combine(src, "model.cfg"));
        }, ct);

        var logFile = Path.Combine(work, "binarize.log");
        // Started from a GUI program binarize writes nothing into a pipe, but it does write into a file: run it through
        // cmd with its output redirected to the log (hidden window).
        var command = $"\"\"{tool}\" -norecurse -always -silent -maxProcesses=0 \"{src}\" \"{dst}\" > \"{logFile}\" 2>&1\"";
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d /s /c " + command)
        {
            // binarize resolves texture / rvmat paths (dz\…) against its current folder: the work drive, as in DayZ Tools
            WorkingDirectory = Directory.Exists(projectRoot) ? projectRoot : work,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException($"{tool} did not start");
        // the editor closing must not leave binarize running
        void Kill(object? _, EventArgs __)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch
            {
                // gone
            }
        }
        AppDomain.CurrentDomain.ProcessExit += Kill;
        bool cancelled = false;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(15));
            await p.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
        }
        AppDomain.CurrentDomain.ProcessExit -= Kill;
        string log;
        try
        {
            log = File.Exists(logFile) ? await File.ReadAllTextAsync(logFile, CancellationToken.None) : "";
        }
        catch (IOException)
        {
            log = ""; // still held by a killed binarize
        }
        var output = Path.Combine(dst, name + ".p3d");
        var messages = P3DForge.Core.IO.BinarizeLog.Parse(log, model);
        P3DForge.Core.Model.P3DModel? result = null;
        if (!File.Exists(output))
        {
            output = null;
            if (!cancelled) messages.Insert(0, new P3DForge.Core.IO.BinarizeMessage(true, $"binarize wrote no file (exit code {(p.HasExited ? p.ExitCode : -1)}) — see the log"));
        }
        else
        {
            try
            {
                result = await Task.Run(() => P3DForge.Core.IO.Odol.Read(File.ReadAllBytes(output)), CancellationToken.None);
            }
            catch (Exception ex)
            {
                messages.Add(new P3DForge.Core.IO.BinarizeMessage(false, $"The binarized file could not be read back: {ex.Message}"));
            }
        }
        return new BinarizeRun(work, output, logFile, p.HasExited ? p.ExitCode : -1, sw.Elapsed, messages, result, cancelled);
    }
}
