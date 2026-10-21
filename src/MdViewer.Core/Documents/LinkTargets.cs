namespace MdViewer.Core.Documents;

public enum LocalLinkKind
{
    /// <summary>Другой документ Markdown — открыть вкладкой.</summary>
    Markdown,

    /// <summary>Другой файл — открыть программой по умолчанию.</summary>
    OtherFile,

    /// <summary>Файл не найден.</summary>
    Missing,
}

public static class LinkTargets
{
    /// <summary>Разрешает ссылку из документа (относительный путь) в полный путь на диске.</summary>
    public static string Resolve(string documentDirectory, string relativePath)
    {
        string p = relativePath.Replace('/', Path.DirectorySeparatorChar);
        int q = p.IndexOfAny(['?', '#']);
        if (q >= 0)
        {
            p = p[..q];
        }

        return Path.GetFullPath(Path.Combine(documentDirectory, p));
    }

    public static LocalLinkKind Classify(string fullPath, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!exists(fullPath))
        {
            return LocalLinkKind.Missing;
        }

        return DocumentFile.IsMarkdownPath(fullPath) ? LocalLinkKind.Markdown : LocalLinkKind.OtherFile;
    }

    /// <summary>Безопасна ли внешняя ссылка для открытия в браузере.</summary>
    public static bool IsSafeExternal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto);
}
