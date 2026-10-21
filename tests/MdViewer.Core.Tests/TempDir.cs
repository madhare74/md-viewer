namespace MdViewer.Core.Tests;

/// <summary>Временная папка, удаляемая после теста.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdv-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch
        {
            // ignore
        }
    }
}

public static class Samples
{
    public static string Dir => System.IO.Path.Combine(AppContext.BaseDirectory, "samples");

    public static string Read(string name) => System.IO.File.ReadAllText(System.IO.Path.Combine(Dir, name));
}
