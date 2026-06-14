#nullable enable

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Switch the harness — the runtime that executes the thread (MeshWeaver, Claude Code, Copilot).
/// A <see cref="MeshNodePickCommand"/> over <c>nodeType:Harness</c> nodes, writing the composer's
/// <c>harness</c>.
/// </summary>
public class HarnessCommand : MeshNodePickCommand
{
    /// <inheritdoc />
    public override string Name => "harness";

    /// <inheritdoc />
    public override string Description => "Switch the harness (runtime) for subsequent messages";

    /// <inheritdoc />
    protected override string Query => "namespace:Harness nodeType:Harness";

    /// <inheritdoc />
    protected override string ComposerField => "harness";

    /// <inheritdoc />
    protected override string Title => "Choose a harness";
}
