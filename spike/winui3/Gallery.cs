using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;
using static IssunSpike.Native;

namespace IssunSpike;

/// <summary>
/// --gallery dir: every button-family control WinUI 3 ships, and every
/// button-family style key in its generic.xaml, at rest, pointer-over,
/// pressed and disabled, rendered offscreen in dark mode to
/// dir\buttons-NN.png. Pointer-over and pressed are forced with
/// VisualStateManager.GoToState (an offscreen render can't hover); a cell
/// whose template has no such state says so instead of showing a copy of rest.
/// </summary>
internal static class Gallery
{
    private sealed record Row(string Type, string Style, Func<Control> Make, string?[] States, Action<Control>? Prepare = null)
    {
        public string Note { get; init; } = "";
    }

    private const string Xmlns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:controls=\"using:Microsoft.UI.Xaml.Controls\"";

    // The four columns. null in a row's States means "disable the control and
    // let it pick its own disabled state" (Disabled, CheckedDisabled, ...).
    private static readonly string[] Plain = { "Normal", "PointerOver", "Pressed", null! };
    private static readonly string[] Checked = { "Checked", "CheckedPointerOver", "CheckedPressed", null! };

    private static MenuFlyout Menu()
    {
        var m = new MenuFlyout();
        m.Items.Add(new MenuFlyoutItem { Text = "Item" });
        return m;
    }

    private static Control Wrap(ICommandBarElement e)
    {
        var bar = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed, IsDynamicOverflowEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };
        bar.PrimaryCommands.Add(e);
        return bar;
    }

    private static List<Row> CoreRows() => new()
    {
        new("Button", "DefaultButtonStyle", () => new Button { Content = "Button" }, Plain),
        new("Button", "AccentButtonStyle", () => new Button { Content = "Button", Style = Res("AccentButtonStyle") }, Plain),
        new("HyperlinkButton", "DefaultHyperlinkButtonStyle", () => new HyperlinkButton { Content = "HyperlinkButton" }, Plain),
        new("ToggleButton, off", "DefaultToggleButtonStyle", () => new ToggleButton { Content = "ToggleButton" }, Plain),
        new("ToggleButton, on", "DefaultToggleButtonStyle", () => new ToggleButton { Content = "ToggleButton", IsChecked = true }, Checked),
        new("RepeatButton", "DefaultRepeatButtonStyle", () => new RepeatButton { Content = "RepeatButton" }, Plain),
        new("DropDownButton", "DefaultDropDownButtonStyle", () => new DropDownButton { Content = "DropDownButton", Flyout = Menu() }, Plain),
        new("SplitButton, main part", "SplitButtonStyle", () => new SplitButton { Content = "SplitButton", Flyout = Menu() }, new[] { "Normal", "PrimaryPointerOver", "PrimaryPressed", null }),
        new("SplitButton, chevron part", "SplitButtonStyle", () => new SplitButton { Content = "SplitButton", Flyout = Menu() }, new[] { "Normal", "SecondaryPointerOver", "SecondaryPressed", null }),
        new("ToggleSplitButton, off", "SplitButtonStyle", () => new ToggleSplitButton { Content = "ToggleSplitButton", Flyout = Menu() }, new[] { "Normal", "PrimaryPointerOver", "PrimaryPressed", null }),
        new("ToggleSplitButton, on", "SplitButtonStyle", () => new ToggleSplitButton { Content = "ToggleSplitButton", Flyout = Menu(), IsChecked = true }, new[] { "Checked", "CheckedPrimaryPointerOver", "CheckedPrimaryPressed", null }),
        new("AppBarButton in a CommandBar", "DefaultAppBarButtonStyle", () => Wrap(new AppBarButton { Icon = new SymbolIcon(Symbol.Add), Label = "AppBarButton" }), Plain),
        new("AppBarToggleButton in a CommandBar, off", "DefaultAppBarToggleButtonStyle", () => Wrap(new AppBarToggleButton { Icon = new SymbolIcon(Symbol.Pin), Label = "AppBarToggleButton" }), Plain),
        new("AppBarToggleButton in a CommandBar, on", "DefaultAppBarToggleButtonStyle", () => Wrap(new AppBarToggleButton { Icon = new SymbolIcon(Symbol.Pin), Label = "AppBarToggleButton", IsChecked = true }), Checked),
        new("RadioButton, off", "DefaultRadioButtonStyle", () => new RadioButton { Content = "RadioButton", GroupName = Guid.NewGuid().ToString() }, Plain),
        new("RadioButton, on", "DefaultRadioButtonStyle", () => new RadioButton { Content = "RadioButton", IsChecked = true, GroupName = Guid.NewGuid().ToString() }, Plain),
        new("CheckBox, off", "DefaultCheckBoxStyle", () => new CheckBox { Content = "CheckBox" }, new[] { "UncheckedNormal", "UncheckedPointerOver", "UncheckedPressed", null }),
        new("CheckBox, on", "DefaultCheckBoxStyle", () => new CheckBox { Content = "CheckBox", IsChecked = true }, new[] { "CheckedNormal", "CheckedPointerOver", "CheckedPressed", null }),
        new("CheckBox, indeterminate", "DefaultCheckBoxStyle", () => new CheckBox { Content = "CheckBox", IsThreeState = true, IsChecked = null }, new[] { "IndeterminateNormal", "IndeterminatePointerOver", "IndeterminatePressed", null }),
        new("ToggleSwitch, off", "DefaultToggleSwitchStyle", () => new ToggleSwitch(), Plain),
        new("ToggleSwitch, on", "DefaultToggleSwitchStyle", () => new ToggleSwitch { IsOn = true }, Plain),
    };

    // Every other keyed style in generic.xaml whose target is a button-family
    // control (tools/button-styles.py lists them). Reveal styles are WinUI 2's
    // and kept for compatibility; they are shown like the rest.
    // Every other keyed style in generic.xaml whose target is a button-family
    // control (tools/button-styles.py lists them). Content: what WinUI's own use
    // of the style gives it where generic.xaml shows one (tools/style-usage.py);
    // for a style whose text is drawn in the icon font and gets its glyph from
    // its control's code, nothing (the sample word would show as boxes); plain
    // sample text otherwise. Reveal styles are WinUI 2's, kept for compatibility.
    private const string NoContent = "no content: its control sets the glyph in code";
    private static readonly (string Target, string Key, string? Content, string Note)[] Keyed =
    {
        ("Button", "SubtleButtonStyle", null, ""),
        ("Button", "AlternateCloseButtonStyle", "", NoContent),
        ("Button", "InfoBarCloseButtonStyle", null, ""),
        ("Button", "TabViewCloseButtonStyle", null, ""),
        ("Button", "EllipsisButton", "icon:&#xE10C;", "glyph from WinUI's own use"),
        ("Button", "CommandBarFlyoutEllipsisButtonStyle", "icon:&#xE712;", "glyph from WinUI's own use"),
        ("Button", "NavigationBackButtonNormalStyle", "", NoContent),
        ("Button", "NavigationBackButtonSmallStyle", "", NoContent),
        ("Button", "PaneToggleButtonStyle", "", NoContent),
        ("Button", "NavigationViewPaneSearchButtonStyle", "", NoContent),
        ("Button", "NavigationViewOverflowButtonStyleWhenPaneOnTop", "More", "content from WinUI's own use"),
        ("Button", "NavigationViewOverflowButtonNoLabelStyleWhenPaneOnTop", null, ""),
        ("Button", "TitleBarBackButtonStyle", "", NoContent),
        ("Button", "TitleBarPaneToggleButtonStyle", "", NoContent),
        ("Button", "WindowCaptionButton", "", NoContent),
        ("Button", "DatePickerFlyoutButtonStyle", null, ""),
        ("Button", "TimePickerFlyoutButtonStyle", null, ""),
        ("Button", "PagerControlNumberPanelButtonStyle", null, ""),
        ("Button", "PagerControlPageNavigationButtonStyle", null, ""),
        ("Button", "PipsPagerButtonBaseStyle", "", NoContent),
        ("Button", "PipsPagerNavigationButtonBaseStyle", "", NoContent),
        ("Button", "PipsPagerNextPageButtonStyle", "", NoContent),
        ("Button", "PipsPagerPreviousPageButtonStyle", "", NoContent),
        ("Button", "PipsPagerNormalPipButtonStyle", "", NoContent),
        ("Button", "PipsPagerSelectedPipButtonStyle", "", NoContent),
        ("Button", "TextBlockButtonStyle", null, ""),
        ("Button", "DateTimePickerFlyoutButtonStyle", "&#xE8FB;", "glyph from WinUI's own use"),
        ("RepeatButton", "DateTimePickerFlyoutLoopingSelectorNavigationButtonStyle", "&#xEDDB;", "glyph from WinUI's own use"),
        ("RepeatButton", "ScrollButtonStyle", "icon:&#xEDDB;", "glyph from WinUI's own use"),
        ("ToggleButton", "ExpanderHeaderDownStyle", null, ""),
        ("ToggleButton", "ExpanderHeaderUpStyle", null, ""),
        ("ToggleButton", "InkToolbarCommonButtonStyle", null, ""),
        ("ToggleButton", "ToggleButtonRevealStyle", null, ""),
        ("controls:DropDownButton", "DropDownButtonRevealStyle", null, ""),
        ("controls:SplitButton", "SplitButtonCommandBarStyle", null, ""),
        ("controls:SplitButton", "SplitButtonRevealStyle", null, ""),
        ("AppBarButton", "AppBarButtonStyle", null, ""),
        ("AppBarButton", "AppBarButtonOverflowStyle", null, ""),
        ("AppBarButton", "AppBarButtonRevealStyle", null, ""),
        ("AppBarButton", "AppBarButtonRevealLabelsOnRightStyle", null, ""),
        ("AppBarButton", "AppBarButtonRevealOverflowStyle", null, ""),
        ("AppBarButton", "CommandBarFlyoutAppBarButtonStyle", null, ""),
        ("AppBarButton", "CommandBarFlyoutAppBarButtonStyleBase", null, ""),
        ("AppBarButton", "MediaControlAppBarButtonStyle", null, ""),
        ("AppBarButton", "SwipeItemStyle", null, ""),
        ("AppBarToggleButton", "AppBarToggleButtonStyle", null, ""),
        ("AppBarToggleButton", "AppBarToggleButtonOverflowStyle", null, ""),
        ("AppBarToggleButton", "AppBarToggleButtonRevealStyle", null, ""),
        ("AppBarToggleButton", "AppBarToggleButtonRevealLabelsOnRightStyle", null, ""),
        ("AppBarToggleButton", "AppBarToggleButtonRevealOverflowStyle", null, ""),
        ("AppBarToggleButton", "CommandBarFlyoutAppBarToggleButtonStyle", null, ""),
        ("AppBarToggleButton", "CommandBarFlyoutAppBarToggleButtonStyleBase", null, ""),
        ("CheckBox", "ItemContainerSelectionCheckboxStyle", "", "no content: list items draw it"),
    };

    // Keys app code can't reach: looked up by key at run time they aren't
    // found (XAML parse error 0x802B000A, and Application.Resources hasn't
    // them). Five are defined inside another control's template; two are top
    // level in the package's generic.xaml and still aren't found.
    private static readonly Dictionary<string, string> Scoped = new()
    {
        ["TabViewCloseButtonStyle"] = "not found at run time, though generic.xaml lists it at top level",
        ["InkToolbarCommonButtonStyle"] = "not found at run time, though generic.xaml lists it at top level",
        ["DatePickerFlyoutButtonStyle"] = "not reachable by key: defined inside DatePicker's template",
        ["TimePickerFlyoutButtonStyle"] = "not reachable by key: defined inside TimePicker's template",
        ["AppBarButtonStyle"] = "not reachable by key: defined inside MediaTransportControls' template",
        ["AppBarToggleButtonStyle"] = "not reachable by key: defined inside MediaTransportControls' template",
        ["MediaControlAppBarButtonStyle"] = "not reachable by key: defined inside MediaTransportControls' template",
    };
    private static Style? Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Style : null;

    private static Row KeyedRow((string Target, string Key, string? Content, string Note) k)
    {
        var tag = k.Target.Replace("controls:", "");
        var text = k.Content ?? tag;
        string content = tag switch
        {
            "AppBarButton" or "AppBarToggleButton" => " Label=\"" + tag + "\"><" + tag + ".Icon><SymbolIcon Symbol=\"Add\"/></" + tag + ".Icon></" + tag + ">",
            "CheckBox" => " Content=\"" + text + "\"/>",
            "DropDownButton" or "SplitButton" => " Content=\"" + text + "\"><" + tag + ".Flyout><MenuFlyout><MenuFlyoutItem Text=\"Item\"/></MenuFlyout></" + tag + ".Flyout></" + tag + ">",
            // WinUI puts these glyphs in as a FontIcon inside the button.
            _ when text.StartsWith("icon:") => "><FontIcon Glyph=\"" + text[5..] + "\"/></" + tag + ">",
            _ => " Content=\"" + text + "\"/>",
        };
        var xaml = "<" + tag + " " + Xmlns + " Style=\"{StaticResource " + k.Key + "}\"" + content;
        var states = tag switch
        {
            "CheckBox" => new[] { "UncheckedNormal", "UncheckedPointerOver", "UncheckedPressed", null },
            "SplitButton" => new[] { "Normal", "PrimaryPointerOver", "PrimaryPressed", null },
            _ => Plain,
        };
        var note = Scoped.TryGetValue(k.Key, out var why) ? why : k.Note;
        return new(tag, k.Key, () => (Control)XamlReader.Load(xaml), states) { Note = note };
    }

    public static async Task<int> RunAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        // Pages short enough for the window, which Windows clamps to the
        // screen's height.
        var core = CoreRows();
        var pages = new List<(string Name, List<Row> Rows)> { ("buttons-01-controls", core.Take(11).ToList()), ("buttons-02-controls", core.Skip(11).ToList()) };
        var keyed = Keyed.Select(KeyedRow).ToList();
        for (int i = 0, n = 3; i < keyed.Count; i += 11, n++)
            pages.Add(($"buttons-{n:00}-style-keys", keyed.Skip(i).Take(11).ToList()));
        var failures = new List<string>();
        foreach (var (name, rows) in pages)
            await RenderPageAsync(Path.Combine(dir, name + ".png"), rows, failures);
        File.WriteAllLines(Path.Combine(dir, "buttons-notes.txt"), failures.Count == 0 ? new[] { "every forced state was found in its template" } : failures);
        return 0;
    }

    private static TextBlock Text(string s, double size, string brush, bool semibold = false) => new()
    {
        Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap,
        FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = (Brush)Application.Current.Resources[brush],
    };

    private static async Task RenderPageAsync(string path, List<Row> rows, List<string> failures)
    {
        var grid = new Grid { Padding = new Thickness(24), ColumnSpacing = 16, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        for (var c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] heads = { "Type and style", "Rest", "Pointer over", "Pressed", "Disabled" };
        for (var c = 0; c < heads.Length; c++)
        {
            var h = Text(heads[c], 14, "TextFillColorSecondaryBrush", semibold: true);
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }

        var cells = new List<(Row Row, int Col, Control Ctl, Grid Host)>();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(Text(row.Type, 14, "TextFillColorPrimaryBrush"));
            label.Children.Add(Text(row.Style, 12, "TextFillColorSecondaryBrush"));
            if (row.Note.Length > 0) label.Children.Add(Text(row.Note, 11, "TextFillColorTertiaryBrush"));
            Grid.SetRow(label, r + 1);
            grid.Children.Add(label);
            for (var c = 0; c < 4; c++)
            {
                var host = new Grid { VerticalAlignment = VerticalAlignment.Center, MinHeight = 32 };
                Grid.SetRow(host, r + 1);
                Grid.SetColumn(host, c + 1);
                grid.Children.Add(host);
                try
                {
                    var ctl = row.Make();
                    ctl.HorizontalAlignment = HorizontalAlignment.Left;
                    ctl.VerticalAlignment = VerticalAlignment.Center;
                    if (row.States[c] is null) ctl.IsEnabled = false;
                    host.Children.Add(ctl);
                    cells.Add((row, c, ctl, host));
                }
                catch (Exception ex)
                {
                    var found = Application.Current.Resources.TryGetValue(row.Style, out var res) ? $"found in app resources as {res?.GetType().Name}" : "not found in app resources";
                    var why = $"{row.Type} {row.Style}: couldn't be created (0x{ex.HResult:X8}, {found}): {ex.Message.Split('\n')[0].Trim()}";
                    if (c == 0) failures.Add(why);
                    host.Children.Add(Text(Scoped.ContainsKey(row.Style) ? "not found by key" : "couldn't be created", 12, "TextFillColorTertiaryBrush"));
                }
            }
        }

        var page = new Border
        {
            Child = grid, RequestedTheme = ElementTheme.Dark, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
        };
        var loaded = new TaskCompletionSource();
        page.Loaded += (_, _) => loaded.TrySetResult();
        var w = new Window { Content = page, Title = "IssunSpike gallery" };
        var hwnd = WindowNative.GetWindowHandle(w);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~(nint)WS_EX_APPWINDOW);
        var one = 1; DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref one, 4);
        w.AppWindow.ResizeClient(new SizeInt32(1300, 1400));
        w.AppWindow.Show(false);
        await Task.WhenAny(loaded.Task, Task.Delay(5000));
        await Task.Delay(500);

        // Force each cell's state, now that templates are applied.
        foreach (var (row, col, ctl, host) in cells)
        {
            var state = row.States[col];
            if (state is null) continue;
            var target = ctl is CommandBar bar ? (Control)bar.PrimaryCommands[0] : ctl;
            var ok = VisualStateManager.GoToState(target, state, false);
            if (!ok)
            {
                failures.Add($"{row.Type} {row.Style}: no '{state}' state");
                host.Children.Add(new Border { Child = Text($"no {state} state", 11, "SystemFillColorCautionBrush"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 36, 0, 0) });
            }
        }
        await Task.Delay(600);
        page.RasterizationScale = 1.25;
        page.UpdateLayout();
        await Task.Delay(300);
        if (page.ActualHeight > w.AppWindow.ClientSize.Height) SpikeLog.Write($"[gallery] {path}: page is {page.ActualHeight} tall, more than the window");
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(page, (int)Math.Ceiling(page.ActualWidth * 1.25), (int)Math.Ceiling(page.ActualHeight * 1.25));
        var px = (await rtb.GetPixelsAsync()).ToArray();
        await App.SavePngAsync(path, px, rtb.PixelWidth, rtb.PixelHeight);
        SpikeLog.Write($"[gallery] wrote {path} ({rtb.PixelWidth}x{rtb.PixelHeight}, {rows.Count} rows)");
        Console.Out.WriteLine($"wrote {path}");
        // Hidden, not closed: closing the last window ends a WinUI app.
        w.AppWindow.Hide();
        KeepAlive.Add(w);
    }

    private static readonly List<Window> KeepAlive = new();
}
