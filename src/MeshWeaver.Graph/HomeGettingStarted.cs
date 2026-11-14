using System;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The <b>Getting started</b> guide on the default user home (<c>@@("area/GettingStarted")</c>,
/// policy <c>home-getting-started-guide</c>): five steps a new user takes — ask the assistant,
/// open or create a space, install an app from the Store, pin what you use, browse the docs —
/// with a <b>Hide</b> button that stores the choice on the owner's own <see cref="User"/> node
/// (<see cref="User.HideGettingStarted"/>).
///
/// <para><b>A template, data-bound.</b> The area emits the guide at once, its style bound to
/// <c>/data/gettingStartedStyle</c>, which the user hub's own node stream feeds: hidden until the
/// node answers, shown while the flag is off, hidden again the moment the flag is written — from
/// this tab, another tab or another device. Showing or hiding is a data change, never a rebuild
/// (Doc/GUI/DataBinding → "Templates first, data later"). The click writes through <c>GetMeshNodeStream(userPath).Update</c>, the one mutation
/// API, with the TYPED overload so an unreadable profile faults the write instead of overwriting
/// the profile with defaults (#3623).</para>
///
/// <para><b>Owner only.</b> The start page is personal: a visitor reading somebody's home sees
/// the profile, never this guide, and a direct address of the area by anyone but the owner
/// renders nothing.</para>
///
/// <para><b>Text.</b> Every visible string is a catalog key (<c>home.gettingStarted.*</c>) in
/// English and German — the guide is platform-owned text, so it follows the VIEWER's language.
/// See <c>Doc/Architecture/AppsHome</c> → "Getting started".</para>
/// </summary>
public static class HomeGettingStarted
{
    /// <summary>The area name the default home embeds with <c>@@("area/GettingStarted")</c>.</summary>
    public const string AreaName = "GettingStarted";

    /// <summary>The number of steps; each is the catalog key <c>home.gettingStarted.step{n}</c>.</summary>
    public const int StepCount = 5;

    /// <summary>
    /// The guide as markdown in <paramref name="locale"/>: the intro line, then the numbered steps.
    /// Pure, so the text is assertable without a hub.
    /// </summary>
    /// <param name="locale">The viewer's locale.</param>
    /// <returns>The guide body.</returns>
    public static string StepsMarkdown(string? locale)
    {
        var lines = new System.Text.StringBuilder()
            .Append(LocalizationCatalog.Get("home.gettingStarted.intro", locale)).Append("\n\n");
        for (var i = 1; i <= StepCount; i++)
            lines.Append(i).Append(". ").Append(LocalizationCatalog.Get($"home.gettingStarted.step{i}", locale)).Append('\n');
        return lines.ToString().TrimEnd();
    }

    /// <summary>The <c>/data</c> id the guide's STYLE is bound to: empty while shown, <see cref="HiddenStyle"/> once hidden.</summary>
    public const string StyleId = "gettingStartedStyle";

    /// <summary>The bound style of a hidden guide.</summary>
    public const string HiddenStyle = "display: none;";

    /// <summary>
    /// The style the guide is bound to for <paramref name="user"/>: <see cref="HiddenStyle"/> once
    /// the owner hid it, else empty. Pure.
    /// </summary>
    /// <param name="user">The owner's profile, or null while it carries no content.</param>
    /// <returns>The style string.</returns>
    public static string StyleFor(User? user) => user?.HideGettingStarted == true ? HiddenStyle : string.Empty;

    /// <summary>
    /// The guide TEMPLATE: a title, the steps and the Hide button wired to <paramref name="hide"/>,
    /// with its style BOUND to <c>/data/{<see cref="StyleId"/>}</c> so showing and hiding it is a
    /// data change, never a rebuild. Pure apart from the click handler it wires.
    /// </summary>
    /// <param name="locale">The viewer's locale.</param>
    /// <param name="hide">The write the Hide button runs; completes when the write is confirmed.</param>
    /// <returns>The guide control.</returns>
    public static UiControl Template(string? locale, Func<IObservable<Unit>> hide)
    {
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("align-items: center;")
            .WithView(Controls.Title(LocalizationCatalog.Get("home.gettingStarted.title", locale), 3)
                .WithStyle("flex: 1; margin: 0;"), "Title")
            .WithView(Controls.Button(LocalizationCatalog.Get("home.gettingStarted.hide", locale))
                .WithLabel(LocalizationCatalog.Get("home.gettingStarted.hideTooltip", locale))
                .WithAppearance(Appearance.Stealth)
                .WithReactiveClickAction(_ => hide()), "Hide");
        return (Controls.Stack
                .WithWidth("100%")
                .WithVerticalGap(4)
                .WithView(header, "Header")
                .WithView(Controls.Markdown(StepsMarkdown(locale)), "Steps"))
            with { Style = new JsonPointerReference(LayoutAreaReference.GetDataPointer(StyleId)) };
    }

    /// <summary>What a viewer who is not the home's owner gets: nothing. Pure.</summary>
    public static UiControl Nothing() => Controls.Stack;

    /// <summary>
    /// Hides the guide for the user at <paramref name="userPath"/>: sets
    /// <see cref="User.HideGettingStarted"/> on their own node through the one mutation API. Cold —
    /// the write runs on Subscribe. Idempotent: an already-hidden guide writes nothing.
    /// </summary>
    /// <param name="hub">The hub issuing the write.</param>
    /// <param name="userPath">The user node's path (the user partition root).</param>
    /// <returns>The node after the write.</returns>
    public static IObservable<MeshNode> Hide(IMessageHub hub, string userPath)
        => hub.GetMeshNodeStream(userPath).Update<User>((node, content) =>
        {
            // Absent content is create-on-write; unreadable content faults (typed overload).
            var user = content ?? new User();
            return user.HideGettingStarted ? node : node with { Content = user with { HideGettingStarted = true } };
        });

    /// <summary>
    /// The live style of the guide on the user hub at <paramref name="host"/>: hidden until the
    /// owner's node answers (so a hidden guide never flashes), then <see cref="StyleFor"/> of every
    /// emission of that node.
    /// </summary>
    internal static IObservable<string> LiveStyle(LayoutAreaHost host)
    {
        var options = host.Hub.JsonSerializerOptions;
        return host.Workspace.GetStream(new MeshNodeReference())!
            .Select(change => StyleFor(change.Value.ContentAs<User>(options)))
            .StartWith(HiddenStyle)
            .DistinctUntilChanged();
    }

    /// <summary>
    /// The <see cref="AreaName"/> view: emits the guide TEMPLATE at once and binds its visibility
    /// to the owner's node (<see cref="LiveStyle"/>, fed into <c>/data/{StyleId}</c>); nothing for
    /// anyone but the home's owner.
    /// </summary>
    internal static IObservable<UiControl?> View(LayoutAreaHost host, RenderingContext _)
    {
        var userPath = host.Hub.Address.ToString();
        var ownerId = userPath.StartsWith("User/") ? userPath[5..] : userPath;
        if (!UserActivityLayoutAreas.IsViewerOwner(host.ViewerContext, ownerId))
            return Observable.Return<UiControl?>(Nothing());
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(HomeGettingStarted));
        // The button stays pending until the write is confirmed and reports a refusal, never swallows it.
        IObservable<Unit> HideGuide() =>
            Hide(host.Hub, userPath)
                .Do(_ => { }, ex => logger?.LogWarning(ex, "[Home] hiding the getting-started guide for {Owner} failed", ownerId))
                .Select(_ => Unit.Default);
        host.SubscribeToDataStream(StyleId, LiveStyle(host));
        return Observable.Return<UiControl?>(Template(host.ViewerLocale(), HideGuide));
    }
}
