// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Portal.Authentication;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Portal.Components;

/// <summary>
/// Portal header component showing the signed-in user's avatar/initials and a menu with
/// profile, settings, login, and logout actions. Resolves the display name and the
/// platform-admin flag from the authentication state and access context.
/// </summary>
public partial class UserProfile : ComponentBase
{
    /// <summary>Navigation manager used for login, logout, and profile/settings routing.</summary>
    [Inject]
    public required NavigationManager Navigation { get; init; }

    /// <summary>Logger for the user-profile component.</summary>
    [Inject]
    public required ILogger<UserProfile> Logger { get; init; }

    /// <summary>Service that builds the login and logout URLs (with return URLs).</summary>
    [Inject]
    public required IAuthenticationNavigationService AuthNavigation { get; init; }

    /// <summary>Access service supplying the current user's access context (name, object id).</summary>
    [Inject]
    public required AccessService AccessService { get; init; }

    /// <summary>Portal application providing the message hub used for the platform-admin check.</summary>
    [Inject]
    public required PortalApplication PortalApp { get; init; }

    /// <summary>Cascaded authentication state used to read the signed-in user's claims.</summary>
    [CascadingParameter]
    public required Task<AuthenticationState> AuthenticationState { get; set; }

    /// <summary>CSS size of the avatar button shown in the header. Defaults to <c>24px</c>.</summary>
    [Parameter]
    public string ButtonSize { get; set; } = "24px";

    /// <summary>CSS size of the avatar image inside the profile menu. Defaults to <c>52px</c>.</summary>
    [Parameter]
    public string ImageSize { get; set; } = "52px";

    private string? name;
    private string? username;
    private string? initials;
    private bool isPlatformAdmin;
    private string NameClaimType { get; } = "name";
    /// <summary>The claim type read for the user's preferred username (<c>preferred_username</c>).</summary>
    public string UsernameClaimType { get; } = "preferred_username";

    /// <summary>
    /// Resolves the display name (preferring the access-context name over the name claim),
    /// computes the initials, and determines whether the user is a platform admin.
    /// </summary>
    /// <returns>A task that completes once the profile fields have been populated.</returns>
    protected override async Task OnParametersSetAsync()
    {
        var authState = await AuthenticationState;

        var claimsIdentity = authState.User.Identity as ClaimsIdentity;

        if (claimsIdentity?.IsAuthenticated == true)
        {
            // Prefer username from AccessContext (set by OnboardingMiddleware from user node)
            var accessName = AccessService.Context?.Name;
            name = !string.IsNullOrEmpty(accessName)
                ? accessName
                : claimsIdentity.FindFirst(NameClaimType)?.Value!;

            username = name;
            initials = GetInitials(name);

            // Canonical platform-admin check: admin on the Admin partition
            // (hub.IsGlobalAdmin). Wait for the positive within a short window — the
            // synced AccessAssignment query emits an empty seed first.
            isPlatformAdmin = await PortalApp.Hub.IsGlobalAdmin()
                .Where(x => x).Take(1)
                .Timeout(TimeSpan.FromSeconds(5), Observable.Return(false))
                .FirstAsync().ToTask();
        }

    }
    /// <summary>
    /// Derives the avatar initials from a name: the first letter for a single word, or the
    /// first letters of the first and last words otherwise.
    /// </summary>
    /// <param name="name">The user's display name.</param>
    /// <returns>The uppercased initials, or an empty string when the name is null/blank.</returns>
    public static string GetInitials(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "";

        var s = name.AsSpan().Trim();

        if (s.Length == 0)
        {
            return "";
        }

        var lastSpaceIndex = s.LastIndexOf(' ');

        if (lastSpaceIndex == -1)
        {
            return s[0].ToString().ToUpperInvariant();
        }

        // The name contained two or more words. Return the initials from the first and last.
        return $"{char.ToUpperInvariant(s[0])}{char.ToUpperInvariant(s[lastSpaceIndex + 1])}";
    }

    private void Login()
    {
        var returnUrl = Navigation.Uri;
        var loginUrl = AuthNavigation.GetLoginUrl(returnUrl);
        Navigation.NavigateTo(loginUrl, forceLoad: true);
    }

    private void NavigateToUserNode()
    {
        var userId = AccessService.Context?.ObjectId;
        if (!string.IsNullOrEmpty(userId))
        {
            Navigation.NavigateTo($"/User/{userId}");
        }
    }

    private void NavigateToSettings()
    {
        var userId = AccessService.Context?.ObjectId;
        if (!string.IsNullOrEmpty(userId))
            Navigation.NavigateTo($"/User/{userId}/Settings");
        else
            Navigation.NavigateTo("/_settings");
    }

    private void Logout()
    {
        var logoutUrl = AuthNavigation.GetLogoutUrl();
        Navigation.NavigateTo(logoutUrl, forceLoad: true);
    }

}
