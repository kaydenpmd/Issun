using Issun.Core;

namespace Issun.Startup;

/// <summary>
/// Deletes what earlier builds unpacked into %TEMP%\.net\Issun.
///
/// A single-file WinUI .exe unpacks everything, about 180 MB, into
/// %TEMP%\.net\Issun\&lt;hash&gt; the first time it runs (Windows App SDK
/// requires it; the winui3-spike's report, question 1), the hash changes with
/// every build, and .NET never deletes the old ones. So each update would
/// leave another 180 MB behind. On start, the installed copy removes every
/// sibling of its own folder that nothing is using.
///
/// "Nothing is using" is tested by renaming the folder first: Windows refuses
/// to rename a folder while a file in it is open, so a copy that is still
/// running (an old Issun during an update) keeps its folder, and nothing is
/// deleted from under it.
/// </summary>
public static class StaleUnpacks
{
    public static void CleanUpInBackground() => Task.Run(CleanUp);

    private static void CleanUp()
    {
        try
        {
            var own = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var parent = own.Parent;
            // Only a single-file run lives in ...\.net\Issun\<hash>; a build
            // run from bin\ has nothing to clean up, and must touch nothing.
            if (parent is null || !parent.Name.Equals("Issun", StringComparison.OrdinalIgnoreCase)
                || parent.Parent?.Name.Equals(".net", StringComparison.OrdinalIgnoreCase) != true)
                return;

            long freed = 0;
            var removed = 0;
            foreach (var dir in parent.GetDirectories())
            {
                if (dir.FullName.Equals(own.FullName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var doomed = dir.FullName + ".deleting";
                try
                {
                    if (!dir.Name.EndsWith(".deleting", StringComparison.Ordinal))
                        Directory.Move(dir.FullName, doomed);
                    else
                        doomed = dir.FullName;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Write($"[ui] leaving {dir.Name} in {parent.FullName}: in use");
                    continue;
                }
                var size = Size(new DirectoryInfo(doomed));
                try
                {
                    Directory.Delete(doomed, recursive: true);
                    freed += size;
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Write($"[ui] couldn't delete {doomed}: {ex.Message}");
                }
            }
            if (removed > 0)
                Log.Write($"[ui] deleted {removed} folder{(removed == 1 ? "" : "s")} earlier builds unpacked into {parent.FullName} ({freed / (1024 * 1024)} MB)");
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] couldn't clean up earlier builds' unpacked files: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static long Size(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
