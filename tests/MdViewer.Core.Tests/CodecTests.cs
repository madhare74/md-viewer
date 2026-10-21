using System.Text;
using MdViewer.Core.Documents;

namespace MdViewer.Core.Tests;

public class CodecTests
{
    [Fact]
    public void Utf8WithoutBom_IsDetected()
    {
        var r = TextFileCodec.Decode(Encoding.UTF8.GetBytes("# Привет\nмир"));
        Assert.Equal("UTF-8", r.EncodingName);
        Assert.False(r.HadBom);
        Assert.Equal("# Привет\nмир", r.Text);
        Assert.Equal(LineEnding.Lf, r.LineEnding);
    }

    [Fact]
    public void Utf8WithBom_BomIsStripped()
    {
        var data = File.ReadAllBytes(Path.Combine(Samples.Dir, "utf8-bom.md"));
        var r = TextFileCodec.Decode(data);
        Assert.True(r.HadBom);
        Assert.Equal("UTF-8 с BOM", r.EncodingName);
        Assert.StartsWith("# С BOM", r.Text);
        Assert.Equal(LineEnding.CrLf, r.LineEnding);
    }

    [Fact]
    public void Windows1251_IsUsedWhenNotUtf8()
    {
        var data = File.ReadAllBytes(Path.Combine(Samples.Dir, "cp1251.md"));
        var r = TextFileCodec.Decode(data);
        Assert.Equal("Windows-1251", r.EncodingName);
        Assert.Contains("Текст на русском языке.", r.Text);
    }

    [Fact]
    public void ForcedEncoding_IsApplied()
    {
        var data = Encoding.UTF8.GetBytes("Привет");
        var r = TextFileCodec.Decode(data, TextFileCodec.Windows1251);
        Assert.Equal("Windows-1251", r.EncodingName);
        Assert.NotEqual("Привет", r.Text);
    }

    [Fact]
    public void Utf16_IsDetectedByBom()
    {
        var data = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("# Заголовок\r\n")).ToArray();
        var r = TextFileCodec.Decode(data);
        Assert.Equal("UTF-16 LE", r.EncodingName);
        Assert.Equal("# Заголовок\r\n", r.Text);
        Assert.False(TextFileCodec.LooksBinary(data));
    }

    [Fact]
    public void BinaryFile_IsDetected()
    {
        Assert.True(TextFileCodec.LooksBinary(File.ReadAllBytes(Path.Combine(Samples.Dir, "binary.bin"))));
        Assert.True(TextFileCodec.LooksBinary(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }));
        Assert.False(TextFileCodec.LooksBinary(Encoding.UTF8.GetBytes("обычный\tтекст\r\n")));
        Assert.False(TextFileCodec.LooksBinary(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc", LineEnding.CrLf)]
    [InlineData("a\nb\nc", LineEnding.Lf)]
    [InlineData("a\rb\rc", LineEnding.Cr)]
    [InlineData("без переводов", LineEnding.CrLf)]
    [InlineData("a\r\nb\nc\nd", LineEnding.Lf)]
    public void LineEnding_IsDetected(string text, LineEnding expected)
    {
        Assert.Equal(expected, TextFileCodec.DetectLineEnding(text));
    }

    [Theory]
    [InlineData(LineEnding.CrLf, "a\r\nb\r\n")]
    [InlineData(LineEnding.Lf, "a\nb\n")]
    [InlineData(LineEnding.Cr, "a\rb\r")]
    public void Save_KeepsLineEndings_AndWritesUtf8WithoutBom(LineEnding le, string expected)
    {
        using var tmp = new TempDir();
        string path = tmp.File("x.md");
        DocumentFile.Save(path, "a\nb\n", le);
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void LoadAndSave_RoundTrip_PreservesCrLfFile()
    {
        using var tmp = new TempDir();
        string path = tmp.File("crlf.md");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("# T\r\n\r\nтекст\r\n"));
        var doc = DocumentFile.Load(path);
        Assert.Equal(LineEnding.CrLf, doc.LineEnding);
        // Редактор работает с \n — при сохранении восстанавливается CRLF.
        DocumentFile.Save(path, TextFileCodec.NormalizeToLf(doc.Text) + "новое\n", doc.LineEnding);
        Assert.Equal("# T\r\n\r\nтекст\r\nновое\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Load_Cp1251File_SavesAsUtf8()
    {
        using var tmp = new TempDir();
        string path = tmp.File("ru.md");
        File.Copy(Path.Combine(Samples.Dir, "cp1251.md"), path);
        var doc = DocumentFile.Load(path);
        DocumentFile.Save(path, doc.Text, doc.LineEnding);
        var again = DocumentFile.Load(path);
        Assert.Equal("UTF-8", again.EncodingName);
        Assert.Equal(doc.Text, again.Text);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => DocumentFile.Load(Path.Combine(Path.GetTempPath(), "нет-такого-" + Guid.NewGuid() + ".md")));
    }

    [Fact]
    public void Load_BinaryFile_Throws()
    {
        Assert.Throws<BinaryFileException>(() => DocumentFile.Load(Path.Combine(Samples.Dir, "binary.bin")));
    }

    [Theory]
    [InlineData("a.md", true)]
    [InlineData("A.MARKDOWN", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.pdf", false)]
    public void MarkdownExtensions_AreRecognized(string path, bool expected)
    {
        Assert.Equal(expected, DocumentFile.IsMarkdownPath(path));
    }
}
