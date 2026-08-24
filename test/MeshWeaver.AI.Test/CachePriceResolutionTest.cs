#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the CACHE-RATE half of the DeepSeek cost defect: a cache-read price authored on a
/// <c>LanguageModel</c> node, or shipped in the built-in table, must reach
/// <see cref="ModelPriceRate"/> instead of silently falling back to the Anthropic 0.1x convention.
///
/// <para>The 0.1x fallback is not universal. Azure Foundry bills DeepSeek cache reads on a
/// separate meter at exactly <b>1/12</b> of the input rate — derived from Azure Cost Management
/// over four independent days whose cache ratios ranged 33%–76%, agreeing to four decimals
/// (V4-Pro: 1.4274 input → 0.11895 cached CHF/M). On a workload that is ~98% cache reads the
/// difference between 1/10 and 1/12 stops being a rounding error.</para>
/// </summary>
public class CachePriceResolutionTest
{
    /// <summary>The built-in table must carry DeepSeek's real 1/12 read rate, not the 0.1x default.</summary>
    [Fact]
    public void BuiltInDefaults_PriceDeepSeekCacheReadsAtOneTwelfth()
    {
        var rate = ModelPricing.Default("DeepSeek-V4-Pro");

        rate.Should().NotBeNull();
        rate!.CacheReadPerMillion.Should().Be(1.75m / 12m,
            "Azure bills Foundry DeepSeek cache reads at exactly 1/12 of input, not the "
            + "Anthropic-standard 0.1x the fallback would otherwise apply");
    }

    /// <summary>An authored node price must win over the built-in table — including the cache rates.</summary>
    [Fact]
    public void AuthoredNodePrice_CarriesCacheRatesIntoTheRate()
    {
        var node = new MeshNode("m", "Provider/AzureFoundry")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Content = new ModelDefinition
            {
                Id = "priced-model",
                Provider = "AzureFoundry",
                InputPricePerMillionTokens = 12m,
                OutputPricePerMillionTokens = 24m,
                CacheReadPricePerMillionTokens = 1m,
                CacheWritePricePerMillionTokens = 15m,
                Currency = "CHF",
            },
        };

        var catalog = ModelPriceCatalog.FromNodes([node], JsonSerializerOptions.Default);
        var rate = ModelPriceCatalog.RateFor("priced-model", catalog);

        rate.Should().NotBeNull();
        rate!.CacheReadPerMillion.Should().Be(1m, "the authored cache-read price must not be dropped");
        rate.CacheWritePerMillion.Should().Be(15m, "the authored cache-write price must not be dropped");
    }

    /// <summary>
    /// A node priced WITHOUT cache rates still falls back to the 0.1x / 1.25x convention — the
    /// change adds precision where it is authored, it does not make unpriced models free.
    /// </summary>
    [Fact]
    public void NodeWithoutCacheRates_KeepsTheConventionalFallback()
    {
        var node = new MeshNode("m", "Provider/X")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Content = new ModelDefinition
            {
                Id = "plain-model",
                Provider = "X",
                InputPricePerMillionTokens = 10m,
                OutputPricePerMillionTokens = 20m,
            },
        };

        var catalog = ModelPriceCatalog.FromNodes([node], JsonSerializerOptions.Default);
        var rate = ModelPriceCatalog.RateFor("plain-model", catalog);

        rate.Should().NotBeNull();
        rate!.CacheReadPerMillion.Should().BeNull("null means 'use the 0.1x convention'");

        // 1M cache reads at the conventional 0.1x of a 10/M input rate = 1.00.
        rate.Cost(1_000_000, 0, 1_000_000, 0).Should().Be(1.00m);
    }

    /// <summary>
    /// The arithmetic that was wrong in production, at the CORRECT rate: 98% cache reads on
    /// DeepSeek-V4-Pro cost a fraction of the same prompt billed entirely at 1x.
    /// </summary>
    [Fact]
    public void CacheAwareCost_CollapsesOnACacheHeavyRound()
    {
        var rate = ModelPricing.Default("DeepSeek-V4-Pro")!;
        const long input = 1_000_000;
        var cached = (long)(input * 0.98);

        var blind = rate.Cost(input, 1_000);                      // every prompt token at 1x
        var aware = rate.Cost(input, 1_000, cached, 0);           // 98% at 1/12

        aware.Should().BeLessThan(blind / 5,
            "a 98%-cached round is the measured steady state once the prompt prefix is stable");
    }
}
