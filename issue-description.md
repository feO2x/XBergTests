# Title

```
bug: ContentOnly detection rejects every legacy OLE2 document given as a path, because the 4 KB sniff window truncates the compound file
```

Labels are applied by the template (`bug`).

---

## Description

With `MimeDetectionPolicy.ContentOnly`, a legacy OLE2 Office document (`.doc`, `.xls`,
`.ppt`) is detected correctly when the caller passes the bytes, but the very same file
passed as a path is rejected:

```
Xberg.ValidationException: Validation error: Could not detect MIME type from file content
```

Both inputs are the identical byte sequence, so both should resolve to the same MIME type.

The two `ContentOnly` branches in `crates/xberg/src/core/mime.rs` do not see the same
input (`main` @ `e74dca8accd9`):

```rust
// detect_or_validate_bytes - the whole buffer
MimeDetectionPolicy::ContentOnly => {
    detect_mime_type_from_bytes(content).and_then(|detected| validate_mime_type(&detected))
}

// detect_or_validate_file - a bounded prefix
MimeDetectionPolicy::ContentOnly => detect_mime_type_from_file_content(file, PackageInspection::FullArchive)
    .ok_or_else(|| XbergError::validation("Could not detect MIME type from file content".to_string()))
    .and_then(|detected| validate_mime_type(&detected)),
```

`detect_mime_type_from_file_content` sniffs `MIME_SNIFF_LENGTH = 4096` bytes and gives up
when that prefix is not conclusive:

```rust
let mut header = [0_u8; MIME_SNIFF_LENGTH];
let bytes_read = file.read(&mut header).ok()?;
...
let mut from_magic = match detect_mime_type_from_bytes_with_inspection(header, package_inspection) {
    Ok(detected) => detected,
    Err(_) if json_candidate => JSON_MIME_TYPE.to_string(),
    Err(_) => return None,
};
```

An OLE2 / MS-CFB compound file cannot be typed from a prefix at all. Identifying one
means parsing its structure - following the FAT chain to the directory sector and reading
the root entry - and a truncated compound file is not parseable, because the chain
references sectors that are not present. `detect_mime_type_from_bytes_with_inspection`
therefore returns `Err`, `detect_mime_type_from_file_content` returns `None`, and the
extraction is rejected.

The consequence is absolute rather than statistical: **no compound document larger than
`MIME_SNIFF_LENGTH` can ever be typed through the path API.** Only a file that fits inside
the sniff buffer whole is detected, and the smallest thing Word, Excel or PowerPoint will
save is an order of magnitude past that.

This is the same defect as #1426 ("a ZIP-based office document is typed as a plain ZIP when
its main part is written past the first 4 KB"), in the same function, for the other
container family. That fix taught the ZIP branch to consult the archive directory rather
than the byte window, and `detect_mime_type_from_file_content` still calls
`detect_zip_package` for exactly that reason. OLE2 has no equivalent escape hatch. The ZIP
case degraded to `application/zip`; the OLE2 case fails outright.

### Impact

Every legacy Office document large enough to be real is affected: with `ContentOnly` on the
path API, `.doc`, `.xls` and `.ppt` are rejected outright rather than misrouted. The
byte-array API on the same file succeeds, so the behaviour depends on which overload the
caller reaches for.

`PreferContent` (the default) masks this whenever the extension happens to be correct,
which is why it shows up under `ContentOnly` - precisely the policy chosen by callers who
must not trust the extension, per #1509.

## Steps to reproduce

Minimal repro, three files, both input kinds:
**<https://github.com/feO2x/XBergTests>**

```bash
git clone https://github.com/feO2x/XBergTests
cd XBergTests
dotnet test
```

Expected: 6 passed. Actual: **3 passed, 3 failed** - every `DetectsMimeTypeFromUri` case
fails, while the `DetectsMimeTypeFromBytes` case for the same file passes.

```
Failed DetectsMimeTypeFromUri(fileName: "legacy-word.doc",       expectedMimeType: "application/msword")
Failed DetectsMimeTypeFromUri(fileName: "legacy-excel.xls",      expectedMimeType: "application/vnd.ms-excel")
Failed DetectsMimeTypeFromUri(fileName: "legacy-powerpoint.ppt", expectedMimeType: "application/vnd.ms-powerpoint")
  Xberg.ValidationException : Validation error: Could not detect MIME type from file content
```

| File                    | Size     | `Bytes`                         | `Uri`  |
| ----------------------- | -------- | ------------------------------- | ------ |
| `legacy-word.doc`       | 25 088 B | `application/msword`            | throws |
| `legacy-excel.xls`      | 26 112 B | `application/vnd.ms-excel`      | throws |
| `legacy-powerpoint.ppt` | 41 984 B | `application/vnd.ms-powerpoint` | throws |

### Confirming the cause

Truncated prefixes of `legacy-powerpoint.ppt` fed through the *byte-array* path - the same
detection routine, with the truncation made explicit - show there is no partial credit:

| Prefix                            | Result                                    |
| --------------------------------- | ----------------------------------------- |
| 512, 1 024, 2 048, 4 096 B        | `Could not determine MIME type from bytes` |
| 8 192, 16 384, 32 768 B           | `Could not determine MIME type from bytes` |
| 41 984 B (whole file)             | `application/vnd.ms-powerpoint`            |

And minimal compound files built either side of the buffer size confirm that total size,
not layout, is the variable - these all place the directory sector last:

| Total file size | `Bytes` | `Uri`  |
| --------------- | ------- | ------ |
| 2 560 B         | OK      | OK     |
| 3 072 B         | OK      | OK     |
| 3 584 B         | OK      | OK     |
| 4 096 B         | OK      | OK     |
| 4 608 B         | OK      | throws |
| 5 120 B         | OK      | throws |

### Not the file extension

All three files carry the extension matching their content and still fail. Copying each to
a misleading `.tmp` name changes nothing in either direction, so this is not `ContentOnly`
falling back to extension-based detection.

## Environment

- `XbergIo.Xberg` **1.1.2**; also reproduced on 1.1.0
- .NET 10, xunit 2.9.3
- macOS 26.6.2 (osx-arm64); CI reproduces on ubuntu-latest (linux-x64) and windows-latest (win-x64)
- Source references against `main` @ `e74dca8accd9`

## Relevant files and configuration

```csharp
private static ExtractionConfig CreateContentOnlyConfig()
{
    return new ()
    {
        DisableOcr = true,
        MimeDetectionPolicy = MimeDetectionPolicy.ContentOnly
    };
}

// passes
var bytes = await File.ReadAllBytesAsync(path);
var result = await XbergConverter.ExtractAsync(
    new () { Kind = ExtractInputKind.Bytes, Bytes = bytes },
    CreateContentOnlyConfig());

// throws ValidationException on the same file
var result = await XbergConverter.ExtractAsync(
    new () { Kind = ExtractInputKind.Uri, Uri = path },
    CreateContentOnlyConfig());
```

All three files are ordinary documents saved from Microsoft Office for Mac in the legacy
97-2003 binary formats, containing nothing but a few lines of placeholder text.

## Suggested fix

Give the compound-file reader the file rather than a prefix, mirroring what #1426 did for
ZIP: when the header's first 8 bytes are the CFB signature `D0 CF 11 E0 A1 B1 1A E1`, parse
the container from the `File` handle the function already holds - it already re-opens the
file for `detect_zip_package` a few lines below - and read the root entry's CLSID.

A more general alternative is to fall back to a full-file sniff whenever the prefix is
inconclusive and the input is seekable, which would cover any other container that cannot
be identified from a fixed window. Keeping the bounded read for the in-memory path is
correct and cheaper, as #1426 noted.

---

*Filed with AI assistance, per the AI policy in CONTRIBUTING.md. The repro, the boundary
measurements and the source references were verified by running them.*
