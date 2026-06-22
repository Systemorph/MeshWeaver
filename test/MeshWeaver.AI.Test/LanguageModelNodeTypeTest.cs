#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests covering the <c>nodeType:LanguageModel</c> surface â€” the platform
/// reads <see cref="LanguageModelCatalogOptions.Sources"/> at mesh init
/// time and emits one static <see cref="LanguageModelNodeType.NodeType"/>
/// MeshNode per <c>{section}:Models[]</c> entry under
/// <see cref="LanguageModelNodeType.RootNamespace"/>. So a query like
/// <c>namespace:Model nodeType:LanguageModel</c> always returns the
/// deployed catalog. Tests exercise <see cref="BuiltInLanguageModelProvider"/>
/// directly with synthetic IConfiguration + catalog options.
/// </summary>
public class LanguageModelNodeTypeTest
{
    [Fact]
    public void Constants_NamespaceAndNodeType_AreStable()
    {
        // Public mesh-query contract â€” anyone typing `namespace:Model
        // nodeType:LanguageModel` in a search box depends on these not
        // silently shifting.
        LanguageModelNodeType.NodeType.Should().Be("LanguageModel");
        LanguageModelNodeType.RootNamespace.Should().Be("Model");
    }

    [Fact]
    public void Provider_OneSection_OneNodePerModel()
    {
        var provider = MakeProvider(
            new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-sonnet-4-6",
                ["Anthropic:Models:1"] = "claude-opus-4-6",
                ["Anthropic:Models:2"] = "claude-haiku-4-5"
            },
            new LanguageModelCatalogSource("Anthropic", "Azure Claude", 1));

        var modelNodes = provider.GetStaticNodes()
            .Where(n => n.NodeType == LanguageModelNodeType.NodeType)
            .ToList();

        modelNodes.Should().HaveCount(3);
        // LanguageModel children now live under the parent ModelProvider's
        // Provider/{name} satellite path (mirrors the user-partition
        // layout) — see ModelProviders.md for the path convention.
        var expectedNs = $"{ModelProviderNodeType.RootNamespace}/Azure Claude";
        modelNodes.Should().AllSatisfy(n =>
        {
            n.Namespace.Should().Be(expectedNs);
            n.Content.Should().BeOfType<ModelDefinition>();
        });
        modelNodes.Select(n => n.Id).Should().BeEquivalentTo(
            new[] { "claude-sonnet-4-6", "claude-opus-4-6", "claude-haiku-4-5" },
            System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public void Provider_ModelDefinition_CarriesIdProviderOrder()
    {
        var provider = MakeProvider(
            new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-sonnet-4-6"
            },
            new LanguageModelCatalogSource("Anthropic", "Azure Claude", 5));

        var node = provider.GetStaticNodes()
            .Single(n => n.NodeType == LanguageModelNodeType.NodeType);

        var def = node.Content.Should().BeOfType<ModelDefinition>().Subject;
        def.Id.Should().Be("claude-sonnet-4-6");
        def.Provider.Should().Be("Azure Claude");
        def.Order.Should().Be(5);
    }

    [Fact]
    public void Provider_TwoSourcesShareModelId_FirstWins()
    {
        // Two providers (e.g. Azure Claude + Direct Anthropic) both
        // advertise claude-sonnet-4-6 â€” the lower-Order source wins. The
        // picker shows it once, attributed to the winner.
        var provider = MakeProvider(
            new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-sonnet-4-6",
                ["Direct:Models:0"] = "claude-sonnet-4-6"
            },
            // Order matters â€” registered order = de-dup precedence.
            new LanguageModelCatalogSource("Anthropic", "Azure Claude", 1),
            new LanguageModelCatalogSource("Direct", "Direct Anthropic", 99));

        var modelNodes = provider.GetStaticNodes()
            .Where(n => n.NodeType == LanguageModelNodeType.NodeType)
            .ToList();

        modelNodes.Should().HaveCount(1);
        ((ModelDefinition)modelNodes[0].Content!).Provider.Should().Be("Azure Claude");
    }

    [Fact]
    public void Provider_EmptyCatalog_EmitsOnlyAccessPolicy()
    {
        // No catalog sources / no models in config = no provider/model CONTENT nodes. The
        // PublicRead _Policy is still emitted unconditionally so the /model picker can read the
        // (empty) Provider catalog under every user identity; it is a governance node that the
        // picker queries (nodeType:LanguageModel|ModelProvider) filter out, so it pollutes nothing.
        var nodes = MakeProvider(new Dictionary<string, string?>())
            .GetStaticNodes()
            .ToList();

        nodes.Should().NotContain(n => n.NodeType == LanguageModelNodeType.NodeType
            || n.NodeType == ModelProviderNodeType.NodeType);
        nodes.Should().OnlyContain(n => n.NodeType == "PartitionAccessPolicy");
    }

    [Fact]
    public void Provider_Policy_IsPublicRead_WithLiftedWriteCaps()
    {
        // The Provider partition's _Policy is PublicRead (every user reads the catalog) with the
        // write caps LIFTED (Create/Update/Delete = true) so platform admins can manage the shared
        // providers/models. Caps are CEILINGS, not grants — non-admins hold no write role in the
        // Provider hierarchy, so they stay read-only.
        var withModels = MakeProvider(
            new Dictionary<string, string?> { ["Anthropic:Models:0"] = "claude-sonnet-4-6" },
            new LanguageModelCatalogSource("Anthropic", "Azure Claude", 1));

        var nodes = withModels.GetStaticNodes().ToList();

        var policy = nodes.Should().ContainSingle(n => n.NodeType == "PartitionAccessPolicy").Subject;
        var aap = policy.Content.Should().BeOfType<MeshWeaver.Mesh.Security.PartitionAccessPolicy>().Subject;
        aap.PublicRead.Should().BeTrue();
        aap.Create.Should().BeTrue();
        aap.Update.Should().BeTrue();
        aap.Delete.Should().BeTrue();
        nodes.Should().Contain(n => n.NodeType == LanguageModelNodeType.NodeType);
    }

    [Fact]
    public void Provider_SectionWithEmptyOrWhitespaceModels_SkipsThem()
    {
        // Aspire/AppHost env var defaults can be empty strings â€” the
        // provider must skip them, not emit nodes with empty ids.
        var provider = MakeProvider(
            new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-sonnet-4-6",
                ["Anthropic:Models:1"] = "",
                ["Anthropic:Models:2"] = "   ",
                ["Anthropic:Models:3"] = "claude-haiku-4-5"
            },
            new LanguageModelCatalogSource("Anthropic", "Azure Claude", 1));

        var ids = provider.GetStaticNodes()
            .Where(n => n.NodeType == LanguageModelNodeType.NodeType)
            .Select(n => n.Id)
            .ToList();

        ids.Should().BeEquivalentTo(new[] { "claude-sonnet-4-6", "claude-haiku-4-5" }, System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public void Provider_MissingSection_NoCrash_NoNodes()
    {
        // Catalog source registered but the corresponding config section
        // absent â€” provider warns + skips, doesn't throw.
        var provider = MakeProvider(
            new Dictionary<string, string?>(),
            new LanguageModelCatalogSource("MissingProvider", "Missing", 1));

        var nodes = provider.GetStaticNodes()
            .Where(n => n.NodeType == LanguageModelNodeType.NodeType)
            .ToList();

        nodes.Should().BeEmpty();
    }

    /// <summary>
    /// Builds a provider with in-memory IConfiguration + a synthesized
    /// LanguageModelCatalogOptions. Mirrors how the real DI graph wires
    /// these dependencies.
    /// </summary>
    private static BuiltInLanguageModelProvider MakeProvider(
        IDictionary<string, string?> configValues,
        params LanguageModelCatalogSource[] sources)
    {
        // 🚦 Each Api source needs an Endpoint + ApiKey to count as CONFIGURED — otherwise
        // BuiltInLanguageModelProvider deliberately hides its models (an un-wired catalog would
        // put selectable-but-unusable models in the picker; see BuiltInLanguageModelProviderTest).
        // These catalog-shape tests exercise the emit path, so stamp synthetic credentials per
        // source section. A section with no Models[] still emits nothing (the empty/missing tests).
        var values = new Dictionary<string, string?>(configValues);
        foreach (var s in sources)
        {
            values.TryAdd($"{s.SectionName}:Endpoint", $"https://{s.SectionName}.example.com");
            values.TryAdd($"{s.SectionName}:ApiKey", "test-key");
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values!)
            .Build();
        var options = new LanguageModelCatalogOptions();
        foreach (var s in sources) options.Add(s);
        return new BuiltInLanguageModelProvider(config, options);
    }
}
