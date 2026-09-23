namespace Issun.Core.Tests.Platform;

/// <summary>A folder of its own for one test, removed afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IssunTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Something still has a file open; the OS temp cleaner gets it later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
