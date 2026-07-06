using System.IO.Compression;
using System.Text;

namespace API.Workers.MangaDownloadWorkers;

/// <summary>
/// Builds a minimal, valid single-chapter EPUB3 file - used for LightNovel/WebNovel Chapters,
/// mirroring the way Manga Chapters are packaged as single-chapter .cbz archives.
/// </summary>
internal static class EpubBuilder
{
    /// <summary>
    /// Writes a single-chapter epub to <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">Full path (including .epub extension) to write to. Overwritten if it exists.</param>
    /// <param name="bookTitle">Series title (used as epub metadata title)</param>
    /// <param name="chapterTitle">Chapter title/number, shown as the chapter heading</param>
    /// <param name="author">Author name, or null</param>
    /// <param name="xhtmlBodyContent">
    /// The chapter content as an XHTML body fragment (e.g. a series of &lt;p&gt; tags).
    /// Should NOT include &lt;html&gt;/&lt;body&gt; wrapper tags - those are added automatically.
    /// </param>
    public static void CreateSingleChapterEpub(string filePath, string bookTitle, string chapterTitle, string? author, string xhtmlBodyContent)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);

        string uid = Guid.NewGuid().ToString();
        string safeBookTitle = System.Security.SecurityElement.Escape(bookTitle) ?? bookTitle;
        string safeChapterTitle = System.Security.SecurityElement.Escape(chapterTitle) ?? chapterTitle;
        string safeAuthor = string.IsNullOrWhiteSpace(author) ? "Unknown" : (System.Security.SecurityElement.Escape(author) ?? author);

        using ZipArchive archive = ZipFile.Open(filePath, ZipArchiveMode.Create);

        // 1) mimetype - MUST be first entry, uncompressed, no extra fields
        ZipArchiveEntry mimetypeEntry = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (Stream s = mimetypeEntry.Open())
        {
            byte[] bytes = Encoding.ASCII.GetBytes("application/epub+zip");
            s.Write(bytes, 0, bytes.Length);
        }

        // 2) META-INF/container.xml
        WriteEntry(archive, "META-INF/container.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);

        // 3) OEBPS/content.opf - package metadata + manifest + spine
        WriteEntry(archive, "OEBPS/content.opf", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="BookId">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="BookId">urn:uuid:{uid}</dc:identifier>
                <dc:title>{safeBookTitle} - {safeChapterTitle}</dc:title>
                <dc:creator>{safeAuthor}</dc:creator>
                <dc:language>en</dc:language>
                <meta property="dcterms:modified">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>
              </metadata>
              <manifest>
                <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
              </manifest>
              <spine>
                <itemref idref="chapter"/>
              </spine>
            </package>
            """);

        // 4) OEBPS/nav.xhtml - EPUB3 navigation document (required)
        WriteEntry(archive, "OEBPS/nav.xhtml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
            <head><title>Navigation</title></head>
            <body>
              <nav epub:type="toc">
                <ol>
                  <li><a href="chapter.xhtml">{safeChapterTitle}</a></li>
                </ol>
              </nav>
            </body>
            </html>
            """);

        // 5) OEBPS/chapter.xhtml - the actual chapter content
        WriteEntry(archive, "OEBPS/chapter.xhtml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml">
            <head><title>{safeChapterTitle}</title></head>
            <body>
              <h1>{safeChapterTitle}</h1>
              {xhtmlBodyContent}
            </body>
            </html>
            """);
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
