// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace MeshWeaverApp1.Portal.Components;

public partial class UserProfile : ComponentBase
{
    [Inject] 
    public required NavigationManager Navigation { get; init; }

    [Inject]
    public required ILogger<UserProfile> Logger { get; init; }

    [CascadingParameter]
    public required Task<AuthenticationState> AuthenticationState { get; set; }

    [Parameter]
    public string ButtonSize { get; set; } = "24px";

    [Parameter]
    public string ImageSize { get; set; } = "52px";

    private string name = "";
    private string username = "";
    private string initials = "";
    private string NameClaimType { get; } = "name";
    public string UsernameClaimType { get; } = "preferred_username";

    protected override async Task OnParametersSetAsync()
    {
            var authState = await AuthenticationState;

            var claimsIdentity = authState.User.Identity as ClaimsIdentity;

            if (claimsIdentity?.IsAuthenticated == true)
            {
                name = claimsIdentity.FindFirst(NameClaimType)?.Value ?? "";
                username = claimsIdentity.FindFirst(UsernameClaimType)?.Value ?? "";
                initials = GetInitials(name);
            }
            else
            {
                // If we don't have an authenticated user, don't show the user profile menu. This shouldn't happen.
                name = "";
                username = "";
                initials = "";
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
        // For Blazor Server, we directly use the ASP.NET Core Identity endpoints
        var loginPath = "/MicrosoftIdentity/Account/SignIn";

        // Add a redirect URL to return to the current page
        var returnUrl = Navigation.Uri;
        Navigation.NavigateTo($"{loginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
    }

    private void Logout()
    {
        var logoutPath = "/MicrosoftIdentity/Account/SignOut";
        Navigation.NavigateTo(logoutPath, forceLoad: true);
    }

}
