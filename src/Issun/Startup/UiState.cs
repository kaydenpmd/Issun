using System.IO;
using System.Text.Json;
using Issun.Core;

namespace Issun.Startup;

/// <summary>
/// What the window remembers about itself between runs — not a setting, so
/// not in settings.json. Today that is one thing: whether Issun has already
/// explained that closing the window leaves it running in the tray. Said once,
/// ever, rather than at every close: autostart means the window is opened and
/// closed a lot, and a reminder each time would be nagging.
///
/// Kept in the host's DataFolder, which for --demo is a temp folder, so a demo
/// never writes into the real one.
/// </summary>
public sealed class UiState
{
    private sealed record Data(bool TrayHintShown);

    private readonly string _path;
    private Data _data;

    private UiState(string path, Data data)
    {
        _path = path;
        _data = data;
    }

    public bool TrayHintShown => _data.TrayHintShown;

    public static UiState Load(string folder)
    {
        var path = Path.Combine(folder, "window.json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) is { } data)
                return new UiState(path, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Worst case the tray hint is shown a second time; not worth more than a line.
            Log.Write($"[ui] couldn't read {path} ({ex.Message}); starting from defaults");
        }
        return new UiState(path, new Data(false));
    }

    public void MarkTrayHintShown()
    {
        _data = _data with { TrayHintShown = true };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"[ui] couldn't save {_path} ({ex.Message})");
        }
    }
}
