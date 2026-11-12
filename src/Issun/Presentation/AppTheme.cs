using Issun.Core;
using Windows.UI.ViewManagement;

namespace Issun.Presentation;

/// <summary>
/// Whether Issun is drawn dark: --theme when one was given, otherwise
/// Windows' own app mode, followed live. The QR code reads it, since it is
/// white on black in the dark theme (the owner, 12 Nov 2026). WPF answered
/// this from its theme's text colour; WinUI's Application.RequestedTheme
/// doesn't follow a change Windows makes while Issun runs, so this asks
/// Windows directly.
/// </summary>
public static class AppTheme
{
    private static UISettings? _settings;
    private static bool? _forced;

    /// <summary>Called once at start: true or false for --theme dark or light, null to follow Windows.</summary>
    public static void Init(bool? forcedDark)
    {
        _forced = forcedDark;
        if (forcedDark is null)
        {
            try
            {
                _settings = new UISettings();
            }
            catch (Exception ex)
            {
                Log.Write($"[ui] couldn't read Windows' colour settings: {ex.Message}");
            }
        }
    }

    public static bool IsDark
    {
        get
        {
            if (_forced is { } forced)
                return forced;
            try
            {
                // Windows' own test: the app background is black in dark mode.
                var c = (_settings ??= new UISettings()).GetColorValue(UIColorType.Background);
                return c.R + c.G + c.B < 3 * 128;
            }
            catch (Exception ex)
            {
                Log.Write($"[ui] couldn't read Windows' colour settings: {ex.Message}");
                return false;
            }
        }
    }
}
