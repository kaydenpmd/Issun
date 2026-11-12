# WinUI 3 button gallery (12 Nov 2026)

Every button-family control WinUI 3 ships, and every button-family style key
in its `generic.xaml`, at rest, pointer over, pressed and disabled, in dark
and in light: each file ends `-dark.png` or `-light.png`. Rendered offscreen
by the spike: `IssunSpike.exe --gallery <dir> --theme dark|light`
(`spike/winui3/Gallery.cs`). WinUI from Windows App SDK 2.6.5
(`Microsoft.WindowsAppSDK.WinUI` 2.6.5), on Windows 11 26H2 (10.0.26300),
1.25x. The light accent is this PC's accent colour as WinUI's light theme
uses it, the dark one the same accent in dark.

- `buttons-01-controls-*`, `buttons-02-controls-*`: the controls, with
  their default styles: Button, Button with AccentButtonStyle,
  HyperlinkButton, ToggleButton off and on, RepeatButton, DropDownButton,
  SplitButton (main part and chevron part), ToggleSplitButton off and on,
  AppBarButton and AppBarToggleButton (off, on) each in a CommandBar,
  RadioButton off and on, CheckBox off, on and indeterminate, ToggleSwitch
  off and on.
- `buttons-03` to `buttons-07-style-keys-*`: the other 53 keyed styles (46 rendered; 7 not found by key, below)
  targeting Button, ButtonBase, RepeatButton, ToggleButton, DropDownButton,
  SplitButton, AppBarButton, AppBarToggleButton and CheckBox, in
  `generic.xaml`'s order by type. `tools/button-styles.py` lists them from
  the package's `lib/native/Microsoft.UI/Themes/generic.xaml`.

How the states were made: pointer over and pressed can't happen offscreen, so
each cell's control was put in that state with `VisualStateManager.GoToState`,
using its template's own state names (Normal/PointerOver/Pressed for most;
PrimaryPointerOver/SecondaryPressed and Checked... for SplitButton;
UncheckedPointerOver, CheckedPressed... for CheckBox; Checked... for the
toggles when on). Disabled is `IsEnabled = false`, letting the control pick
its own disabled state. Every state asked for was found, in both themes
(`render-notes-dark.txt` and `-light.txt` list only the styles that couldn't
be created). The page's theme is the page's `RequestedTheme`; every brush on
it, the labels and background included, is a `{ThemeResource}`, so it
follows that theme rather than the app's (a lookup in
`Application.Resources` from code would follow the app's). Re-rendered after
that change, the dark pages match the first dark render to within 27 of 255
on 126 pixels of one page (anti-aliasing), and are otherwise identical.

What each style key shows as content (`tools/style-usage.py`): where
`generic.xaml` itself uses the style with content, that content (the
ellipsis, scroll and picker glyphs, NavigationView's "More"); for the styles
whose text is drawn in the icon font and whose glyph their control sets in
code (back, pane toggle, search, close, PipsPager, caption), nothing, and the
row says so, since the sample word would draw as boxes; otherwise plain
sample text, which fixed-size styles (InfoBarCloseButtonStyle, EllipsisButton
style family) clip, as they would any text.

Not shown:
- Seven keys aren't found by key at run time (XAML parse error 0x802B000A,
  and absent from Application.Resources): DatePickerFlyoutButtonStyle and
  TimePickerFlyoutButtonStyle (inside the DatePicker and TimePicker
  templates), AppBarButtonStyle, AppBarToggleButtonStyle and
  MediaControlAppBarButtonStyle (inside MediaTransportControls' template),
  and TabViewCloseButtonStyle and InkToolbarCommonButtonStyle, which
  `generic.xaml` lists at top level but which still weren't found. Their rows
  say so.
- Menu items with check or radio behaviour (ToggleMenuFlyoutItem,
  RadioMenuFlyoutItem), the RadioButtons container, and the InkToolbar*Button
  types: menu items and InkToolbar parts rather than buttons.
- The Windows Community Toolkit's SettingsCard and SettingsExpander: the
  toolkit's newest stable SettingsControls (8.2.251219) depends on Windows App
  SDK 1.6, whose build files clash with 2.6.5's ("The MSIX build tools use
  the 'CustomBeforeMicrosoftCommonTargets' MSBuild property..."), so they
  couldn't be added without taking the spike back to 1.x.
- Reveal styles are WinUI 2's and are included because WinUI 3 still ships
  them; the reveal effect itself isn't in WinUI 3.
