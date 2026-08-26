#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the fix for #2331 — a per-model <c>Order</c> pin split its provider's block in the model
/// picker, rendering the same provider's header twice.
///
/// <para><b>Why it happened.</b> <see cref="AgentPickerProjection.ProjectModels"/> used to sort
/// <c>Order</c> FIRST and <c>Provider</c> second. <c>Order</c> does double duty: a global minimum
/// picks the deployment default (<c>ObserveDefaultComposer</c> re-sorts by it independently), and
/// <see cref="ModelOrdering.Defaults"/> pins a SPECIFIC model id below its provider's uniform Order
/// so it can win that global minimum without re-ordering the whole provider (e.g. an OpenRouter model
/// pinned to <c>-2</c> while its eleven siblings stay at the source's uniform <c>Order 6</c>). Sorting
/// by Order first let that pin lift the one model out of its provider's contiguous run — the dropdown
/// (which renders a header whenever the group key changes) rendered the provider a second time,
/// bracketing the rest of its models.</para>
///
/// <para><b>The fix.</b> Provider is now the PRIMARY key — mirrors <c>ProjectAgents</c>'s
/// <c>GroupName</c>-first sort — so every provider's models are contiguous BY CONSTRUCTION. The
/// per-model <c>Order</c> becomes the tie-break INSIDE the group, which is exactly what a pin is
/// for: it still promotes the pinned model to the top of its own provider's block instead of
/// tearing the block in half. The router (Auto) is pinned ahead of every concrete provider via an
/// explicit <c>IsRouter</c>-first key, preserving its documented "sorts ahead of everything" intent
/// (<c>RouterOrder = -10</c>) independently of where "Auto" would otherwise fall alphabetically.</para>
/// </summary>
public class AgentPickerModelOrderingTest
{
    private static readonly JsonSerializerOptions Json = new();

    private static MeshNode ModelNode(
        string id, string provider, int order, bool isRouter = false) =>
        new(id, $"Provider/{provider}")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = id,
            Content = new ModelDefinition
            {
                Id = id,
                Provider = provider,
                Order = order,
                IsRouter = isRouter,
            },
        };

    [Fact]
    public void APinnedModel_StaysInsideItsProvidersContiguousBlock()
    {
        // The reported shape: OpenRouter's eleven siblings share the source's uniform Order 6, but
        // one of them (glm) is pinned to -2 so it can win the deployment-wide default. A second,
        // unrelated provider (Anthropic) sits at Order 1 — squarely between the pin and the rest of
        // OpenRouter's block under the OLD (Order-first) comparer.
        var snapshot = new[]
        {
            ModelNode("z-ai/glm-5.3", "OpenRouter", order: -2),
            ModelNode("anthropic/claude-opus-5", "OpenRouter", order: 6),
            ModelNode("qwen/qwen4", "OpenRouter", order: 6),
            ModelNode("claude-sonnet-5", "Anthropic", order: 1),
        };

        var projected = AgentPickerProjection.ProjectModels(snapshot, Json);

        // One contiguous run per provider — no provider's name appears, breaks, then appears again.
        // Run count = 1 (for the first element) + the number of adjacent pairs that differ; no
        // null sentinel needed (Provider is non-nullable).
        var providers = projected.Select(m => m.Provider).ToList();
        var providerRuns = providers.Count == 0
            ? 0
            : 1 + providers.Zip(providers.Skip(1), (current, next) => current != next).Count(differs => differs);
        var distinctProviders = providers.Distinct().Count();
        providerRuns.Should().Be(distinctProviders,
            "each provider must render as ONE contiguous block, however its models are pinned");

        // The pin still does its job: glm sorts to the TOP of OpenRouter's own block.
        var openRouterBlock = projected.Where(m => m.Provider == "OpenRouter").ToList();
        openRouterBlock.First().Name.Should().Be("z-ai/glm-5.3",
            "the Order pin promotes the model within its OWN provider's block");
        openRouterBlock.Select(m => m.Name).Should().Equal(
            "z-ai/glm-5.3", "anthropic/claude-opus-5", "qwen/qwen4");
    }

    [Fact]
    public void ProvidersWithNaturallyInterleavedOrders_StillRenderOneHeaderEach()
    {
        // The shape #1880 also described: two providers whose models' Order values interleave
        // (AzureFoundry at 1/6, OpenRouter at 2/5) — nobody pinned anything, the source-level
        // Orders just happen to interleave. The OLD Order-first comparer would render
        // AzureFoundry, then OpenRouter, then AzureFoundry again.
        var snapshot = new[]
        {
            ModelNode("DeepSeek-V4-Flash", "AzureFoundry", order: 1),
            ModelNode("moonshotai/kimi-k3", "OpenRouter", order: 2),
            ModelNode("z-ai/glm-5.3", "OpenRouter", order: 5),
            ModelNode("DeepSeek-V4-Pro", "AzureFoundry", order: 6),
        };

        var projected = AgentPickerProjection.ProjectModels(snapshot, Json);

        projected.Select(m => m.Provider).Should().Equal(
            new[] { "AzureFoundry", "AzureFoundry", "OpenRouter", "OpenRouter" },
            "each provider must render as one contiguous block even when the providers' Order "
            + "ranges interleave, with no per-model pin involved");
    }

    [Fact]
    public void TheRouter_LeadsEveryConcreteProvider_RegardlessOfAlphabeticalPosition()
    {
        // "Auto" would sort between Anthropic and Azure alphabetically — but it must still lead the
        // whole list, matching its RouterOrder = -10 / composer-default intent.
        var snapshot = new[]
        {
            ModelNode("claude-sonnet-5", "Anthropic", order: 1),
            ModelNode("gpt-5", "Azure", order: 2),
            ModelNode("auto", "Auto", order: -10, isRouter: true),
        };

        var projected = AgentPickerProjection.ProjectModels(snapshot, Json);

        projected.First().IsRouter.Should().BeTrue("Auto must lead the picker regardless of its provider name's alphabetical position");
        projected.Select(m => m.Provider).Should().Equal("Auto", "Anthropic", "Azure");
    }
}
