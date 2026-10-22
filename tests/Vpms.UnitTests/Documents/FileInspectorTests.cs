using System.IO.Compression;
using System.Text;
using Vpms.Documents.Domain;

namespace Vpms.UnitTests.Documents;

public class FileInspectorTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] Jpg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46];

    private static byte[] Zip(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("<x/>");
            }
        }

        return ms.ToArray();
    }

    [Theory]
    [InlineData("invoice.pdf", "application/pdf")]
    [InlineData("INVOICE.PDF", "application/pdf")]
    [InlineData("scan.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.JPEG", "image/jpeg")]
    public void Accepts_matching_content(string name, string expected)
    {
        var bytes = expected switch { "application/pdf" => Pdf, "image/png" => Png, _ => Jpg };
        Assert.Equal(expected, FileInspector.Inspect(name, bytes).ContentType);
    }

    [Fact]
    public void Accepts_office_packages_with_the_expected_structure()
    {
        Assert.Contains("wordprocessingml", FileInspector.Inspect("a.docx", Zip("[Content_Types].xml", "word/document.xml")).ContentType);
        Assert.Contains("spreadsheetml", FileInspector.Inspect("a.xlsx", Zip("[Content_Types].xml", "xl/workbook.xml")).ContentType);
    }

    [Theory]
    [InlineData("malware.exe")]
    [InlineData("script.js")]
    [InlineData("page.html")]
    [InlineData("noextension")]
    [InlineData("invoice.pdf.exe")]
    [InlineData("")]
    [InlineData(".pdf")]
    public void Rejects_types_that_are_not_allowed(string name)
    {
        var ex = Assert.Throws<FileRejectedException>(() => FileInspector.Inspect(name, Pdf));
        Assert.Equal("unsupported_type", ex.Code);
    }

    [Fact]
    public void Rejects_content_that_does_not_match_the_extension()
    {
        Assert.Equal("content_mismatch", Assert.Throws<FileRejectedException>(() => FileInspector.Inspect("a.pdf", Png)).Code);
        Assert.Equal("content_mismatch", Assert.Throws<FileRejectedException>(() => FileInspector.Inspect("a.png", Pdf)).Code);
        Assert.Equal("content_mismatch", Assert.Throws<FileRejectedException>(() => FileInspector.Inspect("a.pdf", "MZ\x90\0"u8)).Code);
    }

    [Fact]
    public void Rejects_office_files_that_are_plain_zips_or_the_wrong_kind()
    {
        Assert.Equal("content_mismatch", Assert.Throws<FileRejectedException>(() => FileInspector.Inspect("a.docx", Zip("hello.txt"))).Code);
        Assert.Equal("content_mismatch", Assert.Throws<FileRejectedException>(() => FileInspector.Inspect("a.docx", Zip("[Content_Types].xml", "xl/workbook.xml"))).Code);
        Assert.Equal("content_mismatch", Assert.Throws<FileRejectedException>(() => FileInspector.Inspect("a.xlsx", "PK\x03\x04garbage"u8)).Code);
    }

    [Fact]
    public void Rejects_empty_files() =>
        Assert.Equal("empty_file", Assert.Throws<FileRejectedException>(() => FileInspector.Inspect("a.pdf", ReadOnlySpan<byte>.Empty)).Code);

    [Theory]
    [InlineData("..\\..\\windows\\evil.pdf", "evil.pdf")]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("C:\\temp\\a.pdf", "a.pdf")]
    [InlineData("a\0b\r\n.pdf", "a_b__.pdf")]
    [InlineData("فاتورة مورد.pdf", "فاتورة مورد.pdf")]
    [InlineData("a:b*c?.pdf", "a_b_c_.pdf")]
    [InlineData("..hidden..pdf", "hidden.pdf")]
    public void Sanitises_file_names(string input, string expected) =>
        Assert.Equal(expected, FileInspector.SanitizeFileName(input));

    [Fact]
    public void Truncates_long_names_but_keeps_the_extension()
    {
        var name = FileInspector.SanitizeFileName(new string('a', 400) + ".pdf");
        Assert.Equal(150, name.Length);
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public void The_client_supplied_content_type_is_never_consulted() =>
        Assert.Equal("application/pdf", FileInspector.Inspect("a.pdf", Pdf).ContentType);
}
