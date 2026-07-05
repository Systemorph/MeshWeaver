using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// The native <b>MeshWeaver</b> harness — runs the agent + model system (the
/// provider factories drive the round). <see cref="CreateChatClient"/> returns
/// <c>null</c> so <see cref="ThreadExecution"/> uses the default
/// <see cref="AgentChatClient"/> path. This is the one harness that surfaces agent
/// and model selection.
/// </summary>
public sealed class MeshWeaverHarness : IHarness
{
    /// <summary>Stable harness id (<c>Harnesses.MeshWeaver</c>).</summary>
    public string Id => Harnesses.MeshWeaver;

    /// <summary>The catalog definition surfaced as a node and in the harness picker.</summary>
    public Harness Definition => new()
    {
        Id = Harnesses.MeshWeaver,
        DisplayName = "MeshWeaver",
        Description = "MeshWeaver agents with a selectable model.",
        Icon = "/static/NodeTypeIcons/meshweaver-logo.svg",
        // -1 = the "make this the default" convention (AgentPickerProjection.ObserveDefaultComposer
        // picks the LOWEST-Order harness). MeshWeaver must lead the picker AND be the catalog default,
        // ahead of ClaudeCode (1) and Copilot (2).
        Order = -1,
        IsDefault = true,
        SupportsAgentSelection = true
    };

    /// <summary>
    /// Returns <c>null</c> so execution falls through to the default MeshWeaver
    /// agent/model path (this harness has no library-specific client).
    /// </summary>
    /// <param name="context">The per-round execution inputs (unused by this harness).</param>
    /// <returns>Always <c>null</c>.</returns>
    public IChatClient? CreateChatClient(HarnessExecutionContext context) => null;
}
