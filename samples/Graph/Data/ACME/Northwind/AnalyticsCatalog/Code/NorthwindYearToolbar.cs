// <meshweaver>
// Id: NorthwindYearToolbar
// DisplayName: Year Toolbar
// </meshweaver>

using MeshWeaver.Domain;

/// <summary>
/// Toolbar model for year filtering across Northwind views.
/// Year=0 defaults to the latest available year.
/// </summary>
public record NorthwindYearToolbar
{
    internal const string Years = "years";

    [Dimension<int>(Options = Years)] public int Year { get; init; }
}
