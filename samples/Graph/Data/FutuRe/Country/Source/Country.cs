// <meshweaver>
// Id: Country
// DisplayName: Country Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Country dimension for geographic classification in profitability reporting.
/// </summary>
public record Country : INamed
{
    /// <summary>
    /// ISO 3166-1 alpha-2 country code (e.g. US, GB).
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Full country name.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// ISO 3166-1 alpha-3 country code (e.g. USA, GBR).
    /// </summary>
    public string? Alpha3Code { get; init; }

    /// <summary>
    /// Geographic region (e.g. Americas, EMEA, Asia-Pacific).
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Sort order for display.
    /// </summary>
    public int Order { get; init; }

    /// <inheritdoc />
    string INamed.DisplayName => Name;
}
