// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Blazor.Portal.Authentication;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Portal.Components;

/// <summary>
/// Portal header component showing the signed-in user's avatar/initials and a menu with
/// profile, settings, login, and logout actions. Resolves the display name and the
/// platform-admin flag from the authentication state and access context.
/// </summary>
public partial class UserProfile : ComponentBase, IDisposable
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

    /// <summary>Application configuration — read for the frontend-selection feature (Portal:ReactAppUrl).</summary>
    [Inject]
    public required IConfiguration Configuration { get; init; }

    /// <summary>Portal application providing the message hub used for the platform-admin check.</summary>
    [Inject]
    public required PortalApplication PortalApp { get; init; }

    /// <summary>
    /// The MESH hub — deliberately not <see cref="PortalApp"/>'s per-circuit portal hub — used to
    /// read and write the viewer's own profile for the presentation-mode toggle (#1803).
    /// </summary>
    [Inject]
    public required IMessageHub MeshHub { get; init; }

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

    // ----- Presentation mode (#1803) -----
    // The quick toggle and its header indicator. The screen is read LIVE off the viewer's own
    // profile so the header lights up the instant the mode is flipped — including from another tab
    // — and so the state shown here can never disagree with what the tile surfaces are doing.
    private PresentationScreen presentationScreen = PresentationScreen.Off;
    private IDisposable? presentationSubscription;
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

            SubscribePresentationScreen();
        }

    }

    /// <summary>
    /// Subscribes the viewer's live presentation screen so the menu entry and the header indicator
    /// track the real state. Idempotent — <c>OnParametersSetAsync</c> can run more than once.
    /// </summary>
    private void SubscribePresentationScreen()
    {
        if (presentationSubscription is not null)
            return;
        presentationSubscription = AccessService.ViewerScreen(MeshHub)
            .Subscribe(
                screen => InvokeAsync(() =>
                {
                    if (presentationScreen == screen)
                        return;
                    presentationScreen = screen;
                    StateHasChanged();
                }),
                ex => Logger.LogWarning(ex, "Presentation screen stream ended for the user menu"));
    }

    /// <summary>Whether presentation mode is currently on for this viewer.</summary>
    private bool PresentationModeOn => presentationScreen.Active;

    /// <summary>
    /// Flips the viewer's presentation mode. Writes their OWN profile through the one mutation API,
    /// so the owning user hub serialises it and every surface bound to that node — this menu, the
    /// header indicator, every open tile surface — re-renders from the same write.
    /// </summary>
    private void TogglePresentationMode()
    {
        var viewerId = AccessService.ViewerId();
        if (!PresentationScreenExtensions.IsPersonalViewer(viewerId))
            return;
        var on = !presentationScreen.Active;
        var options = MeshHub.JsonSerializerOptions;
        MeshHub.GetMeshNodeStream(viewerId!)
            .Update(node => node with
            {
                Content = PresentationPreference.SetMode(node.ContentAs<User>(options), on)
            })
            .Subscribe(_ => { }, ex => Logger.LogWarning(ex,
                "Failed to set presentation mode for {Viewer}", viewerId));
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

    /// <summary>The "Try the new frontend" entry only shows when the deployment configured a React app URL.</summary>
    private bool showFrontendToggle => FrontendSelection.IsEnabled(Configuration);

    /// <summary>
    /// Switch this user to the React frontend: a full-page navigation to the toggle endpoint, which
    /// sets the override cookie and redirects to the React app. Reversed by the React shell's
    /// "Back to classic" entry (GET /frontend/blazor).
    /// </summary>
    private void SwitchToNewFrontend()
        => Navigation.NavigateTo($"{FrontendSelection.EndpointPrefix}/react", forceLoad: true);

    private void NavigateToSettings()
    {
        var userId = AccessService.Context?.ObjectId;
        if (!string.IsNullOrEmpty(userId))
            Navigation.NavigateTo($"/User/{userId}/Settings");
        else
            Navigation.NavigateTo(GlobalSettingsNodeType.SettingsHref);
    }

    // Platform info lives in the ungated global-settings tabs; the user menu is where users find
    // "About" / "What's New" (a regular user's Settings goes to their own User node, not here). The
    // tab IDS stay literals — those tabs live in the higher Memex.Portal.Shared layer, which this
    // (framework) project can't reference, and the ids ("About"/"WhatsNew") are stable. The ROUTE
    // around them is not a literal: it is derived from the registered node path, because four call
    // sites once spelled that path as plural lowercase "_settings" and every one of them 404'd
    // (#1817).
    private void NavigateToWhatsNew() => Navigation.NavigateTo(GlobalSettingsNodeType.TabHref("WhatsNew"));

    private void NavigateToAbout() => Navigation.NavigateTo(GlobalSettingsNodeType.TabHref("About"));

    private void Logout()
    {
        var logoutUrl = AuthNavigation.GetLogoutUrl();
        Navigation.NavigateTo(logoutUrl, forceLoad: true);
    }

    /// <summary>Releases the presentation-screen subscription when the header component goes away.</summary>
    public void Dispose() => presentationSubscription?.Dispose();
}
