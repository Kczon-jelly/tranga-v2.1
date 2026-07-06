using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace API.Schema.MangaContext;

[PrimaryKey("Key")]
public class FileLibrary(string basePath, string libraryName)
    : Identifiable(TokenGen.CreateToken(typeof(FileLibrary), basePath))
{
    [StringLength(256)] public string BasePath { get; internal set; } = basePath;

    [StringLength(512)] public string LibraryName { get; internal set; } = libraryName;

    /// <summary>
    /// If set, newly-added Manga of this MediaType will automatically be assigned to this Library.
    /// Only one FileLibrary per MediaType should have this set; if multiple do, the first match is used.
    /// </summary>
    public MediaType? DefaultForMediaType { get; internal set; } = null;

    public override string ToString() => $"{base.ToString()} {LibraryName} - {BasePath}";
}