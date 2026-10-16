namespace MeshWeaver.Layout;

/// <summary>
/// <b>Install on iPhone / Android</b> — puts the page it is on onto the home screen as a web app, the
/// way each platform actually allows it. It never adds an icon by itself: a person opens it, and then
///
/// <list type="bullet">
///   <item><description><b>iPhone / iPad</b> is guided to Share → <i>Add to Home Screen</i> (Safari
///     offers no programmatic prompt, and none is promised), with the suggested
///     <see cref="AppName"/>;</description></item>
///   <item><description><b>Android</b> gets an <b>Install</b> button that raises the browser's own
///     install prompt when the browser offered one, and the browser-menu steps when it did
///     not;</description></item>
///   <item><description>an <b>in-app browser</b> (a messenger's or a social app's) is told to open the
///     page in the real browser first, with the address to copy;</description></item>
///   <item><description>a page <b>already opened from the home screen</b> says so and pitches
///     nothing;</description></item>
///   <item><description>a <b>desktop</b> browser shows the address to open on the phone.</description></item>
/// </list>
///
/// <para>The detected platform can be switched by hand, because detection reads a user-agent that
/// can be wrong. While the control is rendered, the page's web-app manifest is
/// <see cref="ManifestHref"/> — the page's own identity, start address and scope, so installing one
/// page never replaces another page's (or the portal's) home-screen entry; the previous manifest is
/// restored when the control goes away. The control asks for no permission (no notifications, camera,
/// contacts or location).</para>
/// </summary>
/// <param name="ManifestHref">The page's web-app manifest (a same-origin URL) — a literal or a data-binding pointer.</param>
public record InstallAppControl(object ManifestHref) : UiControl<InstallAppControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The name to suggest for the home-screen entry — a literal or a binding; optional.</summary>
    public object? AppName { get; init; }

    /// <summary>The page's absolute address, shown where the page has to be opened elsewhere (desktop, in-app browser) — a literal or a binding; optional.</summary>
    public object? PageUrl { get; init; }

    /// <summary>Sets the suggested home-screen name.</summary>
    /// <param name="appName">The name, or a binding.</param>
    public InstallAppControl WithAppName(object appName) => this with { AppName = appName };

    /// <summary>Sets the page's absolute address.</summary>
    /// <param name="pageUrl">The address, or a binding.</param>
    public InstallAppControl WithPageUrl(object pageUrl) => this with { PageUrl = pageUrl };
}
