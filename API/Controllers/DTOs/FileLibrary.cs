using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using API.Schema.MangaContext;

namespace API.Controllers.DTOs;

public sealed record FileLibrary(string Key, string BasePath, string LibraryName, MediaType? DefaultForMediaType = null) : Identifiable(Key)
{
    /// <summary>
    /// The directory Path of the library
    /// </summary>
    [Required]
    [Description("The directory Path of the library")]
    public string BasePath { get; internal set; } = BasePath;

    /// <summary>
    /// The Name of the library
    /// </summary>
    [Required]
    [Description("The Name of the library")]
    public string LibraryName { get; internal set; } = LibraryName;

    /// <summary>
    /// If set, newly-added Manga of this MediaType are automatically assigned to this Library
    /// </summary>
    [Description("If set, newly-added Manga of this MediaType are automatically assigned to this Library")]
    public MediaType? DefaultForMediaType { get; internal set; } = DefaultForMediaType;
}