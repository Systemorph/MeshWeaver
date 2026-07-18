using System;
using System.Collections.Generic;
using System.Linq;
using Memex.Portal.Shared.Models;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Unit tests for the pure decision logic behind Ollama / OpenAI-compatible model auto-discovery
/// (<see cref="OpenAICompatibleModelSync"/>): the embedding filter, the add/remove diff + empty-guard
/// (<see cref="OpenAICompatibleModelSync.ComputeDelta"/>), and the platform LanguageModel node shape
/// (<see cref="OpenAICompatibleModelSync.BuildModelNode"/>). All deterministic — no mesh, no HTTP.
/// The reactive fetch/write orchestration is covered by the framework primitives it composes
/// (ProviderModelLister's IIoPool leaf, IMeshService.CreateOrUpdateNode/DeleteNode, the AsSystem pattern).
/// </summary>
public class OpenAICompatibleModelSyncTest
{
    // ── ComputeDelta ─────────────────────────────────────────────────────────

    [Fact]
    public void ComputeDelta_AddsMissingChatModels_AndExcludesTheEmbeddingModel()
    {
        // The configured embedding model (a second qwen here) is excluded from the chat catalog.
        var endpoint = new[] { "qwen3.6:latest", "qwen3-coder:30b", "qwen3-embedding:latest" };

        var delta = OpenAICompatibleModelSync.ComputeDelta(endpoint, Array.Empty<string>(), embeddingModel: "qwen3-embedding");

        Assert.False(delta.Skip);
        Assert.Equal(new[] { "qwen3.6:latest", "qwen3-coder:30b" }, delta.ToAdd);
        Assert.Empty(delta.ToRemove);
        Assert.DoesNotContain("qwen3-embedding:latest", delta.ToAdd); // the embedder never enters the chat catalog
    }

    [Fact]
    public void ComputeDelta_RemovesCatalogEntriesNoLongerInstalled()
    {
        var endpoint = new[] { "qwen3.6:latest" };
        var current = new[] { "qwen3.6:latest", "removed-model:latest" };

        var delta = OpenAICompatibleModelSync.ComputeDelta(endpoint, current, embeddingModel: null);

        Assert.False(delta.Skip);
        Assert.Empty(delta.ToAdd);
        Assert.Equal(new[] { "removed-model:latest" }, delta.ToRemove);
    }

    [Fact]
    public void ComputeDelta_SteadyState_IsANoOp()
    {
        var models = new[] { "a:latest", "b:latest" };

        var delta = OpenAICompatibleModelSync.ComputeDelta(models, models, embeddingModel: null);

        Assert.False(delta.Skip);
        Assert.Empty(delta.ToAdd);
        Assert.Empty(delta.ToRemove);
    }

    [Fact]
    public void ComputeDelta_CaseInsensitiveMatch_DoesNotChurnExistingModels()
    {
        var endpoint = new[] { "Keep:latest", "new:latest" };
        var current = new[] { "keep:latest", "gone:latest" };

        var delta = OpenAICompatibleModelSync.ComputeDelta(endpoint, current, embeddingModel: null);

        Assert.Equal(new[] { "new:latest" }, delta.ToAdd);      // "Keep" matches "keep" → not re-added
        Assert.Equal(new[] { "gone:latest" }, delta.ToRemove);  // "keep" is still present → not removed
    }

    [Fact]
    public void ComputeDelta_EmptyDesired_Skips_WithoutWipingTheCatalog()
    {
        // Endpoint returns nothing usable (only the embedder, or a transient/malformed empty response).
        var endpoint = new[] { "qwen3-embedding:latest" };
        var current = new[] { "qwen3.6:latest", "qwen3-coder:30b" };

        var delta = OpenAICompatibleModelSync.ComputeDelta(endpoint, current, embeddingModel: "qwen3-embedding");

        Assert.True(delta.Skip);            // guarded: never delete the whole catalog on an empty result
        Assert.Empty(delta.ToAdd);
        Assert.Empty(delta.ToRemove);
    }

    [Fact]
    public void ComputeDelta_TotallyEmptyEndpoint_Skips()
    {
        var delta = OpenAICompatibleModelSync.ComputeDelta(
            Array.Empty<string>(), new[] { "a:latest" }, embeddingModel: null);

        Assert.True(delta.Skip);
        Assert.Empty(delta.ToRemove);
    }

    [Fact]
    public void ComputeDelta_DeduplicatesDesired()
    {
        var endpoint = new[] { "a:latest", "A:latest", "b:latest" };

        var delta = OpenAICompatibleModelSync.ComputeDelta(endpoint, Array.Empty<string>(), embeddingModel: null);

        Assert.Equal(2, delta.ToAdd.Count); // "a:latest"/"A:latest" collapse to one
        Assert.Contains("b:latest", delta.ToAdd);
    }

    // ── GetBool / GetInt (issue #352: empty-string config must not crash startup) ──

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void GetBool_EmptyString_ReturnsDefault_DoesNotThrow()
    {
        // The Helm ConfigMap renders an unset allow-listed key as "" (issue #352). The framework's
        // GetValue<bool> THROWS on that empty string, killing host startup. GetBool must treat it as absent.
        var config = Config(("OpenAICompatible:DiscoverModels", ""));

        Assert.False(OpenAICompatibleModelSync.GetBool(config, "OpenAICompatible:DiscoverModels", false));

        // Pin the regression: the previous code path (GetValue<bool> on the same empty string) DID throw.
        Assert.Throws<InvalidOperationException>(
            () => config.GetValue("OpenAICompatible:DiscoverModels", false));
    }

    [Theory]
    [InlineData(null, false, false)]     // absent key → default
    [InlineData("", true, true)]         // empty (ConfigMap unset) → default
    [InlineData("   ", false, false)]    // whitespace → default
    [InlineData("true", false, true)]    // explicit true
    [InlineData("false", true, false)]   // explicit false
    [InlineData("TRUE", false, true)]    // case-insensitive
    [InlineData("garbage", false, false)] // unparseable → default (inert unless explicitly enabled)
    public void GetBool_HonoursDefaultUnlessExplicitlyParseable(string? value, bool defaultValue, bool expected)
        => Assert.Equal(expected, OpenAICompatibleModelSync.GetBool(
            Config(("OpenAICompatible:DiscoverModels", value)), "OpenAICompatible:DiscoverModels", defaultValue));

    [Theory]
    [InlineData(null, 20, 20)]           // absent → default
    [InlineData("", 120, 120)]           // empty (ConfigMap unset) → default, not a crash
    [InlineData("45", 20, 45)]           // explicit
    [InlineData("notanint", 20, 20)]     // unparseable → default
    public void GetInt_EmptyOrUnparseable_ReturnsDefault(string? value, int defaultValue, int expected)
        => Assert.Equal(expected, OpenAICompatibleModelSync.GetInt(
            Config(("OpenAICompatible:DiscoverIntervalSeconds", value)), "OpenAICompatible:DiscoverIntervalSeconds", defaultValue));

    // ── IsEmbedding ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("qwen3-embedding:latest", "qwen3-embedding", true)]  // matches configured embedder, tag on the endpoint side
    [InlineData("qwen3-embedding", "qwen3-embedding:latest", true)]  // tag on the config side — still matches (tag-agnostic)
    [InlineData("qwen3.6:latest", "qwen3-embedding", false)]         // a real chat model, not the configured embedder
    [InlineData("qwen3-coder:30b", "qwen3-embedding", false)]        // chat model with a non-latest tag
    [InlineData("qwen3.6:latest", null, false)]                     // no embedder configured → nothing excluded
    [InlineData("qwen3.6:latest", "", false)]                      // blank embedder config → nothing excluded
    public void IsEmbedding_ExcludesOnlyTheConfiguredEmbeddingModel(string modelId, string? embeddingModel, bool expected)
        => Assert.Equal(expected, OpenAICompatibleModelSync.IsEmbedding(modelId, embeddingModel));

    // ── BuildModelNode ───────────────────────────────────────────────────────

    [Fact]
    public void BuildModelNode_ProducesThePlatformProviderChildShape_WithTaggedId()
    {
        var node = OpenAICompatibleModelSync.BuildModelNode("qwen3.6:latest");

        Assert.Equal("qwen3.6:latest", node.Id);                              // tag preserved as the wire id
        Assert.Equal("Provider/OpenAICompatible", node.Namespace);
        Assert.Equal("LanguageModel", node.NodeType);
        Assert.Equal(SyncBehavior.ExcludeThisAndChildren, node.SyncBehavior); // create-if-absent, never pruned

        var def = Assert.IsType<ModelDefinition>(node.Content);
        Assert.Equal("qwen3.6:latest", def.Id);
        Assert.Equal("OpenAICompatible", def.Provider);
        Assert.Equal("Provider/OpenAICompatible", def.ProviderRef); // credentials resolved via the parent node
        Assert.Null(def.Endpoint);                                   // resolver follows ProviderRef, not a copied endpoint
        Assert.Null(def.ApiKeySecretRef);                            // never a key on a publicly-readable child
        Assert.Null(def.SupportsTools);                              // default: unknown (assume supported)
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void BuildModelNode_CarriesTheProbedToolSupport(bool? supportsTools)
    {
        var node = OpenAICompatibleModelSync.BuildModelNode("tiefighter:q6_k", supportsTools);
        var def = Assert.IsType<ModelDefinition>(node.Content);
        Assert.Equal(supportsTools, def.SupportsTools);
    }

    // ── ShouldBackfill (tool-capability backfill write-decision) ─────────────

    [Theory]
    // probe conclusive AND differs → write.
    [InlineData(null, false, true)]   // statically-seeded null → known tool-less: stamp it (the Mythalion fix)
    [InlineData(null, true, true)]    // null → known tool-capable: stamp it
    [InlineData(true, false, true)]   // a stale "supported" corrected to tool-less
    [InlineData(false, true, true)]   // a stale "tool-less" corrected to supported
    // probe indeterminate → never write (assume supported, historical behaviour).
    [InlineData(null, null, false)]
    [InlineData(true, null, false)]
    [InlineData(false, null, false)]
    // already correct → no churn (an unchanged reboot must not bump the node version).
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void ShouldBackfill_WritesOnlyWhenTheProbeIsConclusiveAndDiffers(bool? current, bool? probed, bool expected)
        => Assert.Equal(expected, OpenAICompatibleModelSync.ShouldBackfill(current, probed));
}
