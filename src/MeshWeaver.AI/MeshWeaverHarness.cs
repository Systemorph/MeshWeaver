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
    public string Id => Harnesses.MeshWeaver;

    public Harness Definition => new()
    {
        Id = Harnesses.MeshWeaver,
        DisplayName = "MeshWeaver",
        Description = "MeshWeaver agents with a selectable model.",
        Icon = "/static/NodeTypeIcons/bot.svg",
        Order = 0,
        IsDefault = true,
        SupportsAgentSelection = true
    };

    public IChatClient? CreateChatClient(HarnessExecutionContext context) => null;
}
