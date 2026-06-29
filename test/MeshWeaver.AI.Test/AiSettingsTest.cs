#pragma warning disable CS1591

using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests for the <see cref="AiSettings"/> config primitives that drive the chat composer:
/// default query templates, token resolution, and the empty-field-fallback projection.
/// </summary>
public class AiSettingsTest
{
    private static readonly JsonSerializerOptions Json = new();

    [Fact]
    public void ResolveQueries_SubstitutesPresentTokens_AndDropsTemplatesWithEmptyTokens()
    {
        var templates = new[]
        {
            "namespace:Agent nodeType:Agent",
            "path:{currentPath} nodeType:Agent scope:ancestors",
            "namespace:{nodeTypePath} nodeType:Agent scope:selfAndAncestors",
        };

        var resolved = AiSettingsNodeType.ResolveQueries(
            templates, currentPath: "ACME/Project", nodeTypePath: null, userPath: null);

        // {currentPath} substituted; the {nodeTypePath} template dropped (its token is empty).
        Assert.Equal(
            new[]
            {
                "namespace:Agent nodeType:Agent",
                "path:ACME/Project nodeType:Agent scope:ancestors",
            },
            resolved);
    }

    [Fact]
    public void Effective_NullNode_ReturnsDefaults()
    {
        var defaults = new AiSettings { EnabledHarnesses = ImmutableArray.Create("MeshWeaver") };
        Assert.Equal(defaults, AiSettingsNodeType.Effective(null, defaults, Json));
    }

    [Fact]
    public void Effective_EmptyFieldsFallBackToDefaults_PerField()
    {
        var defaults = new AiSettings
        {
            EnabledHarnesses = ImmutableArray.Create("MeshWeaver", "Claude Code"),
            AgentQueries = ImmutableArray.Create("namespace:Agent nodeType:Agent"),
            ModelQueries = ImmutableArray.Create("namespace:Provider nodeType:LanguageModel|ModelProvider scope:descendants"),
        };
        // Saved node sets only the harnesses — the query lists are empty.
        var node = new MeshNode(AiSettingsNodeType.NodeId, "rbuergi/_Memex")
        {
            NodeType = AiSettingsNodeType.NodeType,
            Content = new AiSettings { EnabledHarnesses = ImmutableArray.Create("MeshWeaver") },
        };

        var eff = AiSettingsNodeType.Effective(node, defaults, Json);

        Assert.Equal(new[] { "MeshWeaver" }, eff.EnabledHarnesses);   // saved value kept
        Assert.Equal(defaults.AgentQueries, eff.AgentQueries);        // empty ⇒ default
        Assert.Equal(defaults.ModelQueries, eff.ModelQueries);        // empty ⇒ default
    }

    [Fact]
    public void BuildDefaults_EnabledHarnesses_AreTheRegisteredHarnessIds_OrderedByDefinitionOrder()
    {
        var services = new ServiceCollection()
            .AddSingleton<IHarness>(new FakeHarness("Claude Code", order: 1))
            .AddSingleton<IHarness>(new FakeHarness("MeshWeaver", order: 0))
            .BuildServiceProvider();

        var defaults = AiSettingsNodeType.BuildDefaults(services);

        Assert.Equal(new[] { "MeshWeaver", "Claude Code" }, defaults.EnabledHarnesses); // ordered by Definition.Order
        Assert.NotEmpty(defaults.AgentQueries);
        Assert.NotEmpty(defaults.ModelQueries);
        // Templates carry the substitution tokens (resolved per composer instance).
        Assert.Contains(defaults.AgentQueries, q => q.Contains("{currentPath}"));
        Assert.Contains(defaults.ModelQueries, q => q.Contains("{userPath}"));
    }

    private sealed record FakeHarness(string Id, int order) : IHarness
    {
        public Harness Definition => new() { Id = Id, Order = order };
        public IChatClient? CreateChatClient(HarnessExecutionContext context) => null;
    }
}
