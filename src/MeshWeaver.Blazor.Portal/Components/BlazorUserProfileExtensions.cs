using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Portal.Components;

public static class BlazorUserProfileExtensions
{
    /// <summary>
    /// Registers the UserProfilePageView Blazor renderer for UserProfileControl.
    /// </summary>
    public static MessageHubConfiguration AddUserProfileViews(this MessageHubConfiguration configuration)
        => configuration
            .AddViews(registry => registry
                .WithView<UserProfileControl, UserProfilePageView>());
}
