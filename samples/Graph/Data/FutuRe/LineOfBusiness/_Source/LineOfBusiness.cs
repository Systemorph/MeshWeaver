// <meshweaver>
// Id: LineOfBusiness
// DisplayName: Line of Business
// </meshweaver>

using MeshWeaver.Layout;

/// <summary>
/// Line of business dimension for insurance/reinsurance classification.
/// Each line of business represents a distinct category of risk that insurers
/// and reinsurers underwrite, with its own regulatory framework, actuarial
/// models, and market dynamics.
/// </summary>
public record LineOfBusiness
{
    /// <summary>
    /// Detailed description of the line of business, including the types of
    /// risks covered and typical policy structures.
    /// </summary>
    [Markdown]
    public string? Description { get; init; }
}
