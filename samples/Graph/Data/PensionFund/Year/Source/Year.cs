// <meshweaver>
// Id: Year
// DisplayName: Reporting Year
// </meshweaver>

/// <summary>
/// Reporting-year dimension. Years are mesh nodes so facts and reports
/// reference them by path — and a year node can carry its own context
/// (closing state, auditor sign-off, commentary).
/// </summary>
public record Year
{
    /// <summary>Calendar year, e.g. 2024.</summary>
    public int Value { get; init; }

    /// <summary>True once the reporting year is closed and audited.</summary>
    public bool Closed { get; init; }
}
