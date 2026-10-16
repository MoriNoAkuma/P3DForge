using System.Windows.Input;

namespace P3DForge.App.Editor;

/// <summary>
/// A user action. Menus, toolbar buttons, hotkeys and the command palette refer to commands by id
/// (ids and default hotkeys: docs/COMMANDS.md). A menu entry whose id is not registered is shown disabled.
/// </summary>
public sealed class Command
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>Accelerators like "Ctrl+S", "Delete", "Shift+F2", "Num1"; the first is shown in menus.</summary>
    public string[] Hotkeys { get; set; } = [];
    public required Action Execute { get; init; }
    public Func<bool>? CanExecute { get; init; }
    /// <summary>For toggles: shown with a check mark.</summary>
    public Func<bool>? IsChecked { get; init; }
    public string? Description { get; init; }
}

public static class Commands
{
    static readonly Dictionary<string, Command> registry = new(StringComparer.Ordinal);

    /// <summary>Raised when commands are (re)registered.</summary>
    public static event Action? RegistryChanged;

    /// <summary>Where command exceptions are reported (set by the app to the log).</summary>
    public static Action<string> ErrorSink { get; set; } = _ => { };

    public static void Register(Command cmd)
    {
        registry[cmd.Id] = cmd;
        RegistryChanged?.Invoke();
    }

    public static void Register(params Command[] cmds)
    {
        foreach (var c in cmds) registry[c.Id] = c;
        RegistryChanged?.Invoke();
    }

    public static Command? Get(string id) => registry.GetValueOrDefault(id);
    public static IReadOnlyCollection<Command> All => registry.Values;

    // Labels are written Object Builder style: "_X" marks the Alt access key ("__" = a real underscore) and "&&" an
    // ampersand. Menus show them through these two helpers, never raw.

    /// <summary>A label for a WPF menu header: "_" stays the (hidden) access key, "&amp;&amp;" becomes "&amp;".</summary>
    public static string MenuText(string label) => label.Replace("&&", "&");

    /// <summary>A label as plain text (tooltips, search, log): no access-key marks, "&amp;&amp;" → "&amp;".</summary>
    public static string PlainText(string label) =>
        label.Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_").Replace("&&", "&");

    public static bool IsEnabled(string id)
    {
        if (!registry.TryGetValue(id, out var c)) return false;
        try
        {
            return c.CanExecute?.Invoke() ?? true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsChecked(string id)
    {
        try
        {
            return registry.TryGetValue(id, out var c) && (c.IsChecked?.Invoke() ?? false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs a command if registered and enabled. Exceptions are reported, never thrown.</summary>
    public static void Run(string id)
    {
        if (!registry.TryGetValue(id, out var c) || !IsEnabled(id)) return;
        try
        {
            c.Execute();
        }
        catch (Exception ex)
        {
            ErrorSink($"{PlainText(c.Label)}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // ---- accelerators ---------------------------------------------------------------------------

    /// <summary>Converts a WPF key event to an accelerator string ("Ctrl+Shift+Z", "Num1", "F5", "Delete").</summary>
    public static string? ToAccelerator(Key key, ModifierKeys mods)
    {
        string? name = KeyName(key);
        if (name is null) return null;
        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(name);
        return string.Join('+', parts);
    }

    static string? KeyName(Key key)
    {
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System:
                return null;
            case >= Key.A and <= Key.Z:
                return key.ToString();
            case >= Key.D0 and <= Key.D9:
                return ((int)(key - Key.D0)).ToString();
            case >= Key.NumPad0 and <= Key.NumPad9:
                return "Num" + (int)(key - Key.NumPad0);
            case Key.Multiply: return "NumMultiply";
            case Key.Add: return "NumAdd";
            case Key.Subtract: return "NumSubtract";
            case Key.Divide: return "NumDivide";
            case Key.Decimal: return "NumDecimal";
            case Key.OemQuestion: return "/";
            case Key.Oem5: return "\\";
            case Key.OemOpenBrackets: return "[";
            case Key.Oem6: return "]";
            case Key.OemPlus: return "=";
            case Key.OemMinus: return "-";
            case Key.OemComma: return ",";
            case Key.OemPeriod: return ".";
            case Key.Oem1: return ";";
            case Key.Oem3: return "`";
            case Key.Back: return "Backspace";
            case Key.Return: return "Enter";
            case Key.Escape: return "Escape";
            case Key.Space: return "Space";
            case Key.Tab: return "Tab";
            case Key.Insert: return "Insert";
            case Key.Delete: return "Delete";
            case Key.Home: return "Home";
            case Key.End: return "End";
            case Key.PageUp: return "PageUp";
            case Key.PageDown: return "PageDown";
            case Key.Left: return "Left";
            case Key.Right: return "Right";
            case Key.Up: return "Up";
            case Key.Down: return "Down";
            case Key.Pause: return "Pause";
            default:
                return key is >= Key.F1 and <= Key.F24 ? key.ToString() : null;
        }
    }

    /// <summary>Canonical form of an accelerator string for comparisons ("shift+ctrl+s" → "Ctrl+Shift+S").</summary>
    public static string Normalize(string acc)
    {
        var parts = acc.Split('+', StringSplitOptions.TrimEntries);
        // A trailing "+" key ("Ctrl++") is not used by any command; keep it simple.
        string key = parts[^1];
        var mods = parts[..^1].Select(p => p.ToLowerInvariant()).ToHashSet();
        var o = new List<string>(4);
        if (mods.Contains("ctrl") || mods.Contains("control")) o.Add("Ctrl");
        if (mods.Contains("shift")) o.Add("Shift");
        if (mods.Contains("alt")) o.Add("Alt");
        o.Add(key.Length == 1 ? key.ToUpperInvariant() : key);
        return string.Join('+', o);
    }

    /// <summary>The command bound to an accelerator (enabled ones preferred).</summary>
    public static Command? ForAccelerator(string acc)
    {
        string norm = Normalize(acc);
        Command? fallback = null;
        foreach (var c in registry.Values)
        {
            if (!c.Hotkeys.Any(h => Normalize(h) == norm)) continue;
            if (IsEnabled(c.Id)) return c;
            fallback ??= c;
        }
        return fallback;
    }
}
