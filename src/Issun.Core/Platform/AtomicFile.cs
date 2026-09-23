using System.Text;

namespace Issun.Core.Platform;

/// <summary>
/// Whole-file replacement that a crash or power cut cannot leave half-done:
/// write a temporary file beside the target, flush it to the disk, then rename
/// it over the target in one step.
/// </summary>
internal static class AtomicFile
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const int ReplaceAttempts = 5;

    /// <param name="stillCurrent">
    /// Checked after the new contents are safely on disk and immediately before
    /// the rename; returning false abandons the write and leaves the target as it
    /// was. Lets a read-modify-write caller notice that someone else wrote the
    /// file in the meantime, with the window narrowed to the rename itself
    /// rather than the whole write and flush.
    /// </param>
    /// <returns>False only when <paramref name="stillCurrent"/> said no.</returns>
    public static bool WriteAllText(string path, string contents, Func<bool>? stillCurrent = null)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);

        // Same folder, so the rename never crosses a volume and stays atomic.
        // Hidden-ish and unique, so two writers can't share a temp file and a
        // leftover from a crash is recognisable for what it is.
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(Utf8NoBom.GetBytes(contents));
                // Without this, NTFS can journal the rename before the data
                // reaches the disk, and a power cut leaves a zero-length file
                // under the real name. For settings.json that reads as corrupt
                // on the next start and costs a fresh key — a re-pair of Ammy.
                fs.Flush(flushToDisk: true);
            }
            if (stillCurrent is not null && !stillCurrent())
                return false;
            Replace(temp, full);
            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"[config] could not remove temporary file {temp}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// MoveFileEx with MOVEFILE_REPLACE_EXISTING. Retried briefly because
    /// antivirus scanners and cloud-sync clients (%APPDATA% roams, and OneDrive
    /// likes to look at new files) hold a fresh file open for a moment, which
    /// surfaces as a sharing violation or access denied that clears on its own.
    /// </summary>
    private static void Replace(string temp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < ReplaceAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
