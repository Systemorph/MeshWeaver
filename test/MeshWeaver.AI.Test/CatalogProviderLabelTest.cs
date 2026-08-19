#pragma warning disable CS1591

using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins that the seeded <c>ModelProvider</c> node publishes the catalog source's HUMAN-READABLE
/// label, not its wire name.
///
/// 🚨 Regression: the chat model picker renders the provider node's <see cref="MeshNode.Name"/> as
/// its group title (upper-cased in CSS). <see cref="BuiltInLanguageModelProvider"/> stamped
/// <c>source.ProviderName</c> there, so the Azure Foundry source — which declares
/// <c>DisplayLabel: "Azure Foundry"</c> — surfaced to users as the jammed-together "AZUREFOUNDRY"
/// on memex.meshweaver.cloud. <see cref="LanguageModelCatalogSource.EffectiveLabel"/> already
/// existed for exactly this and was simply never read on the node-emitting path.
///
/// The node ID must stay the wire name: it is the path every
/// <c>ModelDefinition.ProviderRef</c> points at.
/// </summary>
public class CatalogProviderLabelTest
{
    private static MeshNode SeedProviderNode(LanguageModelCatalogSource source)
    {
        var options = new LanguageModelCatalogOptions();
        options.Add(source);
        var configuration = new ConfigurationBuilder().Build();

        return new BuiltInLanguageModelProvider(configuration, options)
            .GetStaticNodes()
            .Single(n => n.NodeType == ModelProviderNodeType.NodeType
                         && n.Id == source.ProviderName);
    }

    [Fact]
    public void SeededProviderNode_UsesTheDeclaredDisplayLabel()
    {
        var node = SeedProviderNode(new LanguageModelCatalogSource(
            SectionName: "AzureFoundry", ProviderName: "AzureFoundry", Order: 2,
            DisplayLabel: "Azure Foundry", DefaultEndpoint: null,
            DefaultModelIds: ImmutableArray<string>.Empty, RequiresApiKey: true));

        Assert.Equal("Azure Foundry", node.Name);
        Assert.Equal("Azure Foundry", Assert.IsType<ModelProviderConfiguration>(node.Content).Label);
    }

    [Fact]
    public void SeededProviderNode_KeepsTheWireNameAsItsPath()
    {
        // ProviderRef points at Provider/{ProviderName} — renaming the path would orphan every model.
        var node = SeedProviderNode(new LanguageModelCatalogSource(
            SectionName: "AzureFoundry", ProviderName: "AzureFoundry", Order: 2,
            DisplayLabel: "Azure Foundry"));

        Assert.Equal("AzureFoundry", node.Id);
        Assert.Equal("Provider/AzureFoundry", node.Path);
        Assert.Equal("AzureFoundry", Assert.IsType<ModelProviderConfiguration>(node.Content).Provider);
    }

    [Fact]
    public void SeededProviderNode_FallsBackToProviderNameWhenNoLabelDeclared()
    {
        var node = SeedProviderNode(new LanguageModelCatalogSource(
            SectionName: "Custom", ProviderName: "Custom"));

        Assert.Equal("Custom", node.Name);
    }
}
