using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for <see cref="BuiltInLanguageModelProvider"/> emitting both
/// <c>LanguageModel</c> and <c>ModelProvider</c> static nodes from
/// IConfiguration sections, with the right key-protection invariants.
/// </summary>
public class ModelProviderEmissionTest
{
    /// <summary>
    /// Config with ApiKey + Endpoint + Models emits a single ModelProvider node
    /// carrying the credentials (not WithPublicRead).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void Emits_OneModelProviderPerCatalogSource_WithFullCredentials()
    {
        // Config â†’ ModelProvider node: ApiKey, Endpoint, Models all flow in.
        // ModelProvider is NOT WithPublicRead, so the key is only visible to
        // callers with Permission.Api on the root namespace (system / admin).
        // Public LanguageModel siblings still carry no key (asserted below).
        var nodes = MakeProvider(
            new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-opus-4-7",
                ["Anthropic:ApiKey"] = "sk-system-secret",
                ["Anthropic:Endpoint"] = "https://api.anthropic.com/v1/messages"
            },
            new LanguageModelCatalogSource("Anthropic", "Anthropic", 1))
            .GetStaticNodes()
            .ToList();

        var providerNode = nodes.Should().ContainSingle(n => n.NodeType == ModelProviderNodeType.NodeType).Subject;
        providerNode.Namespace.Should().Be(ModelProviderNodeType.RootNamespace);
        providerNode.Id.Should().Be("Anthropic");

        var cfg = providerNode.Content.Should().BeOfType<ModelProviderConfiguration>().Subject;
        cfg.Provider.Should().Be("Anthropic");
        cfg.Endpoint.Should().Be("https://api.anthropic.com/v1/messages");
        cfg.ApiKey.Should().Be("sk-system-secret",
            "config-supplied credentials live on the (non-public-read) ModelProvider node");
    }

    /// <summary>
    /// Per-model LanguageModel nodes are publicly readable: they reference the
    /// provider but carry no ApiKey/secret of their own.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void LanguageModelChildren_HaveProviderRefAndNoSecret()
    {
        var nodes = MakeProvider(
            new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-opus-4-7",
                ["Anthropic:Models:1"] = "claude-sonnet-4-6",
                ["Anthropic:ApiKey"] = "sk-system-secret",
                ["Anthropic:Endpoint"] = "https://api.anthropic.com/v1/messages"
            },
            new LanguageModelCatalogSource("Anthropic", "Anthropic", 1))
            .GetStaticNodes()
            .ToList();

        var lmNodes = nodes.Where(n => n.NodeType == LanguageModelNodeType.NodeType).ToList();
        lmNodes.Should().HaveCount(2);

        lmNodes.Should().AllSatisfy(node =>
        {
            var def = node.Content.Should().BeOfType<ModelDefinition>().Subject;
            def.Provider.Should().Be("Anthropic");
            def.ProviderRef.Should().Be("_Provider/Anthropic");
            def.ApiKeySecretRef.Should().BeNull("LanguageModel nodes are publicly readable â€” no secrets here");
        });
    }

    /// <summary>
    /// Empty config section (no ApiKey, Endpoint, or Models) emits neither
    /// ModelProvider nor LanguageModel nodes.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void NoSignal_NoProviderNodeEmitted()
    {
        // Empty config section + no api key + no endpoint = nothing to emit.
        var nodes = MakeProvider(
            new Dictionary<string, string?>(),
            new LanguageModelCatalogSource("Anthropic", "Anthropic", 1))
            .GetStaticNodes()
            .ToList();

        nodes.Should().NotContain(n => n.NodeType == ModelProviderNodeType.NodeType);
        nodes.Should().NotContain(n => n.NodeType == LanguageModelNodeType.NodeType);
    }

    /// <summary>
    /// Partial config (Endpoint only, no Models list) still emits a ModelProvider
    /// so user extensions can attach LanguageModel children to it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void ProviderNodeEmitted_EvenIfModelsListEmpty_WhenEndpointOrKeyPresent()
    {
        // A partially-configured section (endpoint only) still emits a
        // ModelProvider so user extensions can attach to it.
        var nodes = MakeProvider(
            new Dictionary<string, string?>
            {
                ["Anthropic:Endpoint"] = "https://api.anthropic.com/v1/messages"
            },
            new LanguageModelCatalogSource("Anthropic", "Anthropic", 1))
            .GetStaticNodes()
            .ToList();

        nodes.Should().ContainSingle(n => n.NodeType == ModelProviderNodeType.NodeType);
        nodes.Should().NotContain(n => n.NodeType == LanguageModelNodeType.NodeType);
    }

    private static BuiltInLanguageModelProvider MakeProvider(
        IDictionary<string, string?> configValues,
        params LanguageModelCatalogSource[] sources)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues!)
            .Build();
        var options = new LanguageModelCatalogOptions();
        foreach (var s in sources) options.Add(s);
        return new BuiltInLanguageModelProvider(config, options);
    }
}
