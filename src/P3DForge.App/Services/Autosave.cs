using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using P3DForge.App.Editor;
using P3DForge.Core.IO;
using P3DForge.Core.Model;

namespace P3DForge.App.Services;

/// <summary>
/// Crash insurance: while the document has unsaved changes, a copy is written every few minutes to
/// %LOCALAPPDATA%\P3DForge\autosave (one MLOD + one JSON note per running editor, keyed by its process id; the model is
/// immutable, so it is serialized on a background task). Saving, discarding or a normal exit removes it. On start, the
/// copies of editors that are no longer running are offered for recovery. The model file itself is never touched.
/// </summary>
public static class Autosave
{
    /// <summary>Minutes between autosaves (0 = off).</summary>
    public const string MinutesKey = "autosave.minutes";
    public const int DefaultMinutes = 2;

    sealed record Note(string? Path, string? Name, DateTime Time, int Pid, DateTime ProcessStart, int Lods, int Faces);

    static EditorState Ed => AppServices.Editor;
    /// <summary>The autosave folder (P3DFORGE_AUTOSAVE_DIR overrides it for test runs).</summary>
    public static string Dir => Environment.GetEnvironmentVariable("P3DFORGE_AUTOSAVE_DIR") is { Length: > 0 } d ? d
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "P3DForge", "autosave");
    static string Own(string ext) => System.IO.Path.Combine(Dir, $"{Environment.ProcessId}{ext}");

    static DispatcherTimer? timer;
    static P3DModel? written;      // the model last written (reference): nothing new to save while it is current
    static DateTime lastWrite = DateTime.UtcNow;
    static int writing;

    public static int Minutes => Math.Max(0, AppServices.Settings.Get(MinutesKey, DefaultMinutes));

    /// <summary>Time of the last autosave of this session (null = none yet / none pending).</summary>
    public static DateTime? LastSaved { get; private set; }

    /// <summary>Test runs autosave only into their own folder (never into the user's).</summary>
    static bool Enabled => !Dialogs.Dlg.Automated || Environment.GetEnvironmentVariable("P3DFORGE_AUTOSAVE_DIR") is not null;

    public static void Start()
    {
        if (timer is not null || !Enabled) return;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Ed.Changed += OnChanged;
        if (Application.Current is { } app) app.Exit += (_, _) => Delete();
    }

    static void OnChanged(Change c)
    {
        // saved, reverted to the saved state or another document: the copy is no longer needed
        if ((c & (Change.Document | Change.Load)) != 0 && !Ed.Dirty) Delete();
    }

    static void Tick()
    {
        int minutes = Minutes;
        if (minutes == 0 || !Ed.Dirty || ReferenceEquals(Ed.Model, written)) return;
        if ((DateTime.UtcNow - lastWrite).TotalMinutes < minutes || Volatile.Read(ref writing) != 0) return;
        WriteNow();
    }

    /// <summary>Writes the current document now (background). Used by the timer and automation.</summary>
    public static void WriteNow()
    {
        var model = Ed.Model;
        var note = new Note(Ed.Path, Ed.Path is null ? Ed.FileName : null, DateTime.Now, Environment.ProcessId,
            Process.GetCurrentProcess().StartTime, model.Lods.Length, model.Lods.Sum(l => l.FaceCount));
        written = model;
        lastWrite = DateTime.UtcNow;
        Interlocked.Exchange(ref writing, 1);
        Task.Run(() =>
        {
            try
            {
                var bytes = Mlod.Write(model);
                Directory.CreateDirectory(Dir);
                FileService.WriteAtomic(Own(".p3d"), bytes, backup: false);
                File.WriteAllText(Own(".json"), JsonSerializer.Serialize(note));
                Application.Current?.Dispatcher.BeginInvoke(() => LastSaved = note.Time);
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => Ed.Log($"Autosave failed: {ex.Message}", LogLevel.Warn));
            }
            finally
            {
                Interlocked.Exchange(ref writing, 0);
            }
        });
    }

    static void Delete()
    {
        written = null;
        LastSaved = null;
        lastWrite = DateTime.UtcNow;
        if (Volatile.Read(ref writing) != 0)
        {
            // a write is in flight: remove its files once it finished
            Task.Run(async () =>
            {
                while (Volatile.Read(ref writing) != 0) await Task.Delay(50);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!Ed.Dirty) DeleteFiles();
                });
            });
            return;
        }
        DeleteFiles();
    }

    static void DeleteFiles()
    {
        foreach (var f in new[] { Own(".p3d"), Own(".json") })
            try
            {
                if (File.Exists(f)) File.Delete(f);
            }
            catch
            {
                // in use / gone: the next start offers or cleans it
            }
    }

    /// <summary>Copies left by editors that did not exit normally (process gone), newest first.</summary>
    static List<(string File, Note Note)> Orphans()
    {
        var list = new List<(string, Note)>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var json in Directory.EnumerateFiles(Dir, "*.json"))
        {
            var p3d = System.IO.Path.ChangeExtension(json, ".p3d");
            try
            {
                var note = JsonSerializer.Deserialize<Note>(File.ReadAllText(json));
                if (note is null || !File.Exists(p3d) || note.Pid == Environment.ProcessId || Running(note)) continue;
                list.Add((p3d, note));
            }
            catch
            {
                // unreadable note: skip
            }
        }
        return [.. list.OrderByDescending(x => x.Item2.Time)];
    }

    static bool Running(Note n)
    {
        try
        {
            using var p = Process.GetProcessById(n.Pid);
            return Math.Abs((p.StartTime - n.ProcessStart).TotalSeconds) < 2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>On start (after the window is shown): offers the copies of editors that crashed or were killed.</summary>
    public static void OfferRecovery()
    {
        if (!Enabled) return;
        var orphans = Orphans();
        if (orphans.Count == 0) return;
        var (file, note) = orphans[0];
        var what = note.Path ?? note.Name ?? "Untitled";
        var more = orphans.Count > 1 ? $"\n\n{orphans.Count - 1} older autosaved cop{(orphans.Count == 2 ? "y is" : "ies are")} kept in {Dir}." : "";
        var form = new Dialogs.Ops.Form("Recover unsaved work",
                $"P3D Forge did not close normally last time. Unsaved changes were autosaved:\n\n{what}\n" +
                $"autosaved {note.Time:yyyy-MM-dd HH:mm} — {note.Lods} LODs, {note.Faces:N0} faces.{more}")
            .Choice("do", "", ["Recover: open the autosaved copy (Save writes it to the model's file)", "Discard the autosaved copy", "Decide later (keep it)"], 0);
        if (!form.Show("OK")) return;
        int choice = form.Index("do");
        if (choice == 2) return;
        if (choice == 1)
        {
            TryDelete(file);
            Ed.Log($"Discarded the autosaved copy of {what}");
            return;
        }
        if (!FileService.ConfirmDiscard("recover the autosaved copy")) return;
        try
        {
            var warnings = new List<string>();
            var model = Mlod.Read(File.ReadAllBytes(file), warnings);
            Ed.Load(model, note.Path);
            if (note.Path is null) Ed.UntitledName = note.Name;
            Ed.MarkDirty();
            TryDelete(file);
            // it is this session's unsaved document now: keep it protected
            WriteNow();
            Ed.Log($"Recovered the autosaved copy of {what} ({note.Time:HH:mm}). It is not saved yet: File ▸ Save writes it" +
                   (note.Path is null ? "." : $" to {note.Path}."));
        }
        catch (Exception ex)
        {
            Ed.Log($"Cannot recover {file}: {ex.Message}", LogLevel.Error);
        }
    }

    static void TryDelete(string p3d)
    {
        foreach (var f in new[] { p3d, System.IO.Path.ChangeExtension(p3d, ".json") })
            try
            {
                File.Delete(f);
            }
            catch
            {
                // ignore
            }
    }
}
