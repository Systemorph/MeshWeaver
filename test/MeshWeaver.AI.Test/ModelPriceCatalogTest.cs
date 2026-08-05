#pragma warning disable CS1591

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Contract for catalog-backed pricing — the fix for cost surfaces billing at <b>$0</b> for every
/// model outside the compiled-in <see cref="ModelPricing.Defaults"/> table (OpenRouter /
/// OpenAI-compatible / BYO-key models). The rules pinned here:
/// <list type="number">
///   <item><b>The node price wins.</b> A <c>LanguageModel</c> node that carries both per-million
///   prices prices its model, overriding the built-in table.</item>
///   <item><b>The built-in table is the fallback, not the source.</b> An unpriced (or absent) node
///   falls through to <see cref="ModelPricing.Default(string?)"/>; an unknown model yields
///   <c>null</c> — callers show tokens WITHOUT a cost rather than a fake $0.</item>
///   <item><b>Every content shape reads.</b> Node content arrives typed, as
///   <see cref="JsonElement"/>, or as <see cref="JsonObject"/> depending on the source hub — all
///   three must price (the silent-empty-read trap).</item>
///   <item><b>Both id shapes resolve.</b> Usage rows record a bare wire id, an <c>org/model</c>
///   slug, or a node path; all three find their rate.</item>
/// </list>
/// Pure units — no mesh, no hub: the logic under test is a snapshot → lookup projection.
/// </summary>
public class ModelPriceCatalogTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A catalog model node the way the mesh actually stores one: the WIRE id
    /// (<paramref name="wireId"/>, possibly an <c>org/model</c> slug) lives in the content, while
    /// the node's own id is its last segment under <paramref name="ns"/> — so
    /// <c>("moonshotai/kimi-k3", "Provider/OpenRouter/moonshotai")</c> yields the real path
    /// <c>Provider/OpenRouter/moonshotai/kimi-k3</c>. Building it any other way would test a node
    /// shape that never occurs.
    /// </summary>
    private static MeshNode Model(
        string wireId, string ns, decimal? input, decimal? output, string? currency = "USD")
        => new(wireId.Split('/')[^1], ns)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = wireId,
            Content = new ModelDefinition
            {
                Id = wireId,
                Provider = "OpenRouter",
                InputPricePerMillionTokens = input,
                OutputPricePerMillionTokens = output,
                Currency = currency,
            }
        };

    [Fact]
    public void AuthoredNodePrice_BeatsTheBuiltInTable()
    {
        // kimi-k3 IS in Defaults (3/15) — the node's own price must still win, otherwise an admin's
        // edit in Settings ▸ Language Models is decorative.
        var catalog = ModelPriceCatalog.FromNodes(
            [Model("moonshotai/kimi-k3", "Provider/OpenRouter/moonshotai", 9m, 11m)], Json);

        var rate = ModelPriceCatalog.RateFor("moonshotai/kimi-k3", catalog);

        rate.Should().NotBeNull();
        rate!.InputPerMillion.Should().Be(9m);
        rate.OutputPerMillion.Should().Be(11m);
    }

    [Fact]
    public void NodePath_ResolvesToTheSameRateAsTheBareId()
    {
        var catalog = ModelPriceCatalog.FromNodes(
            [Model("moonshotai/kimi-k3", "Provider/OpenRouter/moonshotai", 3m, 15m)], Json);

        ModelPriceCatalog.RateFor("Provider/OpenRouter/moonshotai/kimi-k3", catalog)
            .Should().Be(ModelPriceCatalog.RateFor("moonshotai/kimi-k3", catalog),
                "a composer selection persists the node PATH while a usage row records the wire id");
    }

    [Fact]
    public void BareIdInUsage_ResolvesAgainstASlashKeyedCatalogEntry()
    {
        // The catalog key is "DeepSeek-V4-Flash"; a usage row that recorded the PATH still prices.
        // Deliberately OFF the built-in rate (0.55) so the assertion can only pass via the catalog.
        var catalog = ModelPriceCatalog.FromNodes(
            [Model("DeepSeek-V4-Flash", "Provider/Azure", 0.77m, 1.30m)], Json);

        var rate = ModelPriceCatalog.RateFor("Provider/Azure/DeepSeek-V4-Flash", catalog);

        rate.Should().NotBeNull();
        rate!.InputPerMillion.Should().Be(0.77m);
    }

    [Fact]
    public void UnpricedNode_FallsThroughToTheBuiltInTable()
    {
        // Half-priced (input only) must NOT shadow the table with a partial rate.
        var catalog = ModelPriceCatalog.FromNodes(
            [Model("claude-sonnet-4-6", "Provider/Anthropic", 42m, null)], Json);

        var rate = ModelPriceCatalog.RateFor("claude-sonnet-4-6", catalog);

        rate.Should().Be(ModelPricing.Default("claude-sonnet-4-6"),
            "a node missing one of the two prices contributes nothing");
    }

    [Fact]
    public void EmptyCatalog_StillPricesEveryBuiltInModel()
    {
        // The chip seeds an empty catalog before the model query emits — pricing must not blank out
        // in that window.
        var rate = ModelPriceCatalog.RateFor("DeepSeek-V4-Pro", catalog: null);

        rate.Should().Be(ModelPricing.Default("DeepSeek-V4-Pro"));
    }

    [Fact]
    public void UnknownModel_YieldsNull_NotZero()
    {
        ModelPriceCatalog.RateFor("some-unlisted-local-model", catalog: null)
            .Should().BeNull("callers must show tokens without a cost rather than a fabricated $0");
    }

    [Fact]
    public void OpenRouterGatewayIds_ArePricedByTheBuiltInTable()
    {
        // The backstop rows: an OpenRouter model seeded without an authored price still bills.
        ModelPricing.Default("z-ai/glm-5.2").Should().NotBeNull();
        ModelPricing.Default("moonshotai/kimi-k3").Should().NotBeNull();
        // …and the org/model slug is matched WHOLE, never mangled to its last segment.
        ModelPricing.Default("moonshotai/kimi-k3")!.OutputPerMillion.Should().Be(15m);
    }

    [Fact]
    public void JsonElementContent_Prices()
    {
        var json = JsonSerializer.Serialize(new ModelDefinition
        {
            Id = "z-ai/glm-5.2",
            Provider = "OpenRouter",
            InputPricePerMillionTokens = 0.6286m,
            OutputPricePerMillionTokens = 1.9756m,
            Currency = "USD",
        }, Json);
        var node = new MeshNode("glm-5.2", "Provider/OpenRouter/z-ai")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Content = JsonSerializer.Deserialize<JsonElement>(json, Json),
        };

        var rate = ModelPriceCatalog.RateFor("z-ai/glm-5.2", ModelPriceCatalog.FromNodes([node], Json));

        rate.Should().NotBeNull();
        rate!.InputPerMillion.Should().Be(0.6286m);
    }

    [Fact]
    public void JsonObjectContent_Prices()
    {
        // The node BUILDERS emit JsonObject — neither typed nor JsonElement. A reader that tests
        // `is JsonElement` reads NOTHING here, silently.
        var node = new MeshNode("glm-5.2", "Provider/OpenRouter/z-ai")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Content = new JsonObject
            {
                ["$type"] = "ModelDefinition",
                ["id"] = "z-ai/glm-5.2",
                ["provider"] = "OpenRouter",
                ["inputPricePerMillionTokens"] = 0.6286m,
                ["outputPricePerMillionTokens"] = 1.9756m,
                ["currency"] = "USD",
            },
        };

        var rate = ModelPriceCatalog.RateFor("z-ai/glm-5.2", ModelPriceCatalog.FromNodes([node], Json));

        rate.Should().NotBeNull();
        rate!.OutputPerMillion.Should().Be(1.9756m);
    }

    [Fact]
    public void NonModelNodes_AreIgnored()
    {
        var provider = new MeshNode("OpenRouter", "Provider")
        {
            NodeType = ModelProviderNodeType.NodeType,
            Content = new ModelProviderConfiguration { Provider = "OpenRouter", ApiKey = "sk-secret" },
        };

        ModelPriceCatalog.FromNodes([provider], Json).Should().BeEmpty();
    }

    [Fact]
    public void CacheAwareCost_ChargesReadsAndWritesAtTheirOwnRates()
    {
        var rate = new ModelPriceRate(10m, 20m, "USD");

        // 1M input of which 500k cache reads (0.1×) + 200k cache writes (1.25×), 1M output.
        var cost = rate.Cost(1_000_000, 1_000_000, 500_000, 200_000);

        // 300k fresh @10 + 500k @1 + 200k @12.5 + 1M @20 = 3 + 0.5 + 2.5 + 20
        cost.Should().Be(26m);
        cost.Should().BeLessThan(rate.Cost(1_000_000, 1_000_000),
            "billing every prompt token at the standard rate over-states a cache-heavy thread");
    }

    [Fact]
    public void ToModelInfo_CarriesTheNodeNameAsLabel()
    {
        // The selected-model chip renders this instead of the raw wire id.
        var node = new MeshNode("kimi-k3", "Provider/OpenRouter/moonshotai")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = "Kimi K3 · $3.00/$15.00",
            Content = new ModelDefinition { Id = "moonshotai/kimi-k3", Provider = "OpenRouter" },
        };

        var info = AgentPickerProjection.ToModelInfo(node, Json);

        info.Should().NotBeNull();
        info!.Label.Should().Be("Kimi K3 · $3.00/$15.00");
        info.Name.Should().Be("moonshotai/kimi-k3", "the wire id must pass through UNCHANGED");
        info.Path.Should().Be("Provider/OpenRouter/moonshotai/kimi-k3");
    }

    [Fact]
    public void ToModelInfo_FallsBackToDisplayName_ThenLeavesLabelNull()
    {
        var noName = new MeshNode("kimi-k3", "Provider/OpenRouter/moonshotai")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Content = new ModelDefinition
            {
                Id = "moonshotai/kimi-k3", Provider = "OpenRouter", DisplayName = "Kimi K3",
            },
        };
        var bare = new MeshNode("kimi-k3", "Provider/OpenRouter/moonshotai")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Content = new ModelDefinition { Id = "moonshotai/kimi-k3", Provider = "OpenRouter" },
        };

        AgentPickerProjection.ToModelInfo(noName, Json)!.Label.Should().Be("Kimi K3");
        AgentPickerProjection.ToModelInfo(bare, Json)!.Label.Should().BeNull(
            "no authored label — the caller falls back to the path's last segment");
    }

    [Fact]
    public void KeysAreCaseInsensitive()
    {
        // Off-table price again: matching the built-in 0.55 would let this pass without the catalog.
        var catalog = ModelPriceCatalog.FromNodes(
            [Model("DeepSeek-V4-Flash", "Provider/Azure", 0.77m, 1.30m)], Json);

        ModelPriceCatalog.RateFor("deepseek-v4-flash", catalog)!.InputPerMillion.Should().Be(0.77m);
    }

    [Fact]
    public void FirstPricedNodeWins_OnDuplicateIds()
    {
        // Root catalog + a user's BYO node can both carry the same id; the choice only needs to be
        // deterministic (both are legitimate prices for that id).
        var catalog = ModelPriceCatalog.FromNodes(
        [
            Model("moonshotai/kimi-k3", "Provider/OpenRouter/moonshotai", 3m, 15m),
            Model("moonshotai/kimi-k3", "rbuergi/_Memex/OpenRouter/moonshotai", 4m, 16m),
        ], Json);

        ModelPriceCatalog.RateFor("moonshotai/kimi-k3", catalog)!.InputPerMillion.Should().Be(3m);
    }

    [Fact]
    public void NullSnapshot_IsEmptyNotAThrow()
    {
        ModelPriceCatalog.FromNodes(null, Json).Should().BeEmpty();
    }

    [Fact]
    public void BlankModelId_YieldsNull()
    {
        ModelPriceCatalog.RateFor(null, null).Should().BeNull();
        ModelPriceCatalog.RateFor("  ", null).Should().BeNull();
    }
}
