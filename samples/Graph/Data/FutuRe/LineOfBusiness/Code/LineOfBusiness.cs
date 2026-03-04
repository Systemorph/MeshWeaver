// <meshweaver>
// Id: LineOfBusiness
// DisplayName: Line of Business
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Line of business dimension for insurance/reinsurance classification.
/// Each line of business represents a distinct category of risk that insurers
/// and reinsurers underwrite, with its own regulatory framework, actuarial
/// models, and market dynamics.
/// </summary>
public record LineOfBusiness : Dimension
{
    /// <summary>
    /// Detailed description of the line of business, including the types of
    /// risks covered and typical policy structures.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Display order in lists and reports.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Comma-separated list of typical insurance products mapped to this
    /// line of business, used for categorizing legal entity portfolios.
    /// </summary>
    public string? ProductExamples { get; init; }
}
