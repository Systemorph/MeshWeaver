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
/// Repro + contract for the two model-picker bugs (2026-06-14):
/// <list type="number">
///   <item><b>Bug 1 — unconfigured models show.</b> A config section that lists
///   <c>Models[]</c> but has NO <c>Endpoint</c>/<c>ApiKey</c> is an un-wired default catalog
///   (e.g. an "Azure" section listing Claude ids with no credentials). Its models must NOT
///   enter the picker catalog — they're selectable-but-unusable. Only the ModelProvider node
///   (for Settings → Models) is kept.</item>
///   <item><b>Bug 2 — /model selection breaks.</b> A model selection must persist the model
///   node's PATH onto the composer's ModelName (so the MeshNode picker resolves it), not the
///   bare model id. <see cref="AgentPickerProjection.ToModelInfo"/> must carry <c>node.Path</c>.</item>
/// </list>
/// Both are pure POCO units — no mesh — because the source of truth is
/// <see cref="BuiltInLanguageModelProvider"/> (IConfiguration → catalog nodes) and the
/// projection, not the distributed wiring.
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
    public void UnconfiguredApiProvider_HidesModels_ButKeepsProviderNodeForSettings()
    {
        // Models listed (an un-wired default catalog) but NO Endpoint/ApiKey → not configured.
        var provider = Build(new Dictionary<string, string?>
        {
            ["Azure:Models:0"] = "claude-sonnet-4",
        }, new LanguageModelCatalogSource("Azure", "Azure"));

        ModelsOf(provider).Should().BeEmpty(
            "an Api provider with no Endpoint/ApiKey is not configured — its models must not show in the /model picker (the 'Azure Claude with no config' bug)");

        // The ModelProvider node IS still emitted so Settings → Models can render the configure form.
        ProvidersOf(provider).Select(n => n.Name).Should().Contain("Azure",
            "the provider node must remain so the user can paste a key to configure it");
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
        var node = new MeshNode("claude-sonnet-4", "_Provider/Azure")
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
