using System.IO.Compression;
using System.Text;

namespace Vpms.Documents.Domain;

public sealed record DetectedFile(string SafeName, string ContentType);

/// <summary>
/// Decides what a file really is. The client-supplied content type is ignored: the extension must be allowed and the
/// leading bytes (and, for Office files, the package structure) must match it.
/// </summary>
public static class FileInspector
{
    private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly IReadOnlyDictionary<string, string> AllowedExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".docx"] = Docx,
        [".xlsx"] = Xlsx,
    };

    public static IReadOnlyCollection<string> AllowedContentTypes => AllowedExtensions.Values.Distinct().ToList();

    /// <summary>Keeps letters (including Arabic), digits and a few punctuation marks; drops paths and control characters.</summary>
    public static string SanitizeFileName(string? name)
    {
        var leaf = (name ?? "").Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..].Trim();
        var builder = new StringBuilder();
        foreach (var rune in leaf.EnumerateRunes())
        {
            builder.Append(Rune.IsLetterOrDigit(rune) || rune.Value is '.' or '-' or '_' or ' ' or '(' or ')' ? rune.ToString() : "_");
        }

        var cleaned = builder.ToString().Trim('.', ' ');
        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        }

        if (cleaned.Length > 150)
        {
            var ext = Path.GetExtension(cleaned);
            cleaned = cleaned[..(150 - ext.Length)] + ext;
        }

        return cleaned;
    }

    /// <exception cref="FileRejectedException">The file type is not allowed or the content does not match it.</exception>
    public static DetectedFile Inspect(string? fileName, ReadOnlySpan<byte> content)
    {
        var safe = SanitizeFileName(fileName);
        var ext = Path.GetExtension(safe);
        if (safe.Length == 0 || ext.Length == 0 || !AllowedExtensions.TryGetValue(ext, out var contentType))
        {
            throw new FileRejectedException("unsupported_type", "This file type is not allowed.");
        }

        if (content.Length == 0)
        {
            throw new FileRejectedException("empty_file", "The file is empty.");
        }

        var matches = contentType switch
        {
            "application/pdf" => content.StartsWith("%PDF-"u8),
            "image/png" => content.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/jpeg" => content.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            Docx => IsOfficePackage(content, "word/"),
            Xlsx => IsOfficePackage(content, "xl/"),
            _ => false,
        };

        return matches
            ? new DetectedFile(safe, contentType)
            : throw new FileRejectedException("content_mismatch", "The file content does not match its type.");
    }

    private static bool IsOfficePackage(ReadOnlySpan<byte> content, string folder)
    {
        if (!content.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(content.ToArray());
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            // Names only: entries are never decompressed here, so zip bombs cannot be triggered.
            if (zip.Entries.Count is 0 or > 10_000)
            {
                return false;
            }

            return zip.Entries.Any(e => e.FullName == "[Content_Types].xml")
                   && zip.Entries.Any(e => e.FullName.StartsWith(folder, StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

public sealed class FileRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
