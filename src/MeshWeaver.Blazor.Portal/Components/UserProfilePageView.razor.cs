using MeshWeaver.Blazor.Components;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Portal.Components;

/// <summary>
/// Blazor view that renders the user-profile page for a <c>UserProfileControl</c>, showing
/// the user's details and avatar initials derived from the view model's display name.
/// </summary>
public partial class UserProfilePageView : BlazorView<UserProfileControl, UserProfilePageView>
{
    private string GetInitials()
    {
        var name = ViewModel?.DisplayName;
        if (string.IsNullOrEmpty(name))
            return "?";

        var s = name.AsSpan().Trim();
        if (s.Length == 0)
            return "?";

        var lastSpaceIndex = s.LastIndexOf(' ');
        if (lastSpaceIndex == -1)
            return s[0].ToString().ToUpperInvariant();

        return $"{char.ToUpperInvariant(s[0])}{char.ToUpperInvariant(s[lastSpaceIndex + 1])}";
    }
}
