using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The subscription-plan ladder as the REGISTRY reads it — which plan ids exist, how they rank
/// (cheap → capable), and which of them are all-access — and the ONE coverage rule a plan-scoped
/// <see cref="PluginGrantEntry"/> is decided by.
///
/// <para>The ladder is DATA, not code: the Store seeds one <c>Store/Tier</c> node per plan under
/// <c>Admin/Tiers/{id}</c> (rank on <c>content.rank</c>, all-access on <c>content.allAccess</c>),
/// so an operator's re-pricing or a new plan survives every redeploy and this record is only a
/// snapshot of those nodes (<c>PlanTierLadder</c> in <c>MeshWeaver.PluginCatalog</c> reads them).
/// The platform therefore carries no copy of the Store's <c>PlanTiers</c> table to drift from.</para>
///
/// <para><b>The rule.</b> A grant entry that names a plan licenses exactly the packages that plan
/// covers: the package's declared tier must rank at or below the plan's, unless the plan is
/// all-access (the dedicated instance's "no limit on packages"). An entry naming NO plan licenses
/// every tier — today's semantics, unchanged. Two deliberate asymmetries with the Store's
/// user-facing rule (<c>SubscriptionFact.Covers</c>):</para>
/// <list type="bullet">
///   <item><description>A package that declares NO tier is the platform BASELINE (Store, Agents,
///     Skills, Essentials) and is covered by every plan — rank 0. For a person buying a package
///     "no tier" means "not sold under a plan"; for an instance replicating the registry it means
///     "ships with the platform", and a plan-scoped instance without the Store is not a smaller
///     portal, it is a broken one.</description></item>
///   <item><description>An UNKNOWN id fails closed on BOTH sides: a plan the ladder does not know
///     licenses nothing, and a package tier the ladder does not know is covered by nothing. A typo
///     must never widen a licence.</description></item>
/// </list>
/// </summary>
/// <param name="Ranks">Plan id → rank, case-insensitive.</param>
/// <param name="AllAccess">The plan ids that cover every package regardless of rank.</param>
public sealed record PlanTierRanks(
    ImmutableDictionary<string, int> Ranks,
    ImmutableHashSet<string> AllAccess)
{
    /// <summary>No ladder at all: every plan-scoped entry licenses nothing (fail closed) while
    /// plan-less entries are unaffected — what a registry without Store tier nodes, or one whose
    /// tier read failed, decides with.</summary>
    public static readonly PlanTierRanks Empty = new(
        ImmutableDictionary.Create<string, int>(StringComparer.OrdinalIgnoreCase),
        ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The rank a package with NO declared tier is read at — the platform baseline.</summary>
    public const int BaselineRank = 0;

    /// <summary>Builds a ladder from <c>(id, rank, allAccess)</c> rows — the tier nodes, or a test's
    /// literal. Ids are trimmed and matched case-insensitively; a blank id is skipped.</summary>
    public static PlanTierRanks From(IEnumerable<(string Id, int Rank, bool AllAccess)> plans)
    {
        var ranks = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);
        var allAccess = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, rank, isAllAccess) in plans)
        {
            var key = Canonical(id);
            if (key.Length == 0)
                continue;
            ranks[key] = rank;
            if (isAllAccess)
                allAccess.Add(key);
        }
        return new PlanTierRanks(ranks.ToImmutable(), allAccess.ToImmutable());
    }

    /// <summary>The plan ids this ladder knows, cheapest first — what a form offers.</summary>
    public IReadOnlyList<string> Ids =>
        Ranks.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key).ToList();

    /// <summary>The rank of <paramref name="tier"/>, or null when the id is blank or unknown.</summary>
    public int? RankOf(string? tier) =>
        Canonical(tier) is { Length: > 0 } key && Ranks.TryGetValue(key, out var rank) ? rank : null;

    /// <summary>Whether <paramref name="tier"/> is an all-access plan.</summary>
    public bool IsAllAccess(string? tier) =>
        Canonical(tier) is { Length: > 0 } key && AllAccess.Contains(key);

    /// <summary>
    /// Whether a grant entry scoped to <paramref name="planTier"/> covers a package declaring
    /// <paramref name="packageTier"/> — the rule in the type remarks, pure.
    /// </summary>
    public bool Covers(string? planTier, string? packageTier)
    {
        if (string.IsNullOrWhiteSpace(planTier))
            return true;                                   // no plan on the entry: every tier
        if (RankOf(planTier) is not { } plan)
            return false;                                  // unknown plan licenses nothing
        if (IsAllAccess(planTier))
            return true;
        if (string.IsNullOrWhiteSpace(packageTier))
            return BaselineRank <= plan;                   // baseline package: rank 0
        return RankOf(packageTier) is { } package && package <= plan;
    }

    /// <summary>
    /// The plan an instance stands on when its record names none — the free tier. The one id the
    /// rule below understands WITHOUT a ladder: a registry that seeds no tier nodes (a local
    /// self-registry, the e2e stub) still serves its free and untiered packages, and refuses every
    /// paid tier, because "free" ranks at the baseline by definition.
    /// </summary>
    public const string BaselinePlan = "free";

    /// <summary>
    /// Whether an INSTANCE on <paramref name="instancePlan"/> may pull a package declaring
    /// <paramref name="packageTier"/> — the licence rule every registry surface decides with
    /// (#2804). This is <see cref="Covers"/> with the one difference that closes the hole: a
    /// blank plan is never "every tier", it is <see cref="BaselinePlan"/>. Everything else is the
    /// ladder rule — an all-access plan covers everything, an unknown plan or an unknown package
    /// tier licenses nothing, a package with no tier is the platform baseline.
    /// </summary>
    /// <param name="instancePlan">The plan on the instance record; blank = the baseline.</param>
    /// <param name="packageTier">The package's declared tier; blank = baseline.</param>
    /// <remarks>
    /// An instance plan the ladder does not know reads as the BASELINE — free and untiered
    /// packages, nothing above. That is the fail-closed direction for a plan (a typo, or a plan
    /// this registry has not seeded, can never widen a licence) without turning it into an outage:
    /// "nothing at all, not even the Store" is not a safer answer than "free", it is a broken
    /// portal. A package tier the ladder does not know is still covered by nothing.
    /// </remarks>
    public bool CoversInstance(string? instancePlan, string? packageTier)
    {
        var plan = Canonical(instancePlan) is { Length: > 0 } named ? named : BaselinePlan;
        if (IsAllAccess(plan))
            return true;
        var planRank = RankOf(plan) ?? BaselineRank;       // unknown or unladdered plan: the baseline
        var package = Canonical(packageTier);
        if (package.Length == 0 || package == BaselinePlan)
            return BaselineRank <= planRank;               // a baseline package, ladder or not
        return RankOf(package) is { } packageRank && packageRank <= planRank;
    }

    /// <summary>
    /// The plan a grant entry actually decides with: the instance's plan, NARROWED by the entry's
    /// own cap when it names one. A cap can only lower the plan — it is how an admin licenses a
    /// pro instance's access to one source at the free level — and it can never raise it, which
    /// is what made <c>Plugins/*@pro</c> on a free instance a licence the instance never bought.
    /// A blank cap is no cap; an unknown cap, like an unknown plan, reads as the baseline — the
    /// narrowest level there is (<see cref="CoversInstance"/>).
    /// </summary>
    public string? Narrower(string? instancePlan, string? cap)
    {
        var capId = Canonical(cap);
        if (capId.Length == 0)
            return instancePlan;
        var plan = Canonical(instancePlan) is { Length: > 0 } named ? named : BaselinePlan;
        if (IsAllAccess(capId))
            return plan;                                   // an all-access cap caps nothing
        if (IsAllAccess(plan))
            return capId;
        var planRank = RankOf(plan) ?? BaselineRank;
        var capRank = RankOf(capId) ?? BaselineRank;
        return capRank < planRank ? capId : plan;
    }

    /// <summary>The comparison form of a plan id — trimmed, lower-case; empty for blank.</summary>
    public static string Canonical(string? tier) => (tier ?? "").Trim().ToLowerInvariant();
}
