using System.Text;

namespace MdViewer.Core.Documents;

/// <summary>Загруженный с диска документ.</summary>
public sealed record LoadedDocument(string Path, string Text, string EncodingName, Encoding Encoding, LineEnding LineEnding, DateTime LastWriteUtc, long Length);

/// <summary>Чтение и сохранение файлов Markdown.</summary>
public static class DocumentFile
{
    public static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdwn", ".mdtxt"];

    public static bool IsMarkdownPath(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        return MarkdownExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Читает файл. Бросает FileNotFoundException, UnauthorizedAccessException, IOException, BinaryFileException.</summary>
    public static LoadedDocument Load(string path, Encoding? forcedEncoding = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Файл не найден", path);
        }

        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = ReadAll(fs);
        }

        if (forcedEncoding is null && TextFileCodec.LooksBinary(data))
        {
            throw new BinaryFileException(path);
        }

        var decoded = TextFileCodec.Decode(data, forcedEncoding);
        return new LoadedDocument(info.FullName, decoded.Text, decoded.EncodingName, decoded.Encoding, decoded.LineEnding, info.LastWriteTimeUtc, data.LongLength);
    }

    /// <summary>Читает текст файла для сравнения при внешнем изменении (null — файла нет или он недоступен).</summary>
    public static string? TryReadText(string path, Encoding? encoding = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] data;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                data = ReadAll(fs);
            }

            return TextFileCodec.Decode(data, encoding).Text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Сохраняет в UTF-8 без BOM с нужными переводами строк.</summary>
    public static void Save(string path, string text, LineEnding lineEnding)
    {
        byte[] bytes = TextFileCodec.EncodeForSave(text, lineEnding);
        string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(true);
    }

    public static long GetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
