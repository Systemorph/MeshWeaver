#nullable enable

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Switch the agent for subsequent messages. A <see cref="MeshNodePickCommand"/> over
/// <c>nodeType:Agent</c> nodes, writing the composer's <c>agentName</c>.
/// </summary>
public class AgentCommand : MeshNodePickCommand
{
    /// <inheritdoc />
    public override string Name => "agent";

    /// <inheritdoc />
    public override string Description => "Switch to a different agent for subsequent messages";

    /// <inheritdoc />
    protected override string Query => "namespace:Agent nodeType:Agent";

    /// <inheritdoc />
    protected override string ComposerField => "agentName";

    /// <inheritdoc />
    protected override string Title => "Choose an agent";
}
