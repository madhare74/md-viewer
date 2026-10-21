namespace MdViewer.Core.Documents;

/// <summary>Куда сохранить картинку из буфера обмена и какую ссылку вставить в текст.</summary>
public sealed record PastedImageTarget(string FullPath, string RelativePath, string MarkdownLink);

public static class PastedImages
{
    public const string FolderName = "images";

    /// <summary>
    /// Картинка сохраняется PNG-файлом в папку images рядом с документом:
    /// images/image-ГГГГММДД-ЧЧММСС.png, при совпадении имени — с суффиксом -2, -3…
    /// </summary>
    public static PastedImageTarget Allocate(string documentPath, DateTime now, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        string docDir = Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? throw new ArgumentException("Нет папки документа", nameof(documentPath));
        string dir = Path.Combine(docDir, FolderName);
        string baseName = $"image-{now:yyyyMMdd-HHmmss}";
        string name = baseName + ".png";
        for (int i = 2; exists(Path.Combine(dir, name)); i++)
        {
            name = $"{baseName}-{i}.png";
        }

        string rel = FolderName + "/" + name;
        return new PastedImageTarget(Path.Combine(dir, name), rel, MarkdownImage(rel, "картинка"));
    }

    /// <summary>Относительный путь от папки документа до картинки (через «/»).</summary>
    public static string RelativePath(string documentPath, string imagePath)
    {
        string docDir = Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? string.Empty;
        string rel = Path.GetRelativePath(docDir, Path.GetFullPath(imagePath));
        return rel.Replace('\\', '/');
    }

    public static string MarkdownImage(string relativePath, string alt)
    {
        string target = relativePath.Contains(' ') || relativePath.Contains('(') || relativePath.Contains(')')
            ? "<" + relativePath + ">"
            : relativePath;
        return $"![{alt}]({target})";
    }
}
