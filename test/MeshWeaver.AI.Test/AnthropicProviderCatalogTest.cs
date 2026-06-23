#pragma warning disable CS1591

using System;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.AI.AzureFoundry;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the Anthropic provider + Claude-models catalog contract — the equivalent of the
/// skill/agent catalog tests, for the model side. <see cref="AzureFoundryExtensions.AddAnthropic"/>
/// is the ONE place that declares the built-in Claude model list (latest per category), so this
/// guards that:
/// <list type="number">
///   <item><see cref="AddAnthropic"/> registers a single "Anthropic" BYO-key catalog source with the
///   direct <c>api.anthropic.com</c> endpoint and the current Claude model ids.</item>
///   <item><see cref="BuiltInLanguageModelProvider"/> materialises that source into a
///   <c>ModelProvider</c> node + one key-less, public <c>LanguageModel</c> child per Claude id —
///   the catalog the picker shows and the admin keys later (matching the mesh nodes created at
///   <c>{user}/_Memex/Anthropic</c>).</item>
///   <item>the <see cref="AzureClaudeChatClientAgentFactory"/> routes those ids (Supports), and
///   each id has a built-in price row so cost shows.</item>
/// </list>
/// When Anthropic ships a newer snapshot, bump <see cref="AddAnthropic"/>'s DefaultModelIds +
/// <see cref="ModelPricing.Defaults"/> and update <see cref="ExpectedClaudeModels"/> here.
/// </summary>
public class AnthropicProviderCatalogTest : AITestBase
{
    public AnthropicProviderCatalogTest(ITestOutputHelper output) : base(output) { }

    // Read-only catalog assertions — one mesh for the whole class.
    protected override bool ShareMeshAcrossTests => true;

    private static readonly string[] ExpectedClaudeModels =
        ["claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"];

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAnthropic();

    private LanguageModelCatalogSource AnthropicSource() =>
        Mesh.ServiceProvider.GetRequiredService<LanguageModelCatalogOptions>().Sources
            .Single(s => string.Equals(s.ProviderName, "Anthropic", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void AddAnthropic_RegistersOneByoKeySource_WithDirectEndpoint_AndCurrentClaudeModels()
    {
        var src = AnthropicSource();

        src.Kind.Should().Be(ProviderKind.Api, "Anthropic is a bring-your-own-key provider, not a CLI harness");
        src.RequiresApiKey.Should().BeTrue();
        src.DefaultEndpoint.Should().Be("https://api.anthropic.com/v1/messages",
            "the same AzureClaudeChatClient serves direct Anthropic; the endpoint is the Messages API");
        src.EffectiveLabel.Should().Be("Anthropic");
        // The canonical built-in Claude list (latest per category — opus / sonnet / haiku).
        src.EffectiveModelIds.Should().Equal(ExpectedClaudeModels);
    }

    [Fact]
    public void BuiltInCatalog_EmitsAnthropicProvider_AndOneKeylessLanguageModelPerClaudeId()
    {
        // Drive the REAL Anthropic source (from AddAnthropic) through the seeder with an EMPTY
        // config so no `Anthropic:Models` override applies → the code defaults are what we assert.
        var opts = new LanguageModelCatalogOptions();
        opts.Add(AnthropicSource());
        var nodes = new BuiltInLanguageModelProvider(new ConfigurationBuilder().Build(), opts)
            .GetStaticNodes().ToList();

        // Exactly one ModelProvider node for Anthropic, key-less (the admin sets the key later).
        var providerNodes = nodes
            .Where(n => n.NodeType == ModelProviderNodeType.NodeType && n.Name == "Anthropic")
            .ToList();
        providerNodes.Should().ContainSingle();
        var cfg = (ModelProviderConfiguration)providerNodes[0].Content!;
        cfg.Provider.Should().Be("Anthropic");
        cfg.Endpoint.Should().Be("https://api.anthropic.com/v1/messages");
        cfg.ApiKey.Should().BeNullOrEmpty("the catalog node is key-less — credentials are set later as mesh data");

        // One LanguageModel child per Claude id, all pointing at the Anthropic provider, key-less.
        var models = nodes
            .Where(n => n.NodeType == LanguageModelNodeType.NodeType
                        && (n.Content as ModelDefinition)?.Provider == "Anthropic")
            .Select(n => (ModelDefinition)n.Content!)
            .ToList();
        models.Select(m => m.Id).OrderBy(x => x)
            .Should().Equal(ExpectedClaudeModels.OrderBy(x => x));
        models.Should().OnlyContain(m => string.IsNullOrEmpty(m.ApiKeySecretRef));
    }

    [Fact]
    public void ClaudeFactory_RoutesClaudeIds_AndIgnoresOthers()
    {
        var factory = Mesh.ServiceProvider.GetServices<IChatClientFactory>()
            .OfType<AzureClaudeChatClientAgentFactory>().Single();

        foreach (var id in ExpectedClaudeModels)
            factory.Supports(id).Should().BeTrue($"the Claude factory must route '{id}'");
        factory.Supports("gpt-4o").Should().BeFalse("non-Claude ids route to other factories");
    }

    [Fact]
    public void EveryCurrentClaudeModel_HasABuiltInPriceRow()
    {
        foreach (var id in ExpectedClaudeModels)
        {
            var rate = ModelPricing.Default(id);
            rate.Should().NotBeNull($"a price row is required for '{id}' so cost shows in the UI");
            rate!.Currency.Should().Be("USD");
            rate.InputPerMillion.Should().BeGreaterThan(0m);
            rate.OutputPerMillion.Should().BeGreaterThan(0m);
        }
    }
}
