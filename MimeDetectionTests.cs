using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xberg;
using Xunit;

namespace XbergTests;

public sealed class MimeDetectionTests
{
    private static readonly string FilesDirectory = Path.Combine(AppContext.BaseDirectory, "Files");

    public static TheoryData<string, string> LegacyOfficeDocuments =>
        new ()
        {
            { "legacy-word.doc", "application/msword" },
            { "legacy-excel.xls", "application/vnd.ms-excel" },
            { "legacy-powerpoint.ppt", "application/vnd.ms-powerpoint" }
        };

    [Theory]
    [MemberData(nameof(LegacyOfficeDocuments))]
    public async Task DetectsMimeTypeFromBytes(string fileName, string expectedMimeType)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(FilesDirectory, fileName));
        var result = await XbergConverter.ExtractAsync(
            new ()
            {
                Kind = ExtractInputKind.Bytes,
                Bytes = bytes
            },
            CreateContentOnlyConfig()
        );

        Assert.Equal(expectedMimeType, result.Results.Single().MimeType);
    }

    // Same files, same ContentOnly policy, but passed as a URI instead of bytes:
    // Xberg 1.1.2 fails to detect the MIME type and throws
    //   Xberg.ValidationException: Validation error: Could not detect MIME type from file content
    // for all three documents.
    [Theory]
    [MemberData(nameof(LegacyOfficeDocuments))]
    public async Task DetectsMimeTypeFromUri(string fileName, string expectedMimeType)
    {
        var result = await XbergConverter.ExtractAsync(
            new ()
            {
                Kind = ExtractInputKind.Uri,
                Uri = Path.Combine(FilesDirectory, fileName)
            },
            CreateContentOnlyConfig()
        );

        Assert.Equal(expectedMimeType, result.Results.Single().MimeType);
    }

    private static ExtractionConfig CreateContentOnlyConfig()
    {
        return new ()
        {
            DisableOcr = true,
            MimeDetectionPolicy = MimeDetectionPolicy.ContentOnly
        };
    }
}
