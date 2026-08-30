using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the grant-matching rules that decide what a registered instance may pull. This is the
/// authorization decision of the plugin-registry surface, so every branch is spelled out: a
/// whole-source grant, a single-plugin grant, the source boundary, and the empty grant a freshly
/// registered instance carries.
/// </summary>
public class PluginGrantTest
{
    // The real shape as of 2026-08-06: our company instance holds the whole platform repo plus
    // exactly ONE plugin out of the reinsurance repo — the case that forces two grant levels.
    private static readonly PluginGrant MemexGrant = new()
    {
        InstanceId = "memex",
        Entries =
        [
            new PluginGrantEntry { Source = "Plugins", PackageId = PluginGrantEntry.AllPackages },
            new PluginGrantEntry { Source = "Reinsurance", PackageId = "UWDeepfield" },
        ],
    };

    [Theory]
    [InlineData("Plugins", "Store")]
    [InlineData("Plugins", "Agent")]
    [InlineData("Plugins", "AnythingElseTheRepoGains")]
    public void WholeSourceGrant_AllowsEveryPackageInThatSource(string source, string package)
        => Assert.True(MemexAllows(source, package));

    [Fact]
    public void SinglePluginGrant_AllowsExactlyThatPlugin()
        => Assert.True(MemexAllows("Reinsurance", "UWDeepfield"));

    [Theory]
    [InlineData("ClaimsDeepfield")]
    [InlineData("Ifrs17")]
    [InlineData("Pricing")]
    [InlineData("SST")]
    public void SinglePluginGrant_DeniesTheRestOfTheSameRepo(string package)
        => Assert.False(MemexAllows("Reinsurance", package));

    [Fact]
    public void GrantDoesNotLeakAcrossSources()
    {
        // "UWDeepfield" is granted in Reinsurance — that must not make a same-named package in
        // another source readable. Grants are (source, package) pairs, not bare package names.
        Assert.False(MemexAllows("Education", "UWDeepfield"));
        // And a whole-source grant on Plugins says nothing about Education.
        Assert.False(MemexAllows("Education", "AgenticEngineering"));
    }

    [Fact]
    public void SourceNameMatchIsCaseInsensitive()
    {
        // Source names are operator-typed config values, not wire identifiers.
        Assert.True(MemexAllows("plugins", "Store"));
        Assert.True(MemexAllows("REINSURANCE", "UWDeepfield"));
    }

    [Fact]
    public void PackageIdMatchIsCaseSensitive()
        // The catalog compares ids with StringComparer.Ordinal; the grant must not be laxer, or a
        // near-miss id would authorize a package the catalog considers different.
        => Assert.False(MemexAllows("Reinsurance", "uwdeepfield"));

    [Fact]
    public void FreshlyRegisteredInstance_IsEntitledToNothing()
    {
        // The normal post-registration state: identity without entitlement. This is what makes
        // self-service registration safe — anyone may register, nobody self-grants.
        var fresh = new PluginGrant { InstanceId = "somebody-elses-portal" };
        Assert.False(fresh.Allows("Plugins", "Store"));
        Assert.False(fresh.Allows("Reinsurance", "UWDeepfield"));
        Assert.False(fresh.Allows("Education", "AgenticEngineering"));
    }

    [Fact]
    public void CustomerPortalGrant_CarriesOnlyThePlatformRepo()
    {
        // The customer portal gets MeshWeaver.Plugins and nothing else — in particular none of the
        // reinsurance plugins and none of the paid course content.
        var prod = new PluginGrant
        {
            InstanceId = "prod",
            Entries = [new PluginGrantEntry { Source = "Plugins", PackageId = PluginGrantEntry.AllPackages }],
        };
        Assert.True(prod.Allows("Plugins", "Store"));
        Assert.False(prod.Allows("Reinsurance", "UWDeepfield"));
        Assert.False(prod.Allows("Education", "AgenticEngineering"));
    }

    [Fact]
    public void EntryRendersAsSourceSlashPackage()
    {
        Assert.Equal("Plugins/*", new PluginGrantEntry { Source = "Plugins" }.ToString());
        Assert.Equal("Reinsurance/UWDeepfield",
            new PluginGrantEntry { Source = "Reinsurance", PackageId = "UWDeepfield" }.ToString());
    }

    // TryParse reads the Source/Package notation off PluginCatalog:DefaultGrants config — the list
    // a registry operator uses to opt sources into every new registration. Operator-typed, so a
    // malformed entry must parse to null (skipped), never throw the registration surface down.
    [Theory]
    [InlineData("Plugins/*", "Plugins", "*")]
    [InlineData("Plugins", "Plugins", "*")] // bare source = whole source
    [InlineData("Reinsurance/UWDeepfield", "Reinsurance", "UWDeepfield")]
    [InlineData("  Plugins / * ", "Plugins", "*")] // operator-typed: whitespace tolerated
    public void TryParse_ReadsTheSourceSlashPackageNotation(string text, string source, string package)
    {
        var entry = PluginGrantEntry.TryParse(text);
        Assert.NotNull(entry);
        Assert.Equal(source, entry!.Source);
        Assert.Equal(package, entry.PackageId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")] // separator only — no source
    [InlineData("/Store")] // no source
    [InlineData("Plugins/")] // separator but nothing after it
    public void TryParse_MalformedEntry_IsNullNotAThrow(string? text)
        => Assert.Null(PluginGrantEntry.TryParse(text));

    [Fact]
    public void TryParse_RoundTripsToString()
    {
        // The notation is symmetric: what an entry renders as, TryParse reads back identically.
        var entry = new PluginGrantEntry { Source = "Reinsurance", PackageId = "UWDeepfield" };
        Assert.Equal(entry, PluginGrantEntry.TryParse(entry.ToString()));
    }

    // ── Plans: an entry scoped to a subscription tier ────────────────────────────────────────
    //
    // The ladder the Store seeds under Admin/Tiers/* as of 2026-08-30 — rank on the node, dedicated
    // flagged all-access ("no limit on packages", which its rank alone would not give it).
    private static readonly PlanTierRanks Ladder = PlanTierRanks.From(
    [
        ("free", 0, false), ("personal", 10, false), ("pro", 20, false),
        ("dedicated", 25, true), ("enterprise", 30, false),
    ]);

    private static PluginGrant PlanGrant(string tier) => new()
    {
        InstanceId = "customer",
        Entries = [new PluginGrantEntry { Source = "Plugins", PackageId = PluginGrantEntry.AllPackages, Tier = tier }],
    };

    // The instance is ON the plan its entry names — the entry caps at its own level, which is no
    // cap, so the ladder rule alone decides. The instance plan is what licenses (#2804).
    private static bool Covers(string plan, string? packageTier) =>
        PlanGrant(plan).Allows("Plugins", "SomePackage", packageTier, Ladder, plan, DateTimeOffset.UtcNow);

    private static PluginGrant PlanLessGrant() => new()
    {
        InstanceId = "customer",
        Entries = [new PluginGrantEntry { Source = "Plugins", PackageId = PluginGrantEntry.AllPackages }],
    };

    [Fact]
    public void TheInstancePlanIsTheLicence_AnEntryCanOnlyCapIt()
    {
        var now = DateTimeOffset.UtcNow;
        // A plan-less entry licenses at the INSTANCE's plan — not every tier.
        Assert.True(PlanLessGrant().Allows("Plugins", "P", "pro", Ladder, "pro", now));
        Assert.False(PlanLessGrant().Allows("Plugins", "P", "enterprise", Ladder, "pro", now));
        // An instance that names no plan is on the baseline: free and untiered, nothing above.
        Assert.True(PlanLessGrant().Allows("Plugins", "P", "free", Ladder, null, now));
        Assert.True(PlanLessGrant().Allows("Plugins", "P", null, Ladder, null, now));
        Assert.False(PlanLessGrant().Allows("Plugins", "P", "personal", Ladder, null, now));
        // An entry's plan CAPS: a free-capped entry on a pro instance licenses free only …
        Assert.False(PlanGrant("free").Allows("Plugins", "P", "pro", Ladder, "pro", now));
        Assert.True(PlanGrant("free").Allows("Plugins", "P", "free", Ladder, "pro", now));
        // … and never RAISES: an enterprise-suffixed entry on a free instance is still free.
        Assert.False(PlanGrant("enterprise").Allows("Plugins", "P", "pro", Ladder, "free", now));
        Assert.False(PlanGrant("enterprise").Allows("Plugins", "P", "pro", Ladder, null, now));
    }

    [Theory]
    [InlineData("personal", "free", true)]
    [InlineData("personal", "personal", true)]
    [InlineData("personal", "pro", false)]
    [InlineData("personal", "enterprise", false)]
    [InlineData("pro", "personal", true)]
    [InlineData("pro", "pro", true)]
    [InlineData("pro", "dedicated", false)]
    [InlineData("enterprise", "pro", true)]
    [InlineData("enterprise", "enterprise", true)]
    public void PlanScopedEntry_CoversPackagesRankedAtOrBelowItsPlan(string plan, string packageTier, bool covered)
        => Assert.Equal(covered, Covers(plan, packageTier));

    [Fact]
    public void AllAccessPlan_CoversEvenPackagesRankedAboveIt()
        // Dedicated ranks 25 and enterprise 30, so by rank alone the dedicated instance would lose
        // the enterprise-marked packages — "no limit on packages" is the all-access flag on its
        // tier node, not a bigger number.
        => Assert.True(Covers("dedicated", "enterprise"));

    [Theory]
    [InlineData("personal")]
    [InlineData("pro")]
    [InlineData("enterprise")]
    public void PackageDeclaringNoPlan_IsBaseline_CoveredByEveryPlan(string plan)
        // Store, Agents, Skills and Essentials declare no tier. A plan-scoped instance without them
        // is not a smaller portal, it is a broken one — the one deliberate asymmetry with the
        // Store's purchase rule, where "no tier" means "not sold under a plan".
        => Assert.True(Covers(plan, null));

    [Fact]
    public void UnknownPlan_ReadsAsTheBaseline_NeverWider()
    {
        // A plan the ladder does not know — a typo, or one this registry never seeded — can never
        // WIDEN a licence; it narrows to the baseline. "Nothing at all" would not be safer, it
        // would be a portal without its Store (#2804).
        Assert.True(Covers("platinum", "free"));
        Assert.True(Covers("platinum", null));
        Assert.False(Covers("platinum", "personal"));
        Assert.False(Covers("platinum", "enterprise"));
    }

    [Fact]
    public void UnknownPackageTier_IsCoveredByNothing()
        // A typo on the package side must never widen a licence either.
        => Assert.False(Covers("enterprise", "gold"));

    [Fact]
    public void WithoutALadder_NothingWidensBeyondTheBaseline()
    {
        // No ladder: "pro" cannot be ranked, so a pro-capped entry — and a pro instance — read as
        // the baseline. Free and untiered packages flow; the paid tier does not.
        var scoped = PlanGrant("pro");
        Assert.True(scoped.Allows("Plugins", "Store", "free", PlanTierRanks.Empty, "pro", DateTimeOffset.UtcNow));
        Assert.True(scoped.Allows("Plugins", "Store", null, PlanTierRanks.Empty, "pro", DateTimeOffset.UtcNow));
        Assert.False(scoped.Allows("Plugins", "Store", "pro", PlanTierRanks.Empty, "pro", DateTimeOffset.UtcNow));
        // A plan-less entry on a ladder-less registry licenses the BASELINE — free and untiered
        // packages, which is what a local self-registry or the e2e stub serves — and nothing
        // above it: without a ladder a paid tier cannot be ranked, so it is not covered (#2804).
        var open = new PluginGrant
        {
            InstanceId = "prod",
            Entries = [new PluginGrantEntry { Source = "Plugins" }],
        };
        Assert.True(open.Allows("Plugins", "Store", null, PlanTierRanks.Empty));
        Assert.True(open.Allows("Plugins", "Store", "free", PlanTierRanks.Empty));
        Assert.False(open.Allows("Plugins", "Store", "enterprise", PlanTierRanks.Empty));
    }

    [Fact]
    public void TierBlindOverload_NeverSaysYesOnAPlanScopedEntry()
    {
        // A caller that does not know the package's tier cannot be answered by a plan-scoped
        // entry — otherwise every plan is all-access at exactly the call sites that forgot to ask.
        Assert.False(PlanGrant("enterprise").Allows("Plugins", "Store"));
        Assert.True(MemexGrant.Allows("Plugins", "Store"));
    }

    [Fact]
    public void WholeSourceFetch_NeedsAPlanLessWholeSourceEntry()
    {
        // A sealed publication carries every plan's bundles; a plan-scoped `Plugins/*` licenses
        // that source's packages one by one and never the publication whole.
        Assert.True(MemexGrant.AllowsWholeSource("Plugins"));
        Assert.False(MemexGrant.AllowsWholeSource("Reinsurance"));   // a single-package entry
        Assert.False(PlanGrant("dedicated").AllowsWholeSource("Plugins"));
    }

    [Fact]
    public void PlanMatchIsCaseInsensitive_OnBothSides()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(PlanGrant("PRO").Allows("Plugins", "X", "Personal", Ladder, "Pro", now));
        Assert.True(PlanGrant("pro").Allows("Plugins", "X", " PRO ", Ladder, " PRO ", now));
        Assert.True(PlanLessGrant().Allows("Plugins", "X", "pro", Ladder, "PRO", now));
    }

    [Theory]
    [InlineData("Plugins/*@pro", "Plugins", "*", "pro")]
    [InlineData("Plugins@personal", "Plugins", "*", "personal")]
    [InlineData("Education/AgenticEngineering@Pro", "Education", "AgenticEngineering", "pro")]
    public void TryParse_ReadsThePlanSuffix(string text, string source, string package, string tier)
    {
        var entry = PluginGrantEntry.TryParse(text);
        Assert.NotNull(entry);
        Assert.Equal(source, entry!.Source);
        Assert.Equal(package, entry.PackageId);
        Assert.Equal(tier, entry.Tier);
    }

    [Theory]
    [InlineData("Plugins/*@")]   // a plan separator with no plan
    [InlineData("@pro")]         // a plan with no source
    public void TryParse_MalformedPlanSuffix_IsNull(string text)
        => Assert.Null(PluginGrantEntry.TryParse(text));

    [Fact]
    public void PlanScopedEntry_RendersAndRoundTrips()
    {
        var entry = new PluginGrantEntry { Source = "Plugins", Tier = "pro" };
        Assert.Equal("Plugins/*@pro", entry.ToString());
        Assert.Equal(entry, PluginGrantEntry.TryParse(entry.ToString()));
        // A plan-less entry renders exactly as before — the notation is additive.
        Assert.Equal("Plugins/*", new PluginGrantEntry { Source = "Plugins" }.ToString());
    }

    [Fact]
    public void Ladder_OrdersItsIdsCheapestFirst()
        => Assert.Equal(["free", "personal", "pro", "dedicated", "enterprise"], Ladder.Ids);

    private static bool MemexAllows(string source, string package) => MemexGrant.Allows(source, package);
}
