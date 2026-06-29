using System.Collections.Generic;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
    [Fact]
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
    /// 🚨 Regression (atioz 2026-06-23, non-admin chat crash): the catalog source must seed a
    /// PublicRead <c>PartitionAccessPolicy</c> for BOTH catalog partitions — <c>Provider</c>
    /// (ModelProvider) AND <c>Model</c> (LanguageModel). Chat / the model picker / model
    /// resolution read the <c>Model</c> partition UNDER THE USER'S IDENTITY
    /// (<c>GetDataRequest hub=model</c>); without a PublicRead policy there a non-admin is denied
    /// "lacks Read permission on 'model'", which returns as a DeliveryFailureException and crashes
    /// the chat round. The refactor dropped the Model policy, leaving only Provider's.
    /// </summary>
    [Fact]
    public void Emits_PublicReadPolicy_ForBoth_Provider_And_Model_Partitions()
    {
        var nodes = MakeProvider(
            new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-opus-4-8",
                ["Anthropic:Endpoint"] = "https://api.anthropic.com/v1/messages"
            },
            new LanguageModelCatalogSource("Anthropic", "Anthropic", 1))
            .GetStaticNodes()
            .ToList();

        // Provider partition policy (pre-existing).
        var providerPolicy = nodes.Should().ContainSingle(n =>
            n.NodeType == "PartitionAccessPolicy" && n.Namespace == ModelProviderNodeType.RootNamespace).Subject;
        providerPolicy.Content.Should().BeOfType<PartitionAccessPolicy>()
            .Which.PublicRead.Should().BeTrue();

        // Model partition policy (the chat-crash fix) — emitted whenever Model is a distinct partition.
        if (!string.Equals(LanguageModelNodeType.RootNamespace, ModelProviderNodeType.RootNamespace, System.StringComparison.Ordinal))
        {
            var modelPolicy = nodes.Should().ContainSingle(n =>
                n.NodeType == "PartitionAccessPolicy" && n.Namespace == LanguageModelNodeType.RootNamespace).Subject;
            modelPolicy.Content.Should().BeOfType<PartitionAccessPolicy>()
                .Which.PublicRead.Should().BeTrue(
                "chat reads the Model/LanguageModel partition under the user's identity; without PublicRead a non-admin is denied 'lacks Read on model' and the round crashes");
        }
    }

    /// <summary>
    /// Per-model LanguageModel nodes are publicly readable: they reference the
    /// provider but carry no ApiKey/secret of their own.
    /// </summary>
    [Fact]
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
            def.ProviderRef.Should().Be("Provider/Anthropic");
            def.ApiKeySecretRef.Should().BeNull("LanguageModel nodes are publicly readable â€” no secrets here");
        });
    }

    /// <summary>
    /// Empty config section + a source with no bootstrap defaults STILL emits the
    /// ModelProvider node (create-if-absent seed) but no LanguageModel children
    /// (there are no model ids to seed).
    /// </summary>
    [Fact]
    public void NoSignal_StillEmitsProviderNode_ButNoModels()
    {
        // Empty config + no DefaultModelIds → a bare provider node, no model children.
        // The provider node is always emitted so the admin can configure it (create-if-absent).
        var nodes = MakeProvider(
            new Dictionary<string, string?>(),
            new LanguageModelCatalogSource("Anthropic", "Anthropic", 1))
            .GetStaticNodes()
            .ToList();

        nodes.Should().ContainSingle(n => n.NodeType == ModelProviderNodeType.NodeType);
        nodes.Should().NotContain(n => n.NodeType == LanguageModelNodeType.NodeType);
    }

    /// <summary>
    /// Partial config (Endpoint only, no Models list) still emits a ModelProvider
    /// so user extensions can attach LanguageModel children to it.
    /// </summary>
    [Fact]
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
