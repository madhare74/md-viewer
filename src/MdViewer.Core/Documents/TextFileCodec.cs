using System.Text;

namespace MdViewer.Core.Documents;

/// <summary>Вид перевода строк в файле.</summary>
public enum LineEnding
{
    CrLf,
    Lf,
    Cr,
}

/// <summary>Результат чтения текстового файла.</summary>
public sealed record TextDecodeResult(string Text, Encoding Encoding, string EncodingName, bool HadBom, LineEnding LineEnding);

/// <summary>Файл не похож на текст (двоичный).</summary>
public sealed class BinaryFileException : Exception
{
    public BinaryFileException(string path)
        : base($"Файл не похож на текст: {path}")
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// Определение кодировки и переводов строк, чтение и запись текста.
/// Правила: BOM → по BOM; иначе строгий UTF-8; если не UTF-8 → Windows-1251.
/// Сохранение — всегда UTF-8 без BOM, переводы строк — как были в файле.
/// </summary>
public static class TextFileCodec
{
    public const long LargeFileThreshold = 50L * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    static TextFileCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding Windows1251
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251);
        }
    }

    /// <summary>Кодировки, которые можно выбрать вручную.</summary>
    public static IReadOnlyList<(string Name, Func<Encoding> Get)> ManualEncodings { get; } = new List<(string, Func<Encoding>)>
    {
        ("UTF-8", () => Utf8NoBom),
        ("Windows-1251", () => Windows1251),
        ("UTF-16 LE", () => Encoding.Unicode),
        ("UTF-16 BE", () => Encoding.BigEndianUnicode),
        ("KOI8-R", () => { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(20866); }),
        ("CP866 (DOS)", () => { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(866); }),
    };

    /// <summary>Похоже ли содержимое на двоичный файл (нулевые байты, много управляющих символов).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return false;
        }

        if (HasUtf16Bom(data) || HasUtf8Bom(data))
        {
            return false;
        }

        var sample = data.Length > 8192 ? data[..8192] : data;
        int control = 0;
        foreach (byte b in sample)
        {
            if (b == 0)
            {
                return true;
            }

            if (b < 32 && b != 9 && b != 10 && b != 13 && b != 12 && b != 27)
            {
                control++;
            }
        }

        return control > sample.Length / 10;
    }

    public static TextDecodeResult Decode(byte[] data, Encoding? forced = null)
    {
        string text;
        Encoding enc;
        string name;
        bool bom = false;

        if (forced is not null)
        {
            int skip = 0;
            if (forced is UTF8Encoding && HasUtf8Bom(data))
            {
                skip = 3;
                bom = true;
            }
            else if (forced.CodePage == 1200 && data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            {
                skip = 2;
                bom = true;
            }
            else if (forced.CodePage == 1201 && data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            {
                skip = 2;
                bom = true;
            }

            enc = forced;
            text = forced.GetString(data, skip, data.Length - skip);
            name = NameOf(forced, bom);
        }
        else if (HasUtf8Bom(data))
        {
            enc = Utf8NoBom;
            bom = true;
            text = Utf8NoBom.GetString(data, 3, data.Length - 3);
            name = "UTF-8 с BOM";
        }
        else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            enc = Encoding.Unicode;
            bom = true;
            text = Encoding.Unicode.GetString(data, 2, data.Length - 2);
            name = "UTF-16 LE";
        }
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            enc = Encoding.BigEndianUnicode;
            bom = true;
            text = Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            name = "UTF-16 BE";
        }
        else
        {
            try
            {
                text = StrictUtf8.GetString(data);
                enc = Utf8NoBom;
                name = "UTF-8";
            }
            catch (DecoderFallbackException)
            {
                enc = Windows1251;
                text = enc.GetString(data);
                name = "Windows-1251";
            }
        }

        return new TextDecodeResult(text, enc, name, bom, DetectLineEnding(text));
    }

    public static string NameOf(Encoding e, bool bom = false) => e.CodePage switch
    {
        65001 => bom ? "UTF-8 с BOM" : "UTF-8",
        1251 => "Windows-1251",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        20866 => "KOI8-R",
        866 => "CP866 (DOS)",
        _ => e.WebName,
    };

    /// <summary>Преобладающий вид перевода строк. Если переводов нет — CRLF (Windows).</summary>
    public static LineEnding DetectLineEnding(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (c == '\n')
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0)
        {
            return LineEnding.CrLf;
        }

        if (crlf >= lf && crlf >= cr)
        {
            return LineEnding.CrLf;
        }

        return lf >= cr ? LineEnding.Lf : LineEnding.Cr;
    }

    public static string NormalizeToLf(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    public static string ApplyLineEnding(string text, LineEnding ending)
    {
        string lf = NormalizeToLf(text);
        return ending switch
        {
            LineEnding.CrLf => lf.Replace("\n", "\r\n"),
            LineEnding.Cr => lf.Replace('\n', '\r'),
            _ => lf,
        };
    }

    /// <summary>Текст для сохранения: UTF-8 без BOM, переводы строк как в исходном файле.</summary>
    public static byte[] EncodeForSave(string text, LineEnding ending) =>
        Utf8NoBom.GetBytes(ApplyLineEnding(text, ending));

    public static string LineEndingName(LineEnding e) => e switch
    {
        LineEnding.CrLf => "CRLF",
        LineEnding.Lf => "LF",
        _ => "CR",
    };

    private static bool HasUtf8Bom(ReadOnlySpan<byte> d) => d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF;

    private static bool HasUtf16Bom(ReadOnlySpan<byte> d) =>
        d.Length >= 2 && ((d[0] == 0xFF && d[1] == 0xFE) || (d[0] == 0xFE && d[1] == 0xFF));
}
