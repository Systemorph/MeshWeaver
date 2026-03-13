// <meshweaver>
// Id: ExchangeRate
// DisplayName: Exchange Rate
// </meshweaver>

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

/// <summary>
/// Exchange rate from one currency to another (e.g. EUR → USD).
/// Used at group aggregation time to convert local-currency amounts
/// into the group reporting currency (USD).
/// </summary>
public record ExchangeRate
{
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Rich markdown description with SLO and governance details.
    /// </summary>
    [Markdown]
    [Browsable(false)]
    public string? Description { get; init; }

    /// <summary>
    /// Source currency code (ISO 4217).
    /// </summary>
    [Display(Name = "From Currency")]
    public string FromCurrency { get; init; } = string.Empty;

    /// <summary>
    /// Target currency code (ISO 4217).
    /// </summary>
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

    public int Order { get; init; }
}
