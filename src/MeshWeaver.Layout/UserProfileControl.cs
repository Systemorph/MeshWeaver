namespace MeshWeaver.Layout;

/// <summary>
/// Layout control for rendering a user's public profile page.
/// Shown when a visitor navigates to another user's node.
/// The Blazor renderer fetches user data and displays bio, activity, and child nodes.
/// </summary>
public record UserProfileControl() : UiControl<UserProfileControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The path of the user node being viewed (e.g., "User/Alice").
    /// </summary>
    public string NodePath { get; init; } = "";

    /// <summary>
    /// The display name of the user (from MeshNode.Name).
    /// </summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// The user's icon/avatar URL (from MeshNode.Icon).
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// The user's email address (from User content).
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// The user's bio text (from User content).
    /// </summary>
    public string? Bio { get; init; }

    public UserProfileControl WithNodePath(string path) => this with { NodePath = path };
    public UserProfileControl WithDisplayName(string name) => this with { DisplayName = name };
    public UserProfileControl WithIcon(string? icon) => this with { Icon = icon };
    public UserProfileControl WithEmail(string? email) => this with { Email = email };
    public UserProfileControl WithBio(string? bio) => this with { Bio = bio };
}
