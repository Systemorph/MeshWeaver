#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Blazor.Portal.Chat;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Acme.Test;

/// <summary>
/// Pins the grouping of the chat model picker ("Choose a model") — the arrangement
/// <c>ThreadChatView.RemoveEmptyPickerGroups(ArrangePickerGroups(...))</c> produces.
///
/// 🚨 Regression: a model id may itself contain '/' — OpenRouter ids are <c>vendor/model</c>
/// (e.g. <c>z-ai/glm-5.2</c>). <see cref="MeshNode.Path"/> is DERIVED as <c>{Namespace}/{Id}</c>,
/// so the old group key <c>ParentPath(node.Path)</c> trimmed only the LAST segment and yielded
/// <c>Provider/OpenRouter/z-ai</c> instead of the provider path <c>Provider/OpenRouter</c>.
/// Two things then went wrong at once on memex.meshweaver.cloud: the model never grouped under
/// its provider, and — because no model's key matched the provider's path —
/// <c>RemoveEmptyPickerGroups</c> deleted the OPENROUTER title outright, so those models rendered
/// as untitled rows appended to whichever group happened to sort before them (AZUREFOUNDRY).
/// The key is the node's <see cref="MeshNode.Namespace"/>, which is correct by construction.
/// </summary>
public class ModelPickerGroupingTest
{
    private static MeshNode Provider(string id, string name) =>
        new(id, ModelProviderNodeType.RootNamespace)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = name,
        };

    private static MeshNode Model(string id, string providerPath, string name, int order = 0) =>
        new(id, providerPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = name,
            Order = order,
        };

    private static List<MeshNode> Arrange(params MeshNode[] nodes) =>
        ThreadChatView.RemoveEmptyPickerGroups(
            ThreadChatView.ArrangePickerGroups(nodes.ToList()));

    /// <summary>The header a given model renders under: the nearest provider row above it.</summary>
    private static string? HeaderAbove(List<MeshNode> arranged, string modelPath)
    {
        var i = arranged.FindIndex(n => n.Path == modelPath);
        Assert.True(i >= 0, $"model {modelPath} missing from the arranged picker list");
        for (var j = i - 1; j >= 0; j--)
            if (arranged[j].NodeType == ModelProviderNodeType.NodeType)
                return arranged[j].Name;
        return null;
    }

    [Fact]
    public void ModelIdContainingSlash_KeepsItsProviderHeader()
    {
        // Exactly the prod shape: OpenRouter ids are vendor/model, so the node path gains a segment.
        var arranged = Arrange(
            Provider("OpenRouter", "OpenRouter"),
            Model("z-ai/glm-5.2", "Provider/OpenRouter", "GLM 5.2"));

        Assert.Equal("Provider/OpenRouter/z-ai/glm-5.2", arranged.Last().Path);   // path really is 4 segments
        Assert.Equal("OpenRouter", HeaderAbove(arranged, "Provider/OpenRouter/z-ai/glm-5.2"));
    }

    [Fact]
    public void ProviderHeader_SurvivesWhenAllItsModelIdsContainSlash()
    {
        // RemoveEmptyPickerGroups must not treat the provider as empty.
        var arranged = Arrange(
            Provider("OpenRouter", "OpenRouter"),
            Model("moonshotai/kimi-k3", "Provider/OpenRouter", "Kimi K3"),
            Model("z-ai/glm-5.2", "Provider/OpenRouter", "GLM 5.2"));

        Assert.Contains(arranged, n => n.NodeType == ModelProviderNodeType.NodeType
                                       && n.Path == "Provider/OpenRouter");
    }

    [Fact]
    public void SlashIdModels_AreNotAbsorbedIntoThePrecedingProviderGroup()
    {
        // The reported symptom: Kimi K3 / GLM 5.2 appeared under the AZUREFOUNDRY header.
        // "AzureFoundry" sorts before "OpenRouter", so a broken key puts them in that block.
        var arranged = Arrange(
            Provider("AzureFoundry", "Azure Foundry"),
            Model("DeepSeek-V4-Flash", "Provider/AzureFoundry", "DeepSeek-V4-Flash"),
            Provider("OpenRouter", "OpenRouter"),
            Model("moonshotai/kimi-k3", "Provider/OpenRouter", "Kimi K3"),
            Model("z-ai/glm-5.2", "Provider/OpenRouter", "GLM 5.2"));

        Assert.Equal("Azure Foundry", HeaderAbove(arranged, "Provider/AzureFoundry/DeepSeek-V4-Flash"));
        Assert.Equal("OpenRouter", HeaderAbove(arranged, "Provider/OpenRouter/moonshotai/kimi-k3"));
        Assert.Equal("OpenRouter", HeaderAbove(arranged, "Provider/OpenRouter/z-ai/glm-5.2"));
    }

    [Fact]
    public void PlainModelIds_StillGroupUnderTheirProvider()
    {
        // Guard the untouched path — ids without '/' must behave exactly as before.
        var arranged = Arrange(
            Provider("Anthropic", "Anthropic"),
            Model("claude-sonnet-5", "Provider/Anthropic", "claude-sonnet-5"));

        Assert.Equal("Anthropic", HeaderAbove(arranged, "Provider/Anthropic/claude-sonnet-5"));
    }

    [Fact]
    public void ProviderWithNoModels_IsStillDroppedAsAnEmptyHeader()
    {
        // The existing behaviour RemoveEmptyPickerGroups exists for — must not regress.
        var arranged = Arrange(
            Provider("AzureOpenAI", "Azure OpenAI"),
            Provider("OpenRouter", "OpenRouter"),
            Model("z-ai/glm-5.2", "Provider/OpenRouter", "GLM 5.2"));

        Assert.DoesNotContain(arranged, n => n.Path == "Provider/AzureOpenAI");
    }
}
