// <meshweaver>
// Id: ExchangeRate
// DisplayName: Exchange Rate
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Exchange rate from one currency to another (e.g. EUR → USD).
/// Used at group aggregation time to convert local-currency amounts
/// into the group reporting currency (USD).
/// </summary>
public record ExchangeRate
{
    /// <summary>
    /// Composite key: {FromCurrency}-{ToCurrency} (e.g. EUR-USD).
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Source currency code (ISO 4217).
    /// </summary>
    [Dimension(typeof(string), nameof(FromCurrency))]
    [Display(Name = "From Currency")]
    public string FromCurrency { get; init; } = string.Empty;

    /// <summary>
    /// Target currency code (ISO 4217).
    /// </summary>
    [Dimension(typeof(string), nameof(ToCurrency))]
    [Display(Name = "To Currency")]
    public string ToCurrency { get; init; } = string.Empty;

    /// <summary>
    /// Plan (budget) conversion rate: 1 unit of FromCurrency = PlanRate units of ToCurrency.
    /// Used for converting estimated/budgeted amounts.
    /// </summary>
    [Display(Name = "Plan Rate")]
    public double PlanRate { get; init; }

    /// <summary>
    /// Actual (market) conversion rate: 1 unit of FromCurrency = ActualRate units of ToCurrency.
    /// Used for converting actual amounts at market rates.
    /// </summary>
    [Display(Name = "Actual Rate")]
    public double ActualRate { get; init; }

    /// <summary>
    /// Display order.
    /// </summary>
    public int Order { get; init; }
}
