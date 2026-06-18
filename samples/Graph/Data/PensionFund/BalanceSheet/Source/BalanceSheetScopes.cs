// <meshweaver>
// Id: BalanceSheetScopes
// DisplayName: Balance Sheet Business Rules
// </meshweaver>

// The IScope<,> business-rules framework and its source generator are pulled in on
// demand — they are NOT baked into the platform. The generator runs in-process during
// this node's compilation (SourceGeneratorLoader discovers it from these references) and
// emits the IScope proxy implementations that AddBusinessRules(...) then registers.
#r "nuget:MeshWeaver.BusinessRules, 3.0.0-preview1"
#r "nuget:MeshWeaver.BusinessRules.Generator, 3.0.0-preview1"

using System.Collections.Immutable;
using MeshWeaver.BusinessRules;

/// <summary>
/// The storage every balance-sheet scope computes against: the position
/// dimension nodes (by path) and the atomic fact values (by position × year
/// path pair). Built once per evaluation from the mesh nodes.
/// </summary>
public record BalanceSheetStorage
{
    /// <summary>Position content by position node path.</summary>
    public required ImmutableDictionary<string, Position> Positions { get; init; }

    /// <summary>
    /// Position paths in display order — taken from <c>MeshNode.Order</c> on
    /// the dimension nodes, since ordering lives on the node, not the content.
    /// </summary>
    public required ImmutableList<string> OrderedPositions { get; init; }

    /// <summary>Atomic amounts (CHF m) by (position path, year path).</summary>
    public required ImmutableDictionary<(string Position, string Year), double> Amounts { get; init; }

    /// <summary>All year node paths, ascending.</summary>
    public required ImmutableArray<string> Years { get; init; }
}

/// <summary>
/// Identity of a position value: one Position node in one Year node —
/// both referenced by mesh path.
/// </summary>
public record PositionYear(string Position, string Year);

/// <summary>
/// THE business rule of the balance sheet: what a position is worth.
/// Atomic positions read the fact value; Sum positions fold their weighted
/// components (each component being another PositionValue scope — composition
/// all the way down to the atoms); Ratio positions divide two other positions.
/// Scope instances are cached per (identity) by the ScopeRegistry, so shared
/// sub-positions (e.g. Total Assets inside both Balance Sheet Sum and the
/// Funding Ratio) are computed exactly once.
/// </summary>
public interface PositionValue : IScope<PositionYear, BalanceSheetStorage>
{
    /// <summary>The position's dimension node content.</summary>
    Position Position => GetStorage().Positions[Identity.Position];

    /// <summary>The computed value (CHF m) of this position in this year.</summary>
    double Value => Position.Aggregation switch
    {
        PositionAggregation.Atomic =>
            GetStorage().Amounts.TryGetValue((Identity.Position, Identity.Year), out var v) ? v : 0,

        PositionAggregation.Sum =>
            (Position.Components ?? [])
                .Sum(c => c.Weight * GetScope<PositionValue>(new PositionYear(c.Position, Identity.Year)).Value),

        PositionAggregation.Ratio =>
            GetScope<PositionValue>(new PositionYear(Position.Denominator!, Identity.Year)).Value is var denominator
            && denominator != 0
                ? GetScope<PositionValue>(new PositionYear(Position.Numerator!, Identity.Year)).Value / denominator
                : 0,

        _ => 0,
    };
}

/// <summary>
/// Year-level summary scope: composes the headline figures of one reporting
/// year out of the position scopes. The position paths are resolved from the
/// storage by aggregation/side, so the scope works for any position set.
/// </summary>
public interface BalanceSheetSummary : IScope<string, BalanceSheetStorage>
{
    private static string PathOf(BalanceSheetStorage storage, string suffix)
        => storage.Positions.Keys.First(p => p.EndsWith("/" + suffix));

    double BalanceSheetSum => GetScope<PositionValue>(
        new PositionYear(PathOf(GetStorage(), "BalanceSheetSum"), Identity)).Value;

    double TotalAssets => GetScope<PositionValue>(
        new PositionYear(PathOf(GetStorage(), "TotalAssets"), Identity)).Value;

    double TotalLiabilities => GetScope<PositionValue>(
        new PositionYear(PathOf(GetStorage(), "TotalLiabilities"), Identity)).Value;

    double PensionCapital => GetScope<PositionValue>(
        new PositionYear(PathOf(GetStorage(), "PensionCapital"), Identity)).Value;

    double FundingRatio => GetScope<PositionValue>(
        new PositionYear(PathOf(GetStorage(), "FundingRatio"), Identity)).Value;

    /// <summary>The balance check: assets and liabilities must match.</summary>
    bool Balances => Math.Abs(TotalAssets - TotalLiabilities) < 1e-9;
}
