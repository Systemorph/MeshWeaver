#nullable enable

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Switch the AI model for subsequent messages. A <see cref="MeshNodePickCommand"/> over the
/// configured <c>nodeType:LanguageModel</c> catalog, writing the composer's <c>modelName</c>.
/// </summary>
public class ModelCommand : MeshNodePickCommand
{
    /// <inheritdoc />
    public override string Name => "model";

    /// <inheritdoc />
    public override string Description => "Switch to a different AI model for subsequent messages";

    /// <inheritdoc />
    protected override string Query => "namespace:_Provider nodeType:LanguageModel scope:descendants";

    /// <inheritdoc />
    protected override string ComposerField => "modelName";

    /// <inheritdoc />
    protected override string Title => "Choose a model";
}
