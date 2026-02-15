namespace MeshWeaver.Layout;

/// <summary>
/// A control that renders markdown content with collaborative annotation support
/// (track changes, comments, view mode switching).
/// Used in the read-only overview of markdown nodes.
/// </summary>
public record CollaborativeMarkdownControl()
    : UiControl<CollaborativeMarkdownControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The annotated markdown content (with annotation markers).
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// The node path for comment creation/resolution.
    /// </summary>
    public string? NodePath { get; init; }

    /// <summary>
    /// The hub address for DataChangeRequest (accept/reject changes).
    /// </summary>
    public string? HubAddress { get; init; }

    public CollaborativeMarkdownControl WithValue(string value) => this with { Value = value };
    public CollaborativeMarkdownControl WithNodePath(string nodePath) => this with { NodePath = nodePath };
    public CollaborativeMarkdownControl WithHubAddress(string hubAddress) => this with { HubAddress = hubAddress };
}
