namespace MdViewer.Core.Documents;

public static class CommandLine
{
    /// <summary>Пути файлов из аргументов командной строки (ключи вида --xxx пропускаются).</summary>
    public static IReadOnlyList<string> ParseFiles(IEnumerable<string> args, string currentDirectory)
    {
        var list = new List<string>();
        foreach (string raw in args)
        {
            string a = raw.Trim().Trim('"');
            if (a.Length == 0 || a.StartsWith("--", StringComparison.Ordinal) || (a.StartsWith('/') && a.Length < 4 && !a.Contains('.')))
            {
                continue;
            }

            if (a.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(a, UriKind.Absolute, out var uri))
            {
                a = uri.LocalPath;
            }

            string full = Path.IsPathRooted(a) ? a : Path.Combine(currentDirectory, a);
            try
            {
                full = Path.GetFullPath(full);
            }
            catch (Exception)
            {
                continue;
            }

            if (!list.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(full);
            }
        }

        return list;
    }
}
