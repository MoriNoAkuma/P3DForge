using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Editor;

public enum SelectMode
{
    Points,
    Faces,
}

public enum ToolId
{
    Select,
    Move,
    Rotate,
    Scale,
}

public enum EditKind
{
    /// <summary>Changes the document (marks it dirty).</summary>
    Content,
    /// <summary>Only the current selection changes (undoable, does not mark dirty).</summary>
    Selection,
}

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>What changed in a notification (several flags may be set).</summary>
[Flags]
public enum Change
{
    None = 0,
    Document = 1,
    Model = 2,
    Selection = 4,
    ActiveLod = 8,
    Ui = 16,
    Pin = 32,
    Log = 64,
    /// <summary>A document was loaded or created (history cleared); set together with Document/Model/….</summary>
    Load = 128,
}

public sealed class EditOptions
{
    public EditKind Kind { get; init; } = EditKind.Content;
    /// <summary>Consecutive edits with the same key within 1.5 s merge into one undo step.</summary>
    public string? MergeKey { get; init; }
    /// <summary>Skip automatic normal recalculation for LODs whose geometry changed.</summary>
    public bool KeepNormals { get; init; }
    /// <summary>Target LOD for <see cref="EditorState.EditLod"/> (default: active LOD).</summary>
    public int? LodIndex { get; init; }
}

/// <summary>
/// Central editor state: the open document, active LOD, undo/redo history and UI modes.
/// All document changes go through <see cref="Edit"/>/<see cref="EditLod"/>, which take a pure function
/// producing a new immutable model (see the persistence convention on <see cref="Lod"/>).
/// Single-threaded: use from the UI thread only.
/// </summary>
public sealed class EditorState
{
    sealed record Snapshot(P3DModel Model, int ActiveLod, long ContentVersion, string Label, string? MergeKey)
    {
        public DateTime Time { get; set; } = DateTime.UtcNow;
    }

    readonly List<Snapshot> undo = [];
    readonly List<Snapshot> redo = [];
    readonly List<LogEntry> log = [];
    long contentVersion, savedContentVersion, versionSeed = 1;
    int batchDepth;
    Change pending;
    Dictionary<string, object?> flags = [];

    /// <summary>Raised after every change (combined while inside <see cref="Batch"/>).</summary>
    public event Action<Change>? Changed;

    public P3DModel Model { get; private set; } = P3DModel.Empty();
    public string? Path { get; private set; }
    public int ActiveLod { get; private set; }
    public SelectMode SelectMode { get; private set; } = SelectMode.Points;
    public ToolId Tool { get; private set; } = ToolId.Select;
    /// <summary>Pivot for rotate/scale (Object Builder's "pin"), raw P3D coordinates; null = selection centre.</summary>
    public System.Numerics.Vector3? Pin { get; private set; }
    public int UndoLimit { get; set; } = 200;
    /// <summary>Monotonic counter bumped on every notification.</summary>
    public long Version { get; private set; }

    public Lod Lod => Model.Lods[ActiveLod];
    public bool Dirty => contentVersion != savedContentVersion;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string? UndoLabel => undo.Count > 0 ? undo[^1].Label : null;
    public string? RedoLabel => redo.Count > 0 ? redo[^1].Label : null;
    /// <summary>Undoable steps, oldest first.</summary>
    public IReadOnlyList<string> UndoLabels => undo.Select(s => s.Label).ToList();
    /// <summary>Redoable steps, next first.</summary>
    public IReadOnlyList<string> RedoLabels => redo.Select(s => s.Label).Reverse().ToList();
    public IReadOnlyList<LogEntry> LogEntries => log;

    public string FileName => Path is null ? UntitledName ?? "Untitled" : System.IO.Path.GetFileName(Path);

    /// <summary>Name shown (and offered by Save As) while the document has no file, e.g. a converted binarized model.</summary>
    public string? UntitledName { get; set; }
    public string Title => $"{(Dirty ? "● " : "")}{FileName} — {LodNames.Name(Lod.Resolution)} — P3D Forge";

    // ---- notifications ------------------------------------------------------------------------------

    /// <summary>Groups notifications: listeners get one combined change after <paramref name="action"/>.</summary>
    public void Batch(Action action)
    {
        batchDepth++;
        try
        {
            action();
        }
        finally
        {
            batchDepth--;
            if (batchDepth == 0) Flush();
        }
    }

    public void Notify(Change change)
    {
        pending |= change;
        if (batchDepth == 0) Flush();
    }

    void Flush()
    {
        var c = pending;
        if (c == Change.None) return;
        pending = Change.None;
        Version++;
        Changed?.Invoke(c);
    }

    // ---- log ----------------------------------------------------------------------------------------

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        log.Add(new LogEntry(DateTime.Now, level, message));
        if (log.Count > 2000) log.RemoveRange(0, log.Count - 2000);
        Notify(Change.Log);
    }

    // ---- document lifecycle -------------------------------------------------------------------------

    /// <summary>Replaces the document (open/new) and clears history.</summary>
    public void Load(P3DModel model, string? path)
    {
        if (model.Lods.IsEmpty) model = P3DModel.Empty();
        Model = model;
        Path = path;
        UntitledName = null;
        ActiveLod = 0;
        Pin = null;
        undo.Clear();
        redo.Clear();
        contentVersion = ++versionSeed;
        savedContentVersion = contentVersion;
        Notify(Change.Load | Change.Document | Change.Model | Change.Selection | Change.ActiveLod | Change.Pin);
    }

    /// <summary>Marks the document as changed (e.g. a recovered autosave that is not in its file yet).</summary>
    public void MarkDirty()
    {
        contentVersion = ++versionSeed;
        Notify(Change.Document);
    }

    /// <summary>Records that the current model was written to <paramref name="path"/>.</summary>
    public void MarkSaved(string path)
    {
        Path = path;
        savedContentVersion = contentVersion;
        Notify(Change.Document);
    }

    // ---- editing ------------------------------------------------------------------------------------

    /// <summary>
    /// Applies a document edit. <paramref name="fn"/> returns the new model (or the same instance to cancel).
    /// LODs whose positions/faces changed get recalculated normals unless <see cref="EditOptions.KeepNormals"/>
    /// is set or the function replaced the normals itself.
    /// </summary>
    public bool Edit(string label, Func<P3DModel, P3DModel> fn, EditOptions? opts = null)
    {
        opts ??= new EditOptions();
        var before = Model;
        var after = fn(before);
        if (ReferenceEquals(after, before)) return false;

        if (opts.Kind == EditKind.Content && !opts.KeepNormals)
        {
            var lods = after.Lods;
            for (int i = 0; i < after.Lods.Length && i < before.Lods.Length; i++)
            {
                var lod = after.Lods[i];
                var old = before.Lods[i];
                if (ReferenceEquals(lod, old) || before.Lods.Contains(lod)) continue;
                bool geom = !ReferenceEquals(lod.Positions, old.Positions) || !ReferenceEquals(lod.FacePoints, old.FacePoints);
                if (geom && ReferenceEquals(lod.Normals, old.Normals)) lods = lods.SetItem(i, Structure.RecalcNormals(lod));
            }
            if (lods != after.Lods) after = after with { Lods = lods };
        }

        var now = DateTime.UtcNow;
        var top = undo.Count > 0 ? undo[^1] : null;
        bool merge = opts.MergeKey is not null && top?.MergeKey == opts.MergeKey && (now - top.Time).TotalSeconds < 1.5 && redo.Count == 0;
        if (merge) top!.Time = now;
        else
        {
            undo.Add(new Snapshot(before, ActiveLod, contentVersion, label, opts.MergeKey));
            if (undo.Count > UndoLimit) undo.RemoveAt(0);
        }
        redo.Clear();
        Model = after;
        if (ActiveLod >= after.Lods.Length) ActiveLod = Math.Max(0, after.Lods.Length - 1);
        if (opts.Kind == EditKind.Content) contentVersion = ++versionSeed;
        Notify(opts.Kind == EditKind.Content ? Change.Model | Change.Selection | Change.Document | Change.ActiveLod : Change.Selection);
        return true;
    }

    /// <summary>Edits one LOD (default: the active one).</summary>
    public bool EditLod(string label, Func<Lod, Lod> fn, EditOptions? opts = null)
    {
        int index = opts?.LodIndex ?? ActiveLod;
        return Edit(label, m =>
        {
            var lod = m.Lods[index];
            var next = fn(lod);
            return ReferenceEquals(next, lod) ? m : m.WithLod(index, next);
        }, opts);
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        var snap = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add(new Snapshot(Model, ActiveLod, contentVersion, snap.Label, null));
        Restore(snap);
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        var snap = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add(new Snapshot(Model, ActiveLod, contentVersion, snap.Label, null));
        Restore(snap);
    }

    public void UndoSteps(int n) => Batch(() =>
    {
        for (int i = 0; i < n && CanUndo; i++) Undo();
    });

    public void RedoSteps(int n) => Batch(() =>
    {
        for (int i = 0; i < n && CanRedo; i++) Redo();
    });

    void Restore(Snapshot s)
    {
        Model = s.Model;
        ActiveLod = Math.Min(s.ActiveLod, Model.Lods.Length - 1);
        contentVersion = s.ContentVersion;
        Notify(Change.Model | Change.Selection | Change.Document | Change.ActiveLod);
    }

    // ---- LOD & selection ----------------------------------------------------------------------------

    public void SetActiveLod(int index)
    {
        if (index < 0 || index >= Model.Lods.Length || index == ActiveLod) return;
        ActiveLod = index;
        Notify(Change.ActiveLod | Change.Selection);
    }

    /// <summary>Current selection of a LOD as fresh arrays (safe to modify).</summary>
    public SelectionMask GetSelection(int? lodIndex = null) => Selection.GetSelected(Model.Lods[lodIndex ?? ActiveLod]);

    /// <summary>Replaces the selection of a LOD (undoable, does not mark dirty).</summary>
    public void SetSelection(SelectionMask mask, string label = "Select", int? lodIndex = null) =>
        EditLod(label, lod => Selection.WithSelected(lod, mask), new EditOptions { Kind = EditKind.Selection, LodIndex = lodIndex });

    // ---- UI modes -----------------------------------------------------------------------------------

    public void SetSelectMode(SelectMode mode)
    {
        if (mode == SelectMode) return;
        SelectMode = mode;
        Notify(Change.Ui);
    }

    public void SetTool(ToolId tool)
    {
        if (tool == Tool) return;
        Tool = tool;
        Notify(Change.Ui);
    }

    public void SetPin(System.Numerics.Vector3? pin)
    {
        Pin = pin;
        Notify(Change.Pin);
    }

    /// <summary>Free-form UI flags owned by features (namespaced keys like "view.shading").</summary>
    public T Flag<T>(string key, T fallback) => flags.TryGetValue(key, out var v) && v is T t ? t : fallback;

    public void SetFlag(string key, object? value)
    {
        if (flags.TryGetValue(key, out var old) && Equals(old, value)) return;
        flags = new Dictionary<string, object?>(flags) { [key] = value };
        Notify(Change.Ui);
    }
}
