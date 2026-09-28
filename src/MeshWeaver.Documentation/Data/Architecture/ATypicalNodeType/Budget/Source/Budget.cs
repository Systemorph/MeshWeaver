// <meshweaver>
// Id: Budget
// DisplayName: Budget Data Model
// </meshweaver>
#nullable enable
using System;

/// <summary>
/// A budget: what was planned, and what has been spent against it. The CONTENT of an
/// <c>Doc/Architecture/ATypicalNodeType/Budget</c> node — data only. Everything derived from it (remaining, utilisation,
/// health) is computed by <see cref="BudgetRules"/> and never stored, so the node cannot disagree
/// with itself.
/// </summary>
public record Budget
{
    /// <summary>What the budget pays for.</summary>
    [Description("What the budget pays for")]
    [Translation("de", "Wofür das Budget bestimmt ist")]
    public string Purpose { get; init; } = string.Empty;

    /// <summary>The ISO 4217 currency every amount is stated in.</summary>
    [Description("Currency (ISO 4217)")]
    [Translation("de", "Währung (ISO 4217)")]
    public string Currency { get; init; } = "CHF";

    /// <summary>The planned amount.</summary>
    [Description("Planned amount")]
    [Translation("de", "Geplanter Betrag")]
    public decimal Planned { get; init; }

    /// <summary>The amount spent so far.</summary>
    [Description("Amount spent so far")]
    [Translation("de", "Bisher ausgegeben")]
    public decimal Spent { get; init; }
}

/// <summary>
/// The health of a budget — an OPEN vocabulary of string constants, never an enum
/// (policy <c>open-vocabulary-string-constants</c>): a value this class does not name is carried
/// and rendered as itself, never folded into a meaningful default.
/// </summary>
public static class BudgetHealth
{
    /// <summary>Spent is below the at-risk threshold of the plan.</summary>
    public const string OnTrack = "OnTrack";

    /// <summary>Spent has reached the at-risk threshold but not the plan.</summary>
    public const string AtRisk = "AtRisk";

    /// <summary>Spent exceeds the plan.</summary>
    public const string Overspent = "Overspent";

    /// <summary>Nothing (or a non-positive amount) was planned, so no ratio exists.</summary>
    public const string Unplanned = "Unplanned";
}

/// <summary>
/// The behaviour of a <see cref="Budget"/> — pure functions of its content, which is what makes
/// them testable without a mesh (see <c>Test/BudgetTests.cs</c>).
/// </summary>
public static class BudgetRules
{
    /// <summary>The share of the plan at which a budget turns <see cref="BudgetHealth.AtRisk"/>.</summary>
    public const decimal AtRiskThreshold = 0.9m;

    /// <summary>Plan minus spend; negative when the budget is overspent.</summary>
    /// <param name="budget">The budget.</param>
    /// <returns>The remaining amount.</returns>
    public static decimal Remaining(Budget budget) => budget.Planned - budget.Spent;

    /// <summary>Spend as a share of the plan, or null when nothing was planned.</summary>
    /// <param name="budget">The budget.</param>
    /// <returns>The utilisation (1 = fully spent), or null.</returns>
    public static decimal? Utilisation(Budget budget) =>
        budget.Planned <= 0m ? null : budget.Spent / budget.Planned;

    /// <summary>The budget's health, one of the <see cref="BudgetHealth"/> constants.</summary>
    /// <param name="budget">The budget.</param>
    /// <returns>The health.</returns>
    public static string Health(Budget budget) =>
        Utilisation(budget) switch
        {
            null => BudgetHealth.Unplanned,
            > 1m => BudgetHealth.Overspent,
            >= AtRiskThreshold => BudgetHealth.AtRisk,
            _ => BudgetHealth.OnTrack,
        };

    /// <summary>
    /// Records a spend. Returns the NEW content — the caller writes it through
    /// <c>GetMeshNodeStream(path).Update(node =&gt; …)</c>, the one mutation API.
    /// </summary>
    /// <param name="budget">The budget before the spend.</param>
    /// <param name="amount">The amount spent; must not be negative.</param>
    /// <returns>The budget after the spend.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative.</exception>
    public static Budget RecordSpend(Budget budget, decimal amount) =>
        amount < 0m
            ? throw new ArgumentOutOfRangeException(nameof(amount), amount, "A spend cannot be negative.")
            : budget with { Spent = budget.Spent + amount };
}
