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

public partial class UserProfile : ComponentBase
{
    [Inject]
    public required NavigationManager Navigation { get; init; }

    [Inject]
    public required ILogger<UserProfile> Logger { get; init; }

    [Inject]
    public required IAuthenticationNavigationService AuthNavigation { get; init; }

    [Inject]
    public required AccessService AccessService { get; init; }

    [Inject]
    public required PortalApplication PortalApp { get; init; }

    [CascadingParameter]
    public required Task<AuthenticationState> AuthenticationState { get; set; }

    [Parameter]
    public string ButtonSize { get; set; } = "24px";

    [Parameter]
    public string ImageSize { get; set; } = "52px";

    private string? name;
    private string? username;
    private string? initials;
    private bool isPlatformAdmin;
    private string NameClaimType { get; } = "name";
    public string UsernameClaimType { get; } = "preferred_username";

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
