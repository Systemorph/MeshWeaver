#nullable enable
namespace MeshWeaver.Data;

/// <summary>
/// Identifying information about a user associated with an activity or change.
/// </summary>
/// <param name="Email">The user's email address.</param>
/// <param name="DisplayName">The user's display name.</param>
/// <param name="Photo">Optional URL or reference to the user's profile photo.</param>
public record UserInfo(string Email, string DisplayName, string? Photo = default);