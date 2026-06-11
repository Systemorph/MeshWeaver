// <meshweaver>
// Id: Currency
// DisplayName: Currency
// </meshweaver>

/// <summary>
/// Currency dimension (ISO 4217). Currencies are mesh nodes referenced by
/// path; the pension fund reports in CHF, so all balance-sheet facts carry
/// the CHF node's path.
/// </summary>
public record Currency
{
    /// <summary>ISO 4217 code, e.g. CHF.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Currency symbol for display.</summary>
    public string? Symbol { get; init; }

    /// <summary>Decimal places for formatting.</summary>
    public int DecimalPlaces { get; init; } = 2;
}
