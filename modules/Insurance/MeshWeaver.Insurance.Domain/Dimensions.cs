using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Line of business dimension for insurance classification.
/// </summary>
public record LineOfBusiness
{
    /// <summary>
    /// Line of business code.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Display name for the line of business.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Description of the line of business.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Country dimension for geographic classification.
/// </summary>
public record Country
{
    /// <summary>
    /// Country code (typically ISO 3166-1 alpha-2).
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Full country name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// ISO 3166-1 alpha-3 code.
    /// </summary>
    public string? Alpha3Code { get; init; }

    /// <summary>
    /// Geographic region.
    /// </summary>
    public string? Region { get; init; }
}

/// <summary>
/// Legal entity dimension for organizational structure.
/// </summary>
public record LegalEntity
{
    /// <summary>
    /// Legal entity identifier.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Full legal name of the entity.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Country of incorporation.
    /// </summary>
    public string? CountryOfIncorporation { get; init; }

    /// <summary>
    /// Entity type (e.g., Corporation, LLC, Partnership).
    /// </summary>
    public string? EntityType { get; init; }
}

/// <summary>
/// Currency dimension for monetary values.
/// </summary>
public record Currency
{
    /// <summary>
    /// Currency code (ISO 4217).
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Full currency name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Currency symbol.
    /// </summary>
    public string? Symbol { get; init; }

    /// <summary>
    /// Number of decimal places typically used.
    /// </summary>
    public int? DecimalPlaces { get; init; }
}
