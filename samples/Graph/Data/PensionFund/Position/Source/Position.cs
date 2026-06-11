// <meshweaver>
// Id: Position
// DisplayName: Balance Sheet Position
// </meshweaver>

using MeshWeaver.Domain;

/// <summary>
/// Which side of the balance sheet a position belongs to.
/// </summary>
public enum BalanceSheetSide
{
    Assets,
    Liabilities,
    /// <summary>Computed positions (sums, ratios) — not on either side.</summary>
    Computed,
}

/// <summary>
/// How a position's value is obtained.
/// </summary>
public enum PositionAggregation
{
    /// <summary>Value comes from a BalanceSheetEntry fact node.</summary>
    Atomic,
    /// <summary>Value = Σ Weight·Value(Component) over <see cref="Position.Components"/>.</summary>
    Sum,
    /// <summary>Value = Value(Numerator) / Value(Denominator).</summary>
    Ratio,
}

/// <summary>
/// One weighted operand of a computed (Sum) position. The component references
/// another Position NODE by its mesh path — computed positions are modelled
/// out of other positions, all the way down to the atomic ones.
/// </summary>
public record PositionComponent
{
    /// <summary>Mesh path of the referenced Position node.</summary>
    [MeshNode("nodeType:PensionFund/Position")]
    public string Position { get; init; } = string.Empty;

    /// <summary>Weight in the sum: +1 adds, -1 subtracts.</summary>
    public double Weight { get; init; } = 1;
}

/// <summary>
/// A balance-sheet position of a pension fund — the dimension that says what a
/// value MEANS. Positions are mesh nodes: their identity is the node path, so
/// facts and formulas reference them by path; name, description, and display
/// order live on the MESH NODE itself — the content carries only what the node
/// doesn't already have. Atomic positions take their value from fact entries;
/// computed positions (Total Assets, Balance Sheet Sum, Funding Ratio) define
/// a formula over other positions.
/// </summary>
public record Position
{
    /// <summary>Assets, Liabilities, or Computed.</summary>
    public BalanceSheetSide Side { get; init; }

    /// <summary>Atomic (from facts), Sum (weighted components), or Ratio.</summary>
    public PositionAggregation Aggregation { get; init; } = PositionAggregation.Atomic;

    /// <summary>
    /// Weighted operands for <see cref="PositionAggregation.Sum"/> positions —
    /// e.g. Balance Sheet Sum = 1·Cash + 1·Bonds + 1·Equities + ….
    /// </summary>
    public PositionComponent[]? Components { get; init; }

    /// <summary>Numerator position path for <see cref="PositionAggregation.Ratio"/>.</summary>
    [MeshNode("nodeType:PensionFund/Position")]
    public string? Numerator { get; init; }

    /// <summary>Denominator position path for <see cref="PositionAggregation.Ratio"/>.</summary>
    [MeshNode("nodeType:PensionFund/Position")]
    public string? Denominator { get; init; }
}
