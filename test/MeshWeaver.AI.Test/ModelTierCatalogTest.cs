#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Immutable;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The USAGE-tier selection rules. Pure functions over the catalog — no hub, no snapshot, no
/// credentials — which is the point: "which model does an agent asking for <c>coding</c> actually
/// get" is decidable from data alone, and stays decidable when an environment labels only some of
/// its models, renames a tier, or labels none at all.
///
/// <para>Half these tests exist to pin ONE property: <b>resolution never fails, it degrades.</b>
/// Every stripped-catalog shape below (no labels, router only, unknown label, empty tier registry,
/// nothing usable) must still terminate in a real model — or, in the single genuinely-empty case,
/// report <see cref="ModelTierSource.None"/> so the caller fails audibly instead of running on
/// nothing.</para>
/// </summary>
public class ModelTierCatalogTest
{
    // A catalog shaped like a real deployment: GLM pinned default at -1, a labelled utility and
    // chat rung, kimi-k3 on coding, and the Auto router. Ordered deliberately out of Order so
    // nothing passes by luck.
    private static ImmutableList<ModelTierCandidate> Catalog =>
    [
        new("moonshotai/kimi-k3", 7, ModelTierDefaults.Coding, false),
        new("openrouter/auto", -10, null, true),
        new("z-ai/glm-5.2", -1, ModelTierDefaults.Reasoning, false),
        new("amazon/nova-micro-v1", 3, ModelTierDefaults.Utility, false),
        new("qwen/qwen3-next-80b-a3b-instruct", 2, ModelTierDefaults.Chat, false),
    ];

    private static string? Resolve(string? label, IEnumerable<ModelTierCandidate> candidates,
        IEnumerable<ModelTierDefinition>? tiers = null, Func<string, bool>? usable = null,
        Func<ModelTierDefinition, string?>? legacy = null)
        => ModelTierCatalog.Resolve(label, candidates, tiers, usable, legacy).ModelId;

    [Fact]
    public void ResolvesEachPopulatedTier()
    {
        Assert.Equal("amazon/nova-micro-v1", Resolve(ModelTierDefaults.Utility, Catalog));
        Assert.Equal("qwen/qwen3-next-80b-a3b-instruct", Resolve(ModelTierDefaults.Chat, Catalog));
        Assert.Equal("z-ai/glm-5.2", Resolve(ModelTierDefaults.Reasoning, Catalog));
        Assert.Equal("moonshotai/kimi-k3", Resolve(ModelTierDefaults.Coding, Catalog));
    }

    [Fact]
    public void CodingIsTheTopTier_AndIsWhereTheStrongestModelSits()
    {
        // The requirement in one assertion: the highest rung is named for the JOB (coding), and it
        // is what an agent that writes code lands on — not the pinned deployment default.
        var top = ModelTierDefaults.All.OrderBy(t => t.Rank).Last();
        Assert.Equal(ModelTierDefaults.Coding, top.Id);

        var resolution = ModelTierCatalog.Resolve(top.Id, Catalog);
        Assert.Equal("moonshotai/kimi-k3", resolution.ModelId);
        Assert.Equal(ModelTierSource.Label, resolution.Source);
    }

    [Fact]
    public void UtilityIsTheBottomTier_TheOneBackgroundMicroJobsRunOn()
    {
        var bottom = ModelTierDefaults.All.OrderBy(t => t.Rank).First();
        Assert.Equal(ModelTierDefaults.Utility, bottom.Id);
        Assert.Equal("amazon/nova-micro-v1", Resolve(bottom.Id, Catalog));
    }

    [Fact]
    public void LowestOrderWinsWithinATier()
    {
        var twoCoders = Catalog.Add(new("second-coder", 9, ModelTierDefaults.Coding, false));

        Assert.Equal("moonshotai/kimi-k3", Resolve(ModelTierDefaults.Coding, twoCoders));
    }

    [Fact]
    public void UnpopulatedTierIsAMiss_NotAnError()
    {
        var noCoder = Catalog.RemoveAll(c => c.Tier == ModelTierDefaults.Coding);

        Assert.Null(ModelTierCatalog.ResolveLabel(
            ModelTierDefaults.All.Single(t => t.Id == ModelTierDefaults.Coding), noCoder));
    }

    [Fact]
    public void UnpopulatedTierFallsThroughToTheDefault_AndSaysSo()
    {
        // The whole point of not having to populate every tier: asking for one nobody carries still
        // runs a round, on the default — and the resolution REPORTS that it degraded, so the
        // operator can be told (a silent substitution is the #476 defect).
        var onlyReasoning = Catalog.RemoveAll(
            c => c.Tier is ModelTierDefaults.Utility or ModelTierDefaults.Chat or ModelTierDefaults.Coding);

        var resolution = ModelTierCatalog.Resolve(ModelTierDefaults.Coding, onlyReasoning);

        Assert.Equal("z-ai/glm-5.2", resolution.ModelId);
        Assert.Equal(ModelTierSource.DeploymentDefault, resolution.Source);
        Assert.True(resolution.IsUnpopulatedTierFallback);
        Assert.Equal(ModelTierDefaults.Coding, resolution.RequestedTier);
    }

    [Fact]
    public void NoTierRequestedIsNotAFallback()
    {
        // Declaring no tier and landing on the default is the NORMAL path, not a substitution —
        // reporting it as one would train operators to ignore the warning that matters.
        var resolution = ModelTierCatalog.Resolve(null, Catalog);

        Assert.Equal("z-ai/glm-5.2", resolution.ModelId);
        Assert.Equal(ModelTierSource.DeploymentDefault, resolution.Source);
        Assert.False(resolution.IsUnpopulatedTierFallback);
        Assert.Null(resolution.RequestedTier);
    }

    [Fact]
    public void DefaultIsTheLowestOrder_RouterExcluded()
    {
        // openrouter/auto sorts first at -10 and must STILL not be the default MODEL: it dispatches
        // rather than answers, so defaulting to it would resolve Auto to Auto.
        Assert.Equal("z-ai/glm-5.2", ModelTierCatalog.Default(Catalog));
    }

    [Fact]
    public void RouterIsNeverSelectedByTierEither()
    {
        var labelledRouter = Catalog
            .RemoveAll(c => c.IsRouter)
            .Add(new("openrouter/auto", -10, ModelTierDefaults.Reasoning, true));

        Assert.Equal("z-ai/glm-5.2", Resolve(ModelTierDefaults.Reasoning, labelledRouter));
    }

    [Fact]
    public void UnusableModelsAreSkipped()
    {
        // A labelled model whose credential can't serve a round must not shadow a usable one — the
        // same rule the default already applied, extended to tiers. And the fall-through still ends
        // on a REAL model, never on the broken one.
        bool Usable(string id) => id != "z-ai/glm-5.2";

        Assert.Null(ModelTierCatalog.ResolveLabel(
            ModelTierDefaults.All.Single(t => t.Id == ModelTierDefaults.Reasoning), Catalog, null, Usable));
        Assert.Equal("qwen/qwen3-next-80b-a3b-instruct", ModelTierCatalog.Default(Catalog, Usable));
        Assert.Equal("qwen/qwen3-next-80b-a3b-instruct",
            Resolve(ModelTierDefaults.Reasoning, Catalog, usable: Usable));
    }

    [Fact]
    public void EmptyCatalogIsExhausted_NeverThrows()
    {
        // The ONE null outcome, and it is reported rather than silently returned: the caller has to
        // be able to fail loudly instead of running a round on nothing.
        var resolution = ModelTierCatalog.Resolve(ModelTierDefaults.Coding, []);

        Assert.Null(resolution.ModelId);
        Assert.Equal(ModelTierSource.None, resolution.Source);
        Assert.True(resolution.IsExhausted);
        Assert.Null(ModelTierCatalog.Default([]));
    }

    [Fact]
    public void OnlyARouterAvailableStillYieldsNoModel()
    {
        var routerOnly = Catalog.RemoveAll(c => !c.IsRouter);

        Assert.Null(ModelTierCatalog.Default(routerOnly));
        Assert.True(ModelTierCatalog.Resolve(ModelTierDefaults.Chat, routerOnly).IsExhausted);
    }

    [Fact]
    public void AnUnlabelledCatalogStillServesEveryTier()
    {
        // Strip every label: a deployment that never labelled a thing must still run every agent.
        var unlabelled = Catalog.RemoveAll(c => c.IsRouter)
            .Select(c => c with { Tier = null })
            .ToImmutableList();

        foreach (var tier in ModelTierDefaults.All)
            Assert.Equal("z-ai/glm-5.2", Resolve(tier.Id, unlabelled));
    }

    [Fact]
    public void AnEmptyTierRegistryFallsBackToTheShippedTiers()
    {
        // Every tier node deleted (or a cold snapshot): resolution must not depend on a node being
        // there, so the shipped tiers stand in and the labels on the model nodes keep working.
        Assert.Equal("moonshotai/kimi-k3",
            Resolve(ModelTierDefaults.Coding, Catalog, tiers: Array.Empty<ModelTierDefinition>()));
        Assert.Equal("moonshotai/kimi-k3", Resolve(ModelTierDefaults.Coding, Catalog, tiers: null));
    }

    [Theory]
    [InlineData("utility")]
    [InlineData("s")]
    [InlineData("small")]
    public void LegacyAndSizeSpellingsResolveToUtility(string label) =>
        Assert.Equal("amazon/nova-micro-v1", Resolve(label, Catalog));

    [Theory]
    [InlineData("chat")]
    [InlineData("light")]
    [InlineData("m")]
    [InlineData("medium")]
    public void LegacyAndSizeSpellingsResolveToChat(string label) =>
        Assert.Equal("qwen/qwen3-next-80b-a3b-instruct", Resolve(label, Catalog));

    [Theory]
    [InlineData("reasoning")]
    [InlineData("standard")]
    [InlineData("l")]
    [InlineData("large")]
    public void LegacyAndSizeSpellingsResolveToReasoning(string label) =>
        Assert.Equal("z-ai/glm-5.2", Resolve(label, Catalog));

    [Theory]
    [InlineData("coding")]
    [InlineData("Heavy")]
    [InlineData("xl")]
    [InlineData("  XL  ")]
    [InlineData("code")]
    public void LegacyAndSizeSpellingsResolveToCoding(string label) =>
        Assert.Equal("moonshotai/kimi-k3", Resolve(label, Catalog));

    [Fact]
    public void AModelLabelledWithALegacySpellingIsTheSameRung()
    {
        // The alias equivalence has to hold on the MODEL NODE too, not just on the agent's ask:
        // a deployment mid-migration has `heavy` on the node and `coding` on the agent.
        var legacyLabelled = Catalog
            .RemoveAll(c => c.Tier == ModelTierDefaults.Coding)
            .Add(new("moonshotai/kimi-k3", 7, "heavy", false));

        Assert.Equal("moonshotai/kimi-k3", Resolve(ModelTierDefaults.Coding, legacyLabelled));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gigantic")]
    public void UnknownTierIsAMiss_NotAThrow(string? label)
    {
        Assert.Null(ModelTierCatalog.Find(label, ModelTierDefaults.All));
        // …and an agent declaring nonsense still runs, on the default.
        var resolution = ModelTierCatalog.Resolve(label, Catalog);
        Assert.Equal("z-ai/glm-5.2", resolution.ModelId);
        Assert.Equal(ModelTierSource.DeploymentDefault, resolution.Source);
        // An unknown label was never a tier, so it is not an "unpopulated tier" either — there is
        // nothing for an operator to go and label.
        Assert.False(resolution.IsUnpopulatedTierFallback);
    }

    [Fact]
    public void ADeploymentsOwnTiersAreWhatResolve()
    {
        // The point of tiers-as-NODES: rename a rung, add one, and resolution follows the data.
        ImmutableArray<ModelTierDefinition> renamed =
        [
            new() { Id = "cheap", Rank = 0, Aliases = ["utility"] },
            new() { Id = "vision", Rank = 15 },
        ];
        var catalog = Catalog.Add(new("some/vision-model", 4, "vision", false));

        Assert.Equal("some/vision-model", Resolve("vision", catalog, renamed));
        // The renamed rung still answers its old spelling, because the alias is data on the node.
        Assert.Equal("amazon/nova-micro-v1", Resolve("cheap", catalog, renamed));
        Assert.Equal("amazon/nova-micro-v1", Resolve("utility", catalog, renamed));
        // A tier the deployment DELETED is no longer a tier — the ask falls to the default.
        Assert.Equal("z-ai/glm-5.2", Resolve(ModelTierDefaults.Coding, catalog, renamed));
    }

    // ---- the deprecated ModelTier:* config rung -------------------------------------------------

    private static Func<ModelTierDefinition, string?> Legacy(ModelTierConfiguration config)
        => config.Resolve;

    [Fact]
    public void LegacyConfigAnswersATierNoModelNodeCarries()
    {
        // A portal upgrading from the config keys must NOT silently lose its mapping: the model is
        // in the catalog but carries no tier label, and only ModelTier:Heavy names it.
        var unlabelledCoder = Catalog
            .RemoveAll(c => c.Tier == ModelTierDefaults.Coding)
            .Add(new("moonshotai/kimi-k3", 7, null, false));
        var config = new ModelTierConfiguration { Heavy = "moonshotai/kimi-k3" };

        var resolution = ModelTierCatalog.Resolve(
            ModelTierDefaults.Coding, unlabelledCoder, legacyTierConfig: Legacy(config));

        Assert.Equal("moonshotai/kimi-k3", resolution.ModelId);
        Assert.Equal(ModelTierSource.LegacyConfig, resolution.Source);
    }

    [Fact]
    public void ALabelledNodeBeatsTheDeprecatedConfig()
    {
        // One tiering system at the end: the node label is the system, the config is the shim.
        var config = new ModelTierConfiguration { Heavy = "amazon/nova-micro-v1" };

        var resolution = ModelTierCatalog.Resolve(
            ModelTierDefaults.Coding, Catalog, legacyTierConfig: Legacy(config));

        Assert.Equal("moonshotai/kimi-k3", resolution.ModelId);
        Assert.Equal(ModelTierSource.Label, resolution.Source);
    }

    [Fact]
    public void LegacyConfigNamingAnUnservableModelIsIgnored()
    {
        // Seeding an unverifiable model is exactly how one fallback lands on another broken one.
        var noCoder = Catalog.RemoveAll(c => c.Tier == ModelTierDefaults.Coding);
        var config = new ModelTierConfiguration { Heavy = "a-model-that-was-deleted" };

        var resolution = ModelTierCatalog.Resolve(
            ModelTierDefaults.Coding, noCoder, legacyTierConfig: Legacy(config));

        Assert.Equal("z-ai/glm-5.2", resolution.ModelId);
        Assert.Equal(ModelTierSource.DeploymentDefault, resolution.Source);
    }

    [Fact]
    public void LegacyKeysMapOntoTheNewTiersByRank()
    {
        var config = new ModelTierConfiguration
        {
            Heavy = "heavy-model",
            Standard = "standard-model",
            Light = "light-model",
            Utility = "utility-model",
        };

        Assert.Equal("heavy-model", config.Resolve(ModelTierDefaults.Coding, ModelTierDefaults.All));
        Assert.Equal("standard-model", config.Resolve(ModelTierDefaults.Reasoning, ModelTierDefaults.All));
        Assert.Equal("light-model", config.Resolve(ModelTierDefaults.Chat, ModelTierDefaults.All));
        Assert.Equal("utility-model", config.Resolve(ModelTierDefaults.Utility, ModelTierDefaults.All));
        Assert.False(config.IsEmpty);
        Assert.True(new ModelTierConfiguration().IsEmpty);
    }

    [Fact]
    public void LegacyUtilityDegradesWithinTheSectionWhenTheNewestKeyWasNeverSet()
    {
        // ModelTier:Utility shipped after the other three; a deployment that never set it must still
        // route micro-jobs at its cheapest CONFIGURED rung rather than skipping the whole section.
        var config = new ModelTierConfiguration { Light = "light-model", Standard = "standard-model" };

        Assert.Equal("light-model", config.Resolve(ModelTierDefaults.Utility, ModelTierDefaults.All));
    }
}
