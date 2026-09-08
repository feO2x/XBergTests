# XbergTests - minimal repro for XbergIo.Xberg 1.1.2

With `MimeDetectionPolicy.ContentOnly`, a legacy OLE2 Office document is detected
correctly when the caller passes the bytes, but the very same file passed as a URI
is rejected.

| File                    | Format             | `ExtractInputKind.Bytes`        | `ExtractInputKind.Uri` |
| ----------------------- | ------------------ | ------------------------------- | ---------------------- |
| `legacy-word.doc`       | Word 97-2003       | `application/msword`            | throws                 |
| `legacy-excel.xls`      | Excel 97-2003      | `application/vnd.ms-excel`      | throws                 |
| `legacy-powerpoint.ppt` | PowerPoint 97-2003 | `application/vnd.ms-powerpoint` | throws                 |

The URI path throws:

```
Xberg.ValidationException: Validation error: Could not detect MIME type from file content
```

## Running

```
dotnet test
```

Expect **3 passed, 3 failed**. The three failures are the bug - `DetectsMimeTypeFromUri`
asserts the same MIME types that `DetectsMimeTypeFromBytes` already produces. CI runs
the same suite on Linux, macOS and Windows and is red for the same reason.

Reproduced on .NET 10 with `XbergIo.Xberg` 1.1.0 and 1.1.2, on macOS (osx-arm64),
Linux (linux-x64) and Windows (win-x64).

## What distinguishes the two paths

The URI path sniffs a bounded prefix of the file; the byte-array path sees all of it.

An OLE2 / MS-CFB compound document cannot be typed from a prefix at all. Identifying
one means parsing its structure - following the FAT chain to the directory sector and
reading the root entry - and a truncated compound file is not parseable, because the
chain references sectors that are not present. The parser does not return a worse
answer, it returns none.

Feeding truncated prefixes of these files through the *byte-array* path, which is the
same detection routine, shows there is no partial credit:

| Prefix of `legacy-powerpoint.ppt` | Result                                    |
| --------------------------------- | ----------------------------------------- |
| 512, 1 024, 2 048, 4 096 B        | `Could not determine MIME type from bytes` |
| 8 192, 16 384, 32 768 B           | `Could not determine MIME type from bytes` |
| 41 984 B (the whole file)         | `application/vnd.ms-powerpoint`            |

So the cut-off is simply the size of the sniff buffer. Minimal compound files that fit
inside it whole are detected through either path; one byte larger and the URI path can
never succeed:

| Total file size | `Bytes` | `Uri`  |
| --------------- | ------- | ------ |
| 2 560 B         | OK      | OK     |
| 3 072 B         | OK      | OK     |
| 3 584 B         | OK      | OK     |
| 4 096 B         | OK      | OK     |
| 4 608 B         | OK      | throws |
| 5 120 B         | OK      | throws |

Every real Office document is far larger than 4 KB - the smallest thing Word, Excel or
PowerPoint will save is tens of kilobytes - so under `ContentOnly` the URI path fails
for all of them, without exception. It is not a question of unusual documents or of
where the directory sector happens to land.

## It is not about the file extension

All three files carry the extension matching their content, and all three still fail via
URI. Copying each to a misleading `.tmp` name changes nothing in either direction, so
this is not `ContentOnly` falling back to extension-based detection.

## About the test files

All three are ordinary documents saved from Microsoft Office for Mac in the legacy
97-2003 binary formats, containing nothing but a few lines of placeholder text.
