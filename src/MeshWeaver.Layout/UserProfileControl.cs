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

    /// <summary>Returns a copy with <paramref name="path"/> as the user node path.</summary>
    /// <param name="path">The mesh path of the user node to display.</param>
    /// <returns>A new <see cref="UserProfileControl"/> with the updated node path.</returns>
    public UserProfileControl WithNodePath(string path) => this with { NodePath = path };
    /// <summary>Returns a copy with <paramref name="name"/> as the displayed user name.</summary>
    /// <param name="name">The display name to show.</param>
    /// <returns>A new <see cref="UserProfileControl"/> with the updated display name.</returns>
    public UserProfileControl WithDisplayName(string name) => this with { DisplayName = name };
    /// <summary>Returns a copy with <paramref name="icon"/> as the user's avatar URL.</summary>
    /// <param name="icon">The avatar URL, or null to show a default avatar.</param>
    /// <returns>A new <see cref="UserProfileControl"/> with the updated icon.</returns>
    public UserProfileControl WithIcon(string? icon) => this with { Icon = icon };
    /// <summary>Returns a copy with <paramref name="email"/> as the user's email address.</summary>
    /// <param name="email">The email address to display, or null to omit.</param>
    /// <returns>A new <see cref="UserProfileControl"/> with the updated email.</returns>
    public UserProfileControl WithEmail(string? email) => this with { Email = email };
    /// <summary>Returns a copy with <paramref name="bio"/> as the user's biography text.</summary>
    /// <param name="bio">The bio text to display, or null to omit.</param>
    /// <returns>A new <see cref="UserProfileControl"/> with the updated bio.</returns>
    public UserProfileControl WithBio(string? bio) => this with { Bio = bio };
}
