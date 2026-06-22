#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Contract for the admin-managed platform catalog seeder + the /model selection path:
/// <list type="number">
///   <item><b>Always-seed catalog.</b> Each catalog source ALWAYS emits a <c>ModelProvider</c>
///   node (create-if-absent, <c>ExcludeThisAndChildren</c>) plus a key-less, public
///   <c>LanguageModel</c> child per model id — regardless of whether an Endpoint/ApiKey is wired
///   in config. Keys/endpoints are set later as mesh data; the picker shows the catalog and the
///   admin manages credentials. (This drops the older "hide unconfigured models" gate.)</item>
///   <item><b>/model selection.</b> A model selection must persist the model node's PATH onto
///   the composer's ModelName (so the MeshNode picker resolves it), not the bare model id.
///   <see cref="AgentPickerProjection.ToModelInfo"/> must carry <c>node.Path</c>.</item>
/// </list>
/// Pure POCO units — no mesh — because the source of truth is
/// <see cref="BuiltInLanguageModelProvider"/> (IConfiguration + bootstrap defaults → catalog
/// nodes) and the projection, not the distributed wiring.
/// </summary>
public class BuiltInLanguageModelProviderTest
{
    private static readonly JsonSerializerOptions Json = new();

    private static BuiltInLanguageModelProvider Build(
        IDictionary<string, string?> config,
        params LanguageModelCatalogSource[] sources)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var opts = new LanguageModelCatalogOptions();
        foreach (var s in sources)
            opts.Add(s);
        return new BuiltInLanguageModelProvider(configuration, opts);
    }

    private static IReadOnlyList<MeshNode> ModelsOf(BuiltInLanguageModelProvider p) =>
        p.GetStaticNodes()
            .Where(n => string.Equals(n.NodeType, LanguageModelNodeType.NodeType, System.StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static IReadOnlyList<MeshNode> ProvidersOf(BuiltInLanguageModelProvider p) =>
        p.GetStaticNodes()
            .Where(n => string.Equals(n.NodeType, ModelProviderNodeType.NodeType, System.StringComparison.OrdinalIgnoreCase))
            .ToList();

    [Fact]
    public void ConfiguredApiProvider_EmitsItsModels()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Azure:Models:0"] = "claude-sonnet-4",
            ["Azure:Endpoint"] = "https://x.openai.azure.com",
            ["Azure:ApiKey"] = "sk-secret",
        }, new LanguageModelCatalogSource("Azure", "Azure"));

        ModelsOf(provider).Select(n => n.Name).Should().Contain("claude-sonnet-4");
    }

    [Fact]
    public void UnconfiguredApiProvider_StillEmitsModelsAndProviderNode()
    {
        // Models listed but NO Endpoint/ApiKey. The catalog is admin-managed now: the key-less,
        // public LanguageModel children are ALWAYS emitted (keys are set later as mesh data), and
        // the ModelProvider node is always emitted create-if-absent. The old "hide unconfigured
        // models" gate is gone.
        var provider = Build(new Dictionary<string, string?>
        {
            ["Azure:Models:0"] = "claude-sonnet-4",
        }, new LanguageModelCatalogSource("Azure", "Azure"));

        ModelsOf(provider).Select(n => n.Name).Should().Contain("claude-sonnet-4",
            "the platform catalog always surfaces its models; the admin sets the key later as mesh data");

        ProvidersOf(provider).Select(n => n.Name).Should().Contain("Azure",
            "the provider node is always emitted (create-if-absent) so the admin can configure it");
    }

    [Fact]
    public void KeylessProvider_EmitsModels_WithoutCredentials()
    {
        // RequiresApiKey: false (a co-hosted/keyless provider) → configured without an endpoint/key.
        var provider = Build(new Dictionary<string, string?>
        {
            ["Local:Models:0"] = "local-model",
        }, new LanguageModelCatalogSource("Local", "Local", RequiresApiKey: false));

        ModelsOf(provider).Select(n => n.Name).Should().Contain("local-model");
    }

    [Fact]
    public void ToModelInfo_CarriesNodePath_SoSelectionPersistsTheNodeIdentity()
    {
        var node = new MeshNode("claude-sonnet-4", "Admin/Provider/Azure")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = "claude-sonnet-4",
            Content = new ModelDefinition
            {
                Id = "claude-sonnet-4",
                DisplayName = "claude-sonnet-4",
                Provider = "Azure",
            },
        };

        var info = AgentPickerProjection.ToModelInfo(node, Json);

        info.Should().NotBeNull();
        info!.Path.Should().Be(node.Path,
            "the /model selection persists the node PATH onto the composer ModelName — without it the picker can't resolve the node (the 'dialog breaks' bug)");
        info.Name.Should().Be("claude-sonnet-4");
    }
}
