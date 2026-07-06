using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using API.Schema.MangaContext;

namespace API.Controllers.Requests;

public sealed record CreateLibraryRecord
{
    /// <summary>
    /// The directory Path of the library
    /// </summary>
    [Required]
    [Description("The directory Path of the library")]
    public required string BasePath { get; init; }
    
    /// <summary>
    /// The Name of the library
    /// </summary>
    [Required]
    [Description("The Name of the library")]
    public required string LibraryName { get; init; }

    /// <summary>
    /// If set, newly-added Manga of this MediaType are automatically assigned to this Library
    /// </summary>
    [Description("If set, newly-added Manga of this MediaType are automatically assigned to this Library")]
    public MediaType? DefaultForMediaType { get; init; } = null;
}