using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using P3DForge.App.Editor;
using P3DForge.App.Shell;

namespace P3DForge.App.Dialogs;

/// <summary>
/// Small modal dialog helpers shared by the panels features (prompt, confirm, window scaffolding).
/// <para>
/// Automation hook: while a <c>P3DFORGE_AUTOMATION</c> script runs, dialogs are never shown (they would block the
/// script). Confirmations return the flag <c>"automation.confirm"</c> (default true) and prompts consume the flag
/// <c>"automation.answer"</c> (string; cancelled when unset), e.g. <c>{ "setFlag": ["automation.answer", "door"] }</c>
/// followed by <c>{ "command": "namedSelection.rename" }</c>.
/// </para>
/// </summary>
public static class Dlg
{
    static EditorState Ed => AppServices.Editor;

    public static bool Automated => Automation.ScriptPath is not null;

    /// <summary>Consumes the scripted answer for the next prompt (automation only).</summary>
    public static string? TakeAnswer()
    {
        var a = Ed.Flag<string?>("automation.answer", null);
        if (a is not null) Ed.SetFlag("automation.answer", null);
        return a;
    }

    /// <summary>
    /// Automation only: when P3DFORGE_DIALOG_SHOTS names a folder, shows the dialog briefly, saves its content as
    /// &lt;folder&gt;/&lt;name&gt;.png and closes it (dialogs are never left open under automation).
    /// </summary>
    public static void Capture(Window w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("P3DFORGE_DIALOG_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            w.ShowActivated = false;
            w.Show();
            w.UpdateLayout();
            if (w.Content is not FrameworkElement content || content.ActualWidth < 1) return;
            var m = content.Margin;
            int width = (int)Math.Ceiling(content.ActualWidth + m.Left + m.Right), height = (int)Math.Ceiling(content.ActualHeight + m.Top + m.Bottom);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, width, height)); // ≈ Mica dark
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Render(content); // rendered at its layout offset (keeps the margin)
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            System.IO.Directory.CreateDirectory(dir);
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        catch (Exception ex)
        {
            Ed.Log($"dialog capture failed: {ex.Message}", LogLevel.Warn);
        }
        finally
        {
            w.Close();
        }
    }

    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>A dialog window owned by the main window (dark theme comes from Application.ThemeMode).</summary>
    public static Window MakeWindow(string title, UIElement content, double minWidth = 340)
    {
        var w = new Window
        {
            Title = title,
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            MinWidth = minWidth,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var owner = Application.Current.MainWindow;
        if (owner is not null && owner.IsVisible) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w;
    }

    public static Button Button(string text, bool isDefault = false, bool isCancel = false) => new()
    {
        Content = text,
        IsDefault = isDefault,
        IsCancel = isCancel,
        MinWidth = 80,
        Margin = new Thickness(8, 0, 0, 0),
        Style = (Style)Application.Current.FindResource("CompactButton"),
    };

    /// <summary>OK/Cancel row; OK closes with DialogResult = true when <paramref name="onOk"/> returns true.</summary>
    public static (StackPanel Row, Button Ok) OkCancel(Window w, Func<bool> onOk, string okText = "OK")
    {
        var ok = Button(okText, isDefault: true);
        var cancel = Button("Cancel", isCancel: true);
        ok.Click += (_, _) =>
        {
            if (onOk()) w.DialogResult = true;
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        return (row, ok);
    }

    public static TextBlock Label(string text, double top = 0) => new() { Text = text, Margin = new Thickness(0, top, 0, 4), FontSize = 12 };

    public static TextBlock ErrorText() => new()
    {
        Foreground = Res("Editor.Error"),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420,
        Margin = new Thickness(0, 6, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    public static void SetError(TextBlock t, string? error)
    {
        t.Text = error ?? "";
        t.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
    }

    public static TextBox TextBox(string text = "") => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("CompactTextBox"),
        MinWidth = 300,
    };

    /// <summary>Yes/No confirmation (OK/Cancel). Under automation returns the "automation.confirm" flag (default true).</summary>
    public static bool Confirm(string title, string message, string okText = "OK")
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, FontSize = 12 });
        var w = MakeWindow(title, panel);
        var (row, ok) = OkCancel(w, () => true, okText);
        panel.Children.Add(row);
        w.Loaded += (_, _) => ok.Focus();
        if (Automated)
        {
            Capture(w, "confirm");
            bool yes = Ed.Flag("automation.confirm", true);
            Ed.Log($"[automation] confirm \"{title}\" → {yes}");
            return yes;
        }

        return w.ShowDialog() == true;
    }

    /// <summary>A message with an OK button (selectable text). Under automation it is only logged.</summary>
    public static void Info(string title, string message)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBox
        {
            Text = message,
            Style = (Style)Application.Current.FindResource("CompactTextBox"),
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            FontSize = 12,
        });
        var w = MakeWindow(title, panel);
        var (row, ok) = OkCancel(w, () => true, "OK");
        panel.Children.Add(row);
        w.Loaded += (_, _) => ok.Focus();
        if (Automated)
        {
            Capture(w, "info");
            Ed.Log($"[automation] info \"{title}\": {message.Replace('\n', ' ')}");
            return;
        }
        w.ShowDialog();
    }

    /// <summary>
    /// Single-line text prompt with live validation (<paramref name="validate"/> returns an error or null).
    /// Returns the text, or null when cancelled.
    /// </summary>
    public static string? Prompt(string title, string label, string initial, Func<string, string?>? validate = null, string okText = "OK")
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Label(label));
        var box = TextBox(initial);
        panel.Children.Add(box);
        var error = ErrorText();
        panel.Children.Add(error);
        var w = MakeWindow(title, panel);
        var (row, ok) = OkCancel(w, () => validate?.Invoke(box.Text) is null, okText);
        panel.Children.Add(row);
        void Check()
        {
            var e = validate?.Invoke(box.Text);
            SetError(error, e);
            ok.IsEnabled = e is null;
        }
        box.TextChanged += (_, _) => Check();
        w.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
            Check();
        };
        if (Automated)
        {
            Capture(w, "prompt");
            var a = TakeAnswer();
            var err = a is null ? "no automation.answer" : validate?.Invoke(a);
            Ed.Log($"[automation] prompt \"{title}\" → {a ?? "(cancel)"}{(err is null ? "" : " — " + err)}", err is null ? LogLevel.Info : LogLevel.Warn);
            return err is null ? a : null;
        }

        return w.ShowDialog() == true ? box.Text : null;
    }

    public static bool TryParse(string s, out double v) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    /// <summary>Shows a validation error (status bar + log) instead of a modal message box.</summary>
    public static void Fail(string message) => Ed.Log(message, LogLevel.Warn);

    /// <summary>Runs <paramref name="action"/>; ArgumentException/InvalidOperationException messages go to the log.</summary>
    public static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Fail(ex.Message);
        }
    }

    public static ModifierKeys Mods => Keyboard.Modifiers;
}
