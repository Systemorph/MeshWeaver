#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pure-logic tests for token-cost pricing and the summary builders — no hub, no
/// async. Pins: per-million cost math, the built-in default price table + node
/// override resolution, per-model row building, multi-thread merge, and the HTML
/// render. The end-to-end accumulation onto a thread is covered by
/// <see cref="ThreadTokenUsageTest"/>.
/// </summary>
public class TokenCostTest
{
    // ─── ModelPriceRate.Cost ───

    [Fact]
    public void Cost_OneMillionEach_IsInputPlusOutputRate()
    {
        var rate = new ModelPriceRate(5m, 25m, "USD");
        rate.Cost(1_000_000, 1_000_000).Should().Be(30m);
    }

    [Fact]
    public void Cost_PartialTokens_ProratesPerMillion()
    {
        var rate = new ModelPriceRate(3m, 15m, "USD");
        // 500k in @ $3/M = 1.50 ; 200k out @ $15/M = 3.00 → 4.50
        rate.Cost(500_000, 200_000).Should().Be(4.5m);
    }

    // ─── ModelPricing.Default ───

    [Theory]
    [InlineData("claude-opus-4-6", 5, 25)]
    [InlineData("claude-sonnet-4-6", 3, 15)]
    [InlineData("claude-haiku-4-5", 1, 5)]
    public void Default_KnownAnthropicModels_HavePublishedRates(string id, int inPrice, int outPrice)
    {
        var rate = ModelPricing.Default(id);
        rate.Should().NotBeNull();
        rate!.InputPerMillion.Should().Be(inPrice);
        rate.OutputPerMillion.Should().Be(outPrice);
        rate.Currency.Should().Be("USD");
    }

    [Fact]
    public void Default_UnknownModel_IsNull()
        => ModelPricing.Default("totally-made-up-model").Should().BeNull();

    [Fact]
    public void Default_PathPrefixedId_FallsBackToLastSegment()
    {
        // A provider-prefixed id still resolves via its last segment.
        var rate = ModelPricing.Default("_Provider/Anthropic/claude-opus-4-6");
        rate.Should().NotBeNull();
        rate!.InputPerMillion.Should().Be(5m);
    }

    // ─── ModelPricing.Resolve ───

    [Fact]
    public void Resolve_NodeWithExplicitPrice_WinsOverDefault()
    {
        var node = new ModelDefinition
        {
            Id = "claude-opus-4-6",
            Provider = "Anthropic",
            InputPricePerMillionTokens = 7m,
            OutputPricePerMillionTokens = 35m,
            Currency = "EUR"
        };
        var rate = ModelPricing.Resolve("claude-opus-4-6", node);
        rate.Should().NotBeNull();
        rate!.InputPerMillion.Should().Be(7m);
        rate.OutputPerMillion.Should().Be(35m);
        rate.Currency.Should().Be("EUR");
    }

    [Fact]
    public void Resolve_NodeWithoutPrice_FallsBackToDefault()
    {
        var node = new ModelDefinition { Id = "claude-haiku-4-5", Provider = "Anthropic" };
        var rate = ModelPricing.Resolve("claude-haiku-4-5", node);
        rate.Should().NotBeNull();
        rate!.InputPerMillion.Should().Be(1m);
        rate.OutputPerMillion.Should().Be(5m);
    }

    [Fact]
    public void Resolve_NoNodeNoDefault_IsNull()
        => ModelPricing.Resolve("unknown", null).Should().BeNull();

    // ─── TokenCostSummary.BuildRows ───

    [Fact]
    public void BuildRows_PricesPerModel_SortsByTotalDesc_DropsZero()
    {
        var tokens = new Dictionary<string, ModelTokenUsage>
        {
            ["claude-haiku-4-5"] = new() { InputTokens = 100, OutputTokens = 50 },
            ["claude-opus-4-6"] = new() { InputTokens = 1_000_000, OutputTokens = 200_000 },
            ["zero-model"] = new() { InputTokens = 0, OutputTokens = 0 },
        };

        var rows = TokenCostSummary.BuildRows(tokens, id => ModelPricing.Default(id));

        rows.Should().HaveCount(2, "the zero-usage model is dropped");
        rows[0].Model.Should().Be("claude-opus-4-6", "highest total tokens sorts first");
        // Opus: 1M in @ $5 + 0.2M out @ $25 = 5 + 5 = 10
        rows[0].Cost.Should().Be(10m);
        rows[0].Currency.Should().Be("USD");
    }

    [Fact]
    public void BuildRows_UnknownModel_HasNullCost()
    {
        var tokens = new Dictionary<string, ModelTokenUsage>
        {
            ["mystery"] = new() { InputTokens = 10, OutputTokens = 5 },
        };
        var rows = TokenCostSummary.BuildRows(tokens, id => ModelPricing.Default(id));
        rows.Should().ContainSingle();
        rows[0].Cost.Should().BeNull("no price is known for the model");
    }

    // ─── TokenCostSummary.Merge ───

    [Fact]
    public void Merge_SumsPerModel_AcrossThreads()
    {
        var a = new Dictionary<string, ModelTokenUsage>
        {
            ["m1"] = new() { InputTokens = 10, OutputTokens = 1 },
            ["m2"] = new() { InputTokens = 5, OutputTokens = 2 },
        };
        var b = new Dictionary<string, ModelTokenUsage>
        {
            ["m1"] = new() { InputTokens = 20, OutputTokens = 3 },
        };

        var merged = TokenCostSummary.Merge(new[] { a, b });

        merged["m1"].InputTokens.Should().Be(30);
        merged["m1"].OutputTokens.Should().Be(4);
        merged["m2"].InputTokens.Should().Be(5);
    }

    [Fact]
    public void Merge_Empty_IsEmpty()
        => TokenCostSummary.Merge(Array.Empty<IReadOnlyDictionary<string, ModelTokenUsage>>())
            .Should().BeEmpty();

    // ─── TokenCostSummary.RenderHtml ───

    [Fact]
    public void RenderHtml_NoRows_ShowsEmptyText()
    {
        var html = TokenCostSummary.RenderHtml(Array.Empty<TokenCostSummary.CostRow>(), "nothing here");
        html.Should().Contain("nothing here");
        html.Should().NotContain("<table");
    }

    [Fact]
    public void RenderHtml_WithRows_ShowsModelTokensAndTotal()
    {
        var rows = new List<TokenCostSummary.CostRow>
        {
            new("claude-opus-4-6", 1_000_000, 200_000, 10m, "USD"),
        };
        var html = TokenCostSummary.RenderHtml(rows);
        html.Should().Contain("claude-opus-4-6");
        html.Should().Contain("1,000,000");
        html.Should().Contain("Total");
        html.Should().Contain("USD");
    }

    // ─── ModelTokenUsage.Add ───

    [Fact]
    public void Add_AccumulatesImmutably()
    {
        var u = new ModelTokenUsage { InputTokens = 10, OutputTokens = 2 };
        var u2 = u.Add(5, 3);
        u2.InputTokens.Should().Be(15);
        u2.OutputTokens.Should().Be(5);
        u.InputTokens.Should().Be(10, "the original is unchanged (immutable record)");
    }
}
