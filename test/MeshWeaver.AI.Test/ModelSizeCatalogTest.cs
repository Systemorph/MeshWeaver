#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Immutable;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The size-label selection rules. Pure functions over the catalog — no hub, no snapshot, no
/// credentials — which is the point: "which model does an agent asking for L actually get" is
/// decidable from data alone, and stays decidable when an environment labels only some of its
/// models.
/// </summary>
public class ModelSizeCatalogTest
{
    // A catalog shaped like a real deployment: GLM pinned default at -1, a labelled S and M, an
    // XL, and the Auto router. Ordered deliberately out of Order so nothing passes by luck.
    private static ImmutableList<ModelSizeCandidate> Catalog =>
    [
        new("moonshotai/kimi-k3", 7, ModelSize.XL, false),
        new("openrouter/auto", -10, null, true),
        new("z-ai/glm-5.2", -1, ModelSize.L, false),
        new("amazon/nova-micro-v1", 3, ModelSize.S, false),
        new("qwen/qwen3-next-80b-a3b-instruct", 2, ModelSize.M, false),
    ];

    [Fact]
    public void ResolvesEachPopulatedLabel()
    {
        Assert.Equal("amazon/nova-micro-v1", ModelSizeCatalog.Resolve(ModelSize.S, Catalog));
        Assert.Equal("qwen/qwen3-next-80b-a3b-instruct", ModelSizeCatalog.Resolve(ModelSize.M, Catalog));
        Assert.Equal("z-ai/glm-5.2", ModelSizeCatalog.Resolve(ModelSize.L, Catalog));
        Assert.Equal("moonshotai/kimi-k3", ModelSizeCatalog.Resolve(ModelSize.XL, Catalog));
    }

    [Fact]
    public void LowestOrderWinsWithinALabel()
    {
        var twoLarges = Catalog.Add(new("second-large", 5, ModelSize.L, false));

        Assert.Equal("z-ai/glm-5.2", ModelSizeCatalog.Resolve(ModelSize.L, twoLarges));
    }

    [Fact]
    public void UnpopulatedLabelIsAMiss_NotAnError()
    {
        var noExtraLarge = Catalog.RemoveAll(c => c.Size == ModelSize.XL);

        Assert.Null(ModelSizeCatalog.Resolve(ModelSize.XL, noExtraLarge));
    }

    [Fact]
    public void UnpopulatedLabelFallsThroughToTheDefault()
    {
        // The whole point of not having to populate every size: asking for one nobody carries
        // still runs a round, on the default.
        var onlyLarge = Catalog.RemoveAll(c => c.Size is ModelSize.S or ModelSize.M or ModelSize.XL);

        Assert.Equal("z-ai/glm-5.2", ModelSizeCatalog.ResolveOrDefault("XL", onlyLarge));
        Assert.Equal("z-ai/glm-5.2", ModelSizeCatalog.ResolveOrDefault("S", onlyLarge));
    }

    [Fact]
    public void DefaultIsTheLowestOrder_RouterExcluded()
    {
        // openrouter/auto sorts first at -10 and must STILL not be the default: it dispatches
        // rather than answers, so defaulting to it would resolve Auto to Auto.
        Assert.Equal("z-ai/glm-5.2", ModelSizeCatalog.Default(Catalog));
    }

    [Fact]
    public void RouterIsNeverSelectedByLabelEither()
    {
        var labelledRouter = Catalog
            .RemoveAll(c => c.IsRouter)
            .Add(new("openrouter/auto", -10, ModelSize.L, true));

        Assert.Equal("z-ai/glm-5.2", ModelSizeCatalog.Resolve(ModelSize.L, labelledRouter));
    }

    [Fact]
    public void UnusableModelsAreSkipped()
    {
        // A labelled model whose credential can't serve a round must not shadow a usable one —
        // the same rule the default already applied, extended to labels.
        bool Usable(string id) => id != "z-ai/glm-5.2";

        Assert.Null(ModelSizeCatalog.Resolve(ModelSize.L, Catalog, Usable));
        Assert.Equal("qwen/qwen3-next-80b-a3b-instruct", ModelSizeCatalog.Default(Catalog, Usable));
        Assert.Equal("qwen/qwen3-next-80b-a3b-instruct", ModelSizeCatalog.ResolveOrDefault("L", Catalog, Usable));
    }

    [Fact]
    public void EmptyCatalogResolvesToNothing_NeverThrows()
    {
        Assert.Null(ModelSizeCatalog.Default([]));
        Assert.Null(ModelSizeCatalog.Resolve(ModelSize.L, []));
        Assert.Null(ModelSizeCatalog.ResolveOrDefault("L", []));
    }

    [Fact]
    public void OnlyARouterAvailableStillYieldsNoDefault()
    {
        var routerOnly = Catalog.RemoveAll(c => !c.IsRouter);

        Assert.Null(ModelSizeCatalog.Default(routerOnly));
        Assert.Null(ModelSizeCatalog.ResolveOrDefault("M", routerOnly));
    }

    [Theory]
    [InlineData("S", ModelSize.S)]
    [InlineData("utility", ModelSize.S)]
    [InlineData("m", ModelSize.M)]
    [InlineData("light", ModelSize.M)]
    [InlineData("L", ModelSize.L)]
    [InlineData("standard", ModelSize.L)]
    [InlineData("XL", ModelSize.XL)]
    [InlineData("Heavy", ModelSize.XL)]
    [InlineData("  xl  ", ModelSize.XL)]
    public void LegacyTierNamesParseToTheSameLabels(string input, ModelSize expected) =>
        Assert.Equal(expected, ModelSizes.Parse(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gigantic")]
    public void UnknownLabelIsAMiss_NotAThrow(string? input)
    {
        Assert.Null(ModelSizes.Parse(input));
        // …and an agent declaring nonsense still runs, on the default.
        Assert.Equal("z-ai/glm-5.2", ModelSizeCatalog.ResolveOrDefault(input, Catalog));
    }
}
