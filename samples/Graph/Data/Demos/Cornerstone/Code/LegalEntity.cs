// <meshweaver>
// Id: LegalEntity
// DisplayName: Legal Entity Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;


/// <summary>
/// Legal entity dimension for organizational structure.
/// </summary>
public record LegalEntity : INamed
{
    /// <summary>
    /// Legal entity identifier.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Full legal name of the entity.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Country of incorporation.
    /// </summary>
    public string? CountryOfIncorporation { get; init; }

    /// <summary>
    /// Entity type (e.g., Corporation, LLC, Partnership).
    /// </summary>
    public string? EntityType { get; init; }

    /// <summary>
    /// Display order in lists.
    /// </summary>
    public int Order { get; init; }

    string INamed.DisplayName => Name;

    public static readonly LegalEntity CornerstoneUS = new()
    {
        Id = "CS-US",
        Name = "Cornerstone Insurance US Inc.",
        CountryOfIncorporation = "US",
        EntityType = "Corporation",
        Order = 0
    };

    public static readonly LegalEntity CornerstoneUK = new()
    {
        Id = "CS-UK",
        Name = "Cornerstone Insurance UK Ltd.",
        CountryOfIncorporation = "GB",
        EntityType = "Limited Company",
        Order = 1
    };

    public static readonly LegalEntity CornerstoneEU = new()
    {
        Id = "CS-EU",
        Name = "Cornerstone Insurance Europe AG",
        CountryOfIncorporation = "CH",
        EntityType = "Corporation",
        Order = 2
    };

    public static readonly LegalEntity CornerstoneAsia = new()
    {
        Id = "CS-ASIA",
        Name = "Cornerstone Insurance Asia Pte. Ltd.",
        CountryOfIncorporation = "SG",
        EntityType = "Private Limited",
        Order = 3
    };

    public static readonly LegalEntity MeshWeaverUS = new()
    {
        Id = "MW-US",
        Name = "MeshWeaver Insurance US Inc.",
        CountryOfIncorporation = "US",
        EntityType = "Corporation",
        Order = 4
    };

    public static readonly LegalEntity MeshWeaverUK = new()
    {
        Id = "MW-UK",
        Name = "MeshWeaver Insurance UK Ltd.",
        CountryOfIncorporation = "GB",
        EntityType = "Limited Company",
        Order = 5
    };

    public static readonly LegalEntity MeshWeaverEU = new()
    {
        Id = "MW-EU",
        Name = "MeshWeaver Insurance Europe AG",
        CountryOfIncorporation = "DE",
        EntityType = "Corporation",
        Order = 6
    };

    public static readonly LegalEntity[] All = [CornerstoneUS, CornerstoneUK, CornerstoneEU, CornerstoneAsia, MeshWeaverUS, MeshWeaverUK, MeshWeaverEU];

    public static LegalEntity GetById(string? id) =>
        All.FirstOrDefault(e => e.Id == id) ?? CornerstoneUS;
}
