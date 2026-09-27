using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// The <b>Inbox app</b> — <c>/{user}/Inbox</c>, installed for every user by
/// <c>SeedInboxAppLogonAction</c>: one page with what needs the person, what is running for them and
/// what just finished. It is the rendered side of <see cref="InboxQueries"/>: every leg is resolved for
/// the OWNER of the hub (anchored on their own partition, projected — the invariants
/// <c>InboxLegsAreAnchoredTest</c> pins) and shown as a <c>MeshSearch</c> list, grouped by band.
///
/// <para>Only the owner sees it. A User node is public-read, so without the owner check anyone could
/// open another person's inbox address; the legs are RLS-scoped anyway, but the page says "not yours"
/// instead of rendering someone else's empty shell.</para>
///
/// <para>Two legs that resolve to the SAME query (core's Running and Recent activity legs do today) are
/// shown once, under the first band that asks for them — a row listed twice on one page is noise.</para>
/// </summary>
public static class InboxLayoutArea
{
    /// <summary>The layout area on the User hub — also the app record's id (<c>{user}/_App/Inbox</c>).</summary>
    public const string AreaName = "Inbox";

    /// <summary>The order bands are shown in; a provider's own band follows these, alphabetically.</summary>
    internal static readonly ImmutableArray<string> BandOrder = [InboxBand.NeedsYou, InboxBand.Running, InboxBand.Recent];

    /// <summary>The page.</summary>
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var owner = UserNodeType.OwnerOf(host.Hub.Address.ToString());
        var viewer = host.Hub.ServiceProvider.GetService<AccessService>().ViewerId();
        if (string.IsNullOrEmpty(viewer) || !string.Equals(viewer, owner, StringComparison.OrdinalIgnoreCase))
            return Observable.Return<UiControl?>(Controls.Markdown(host.Localize("inbox.notYours")));

        var stack = Controls.Stack.WithWidth("100%").WithVerticalGap(16)
            .WithView(Controls.H2(host.Localize("inbox.title")).WithStyle("margin: 0;"));
        foreach (var (band, query) in Sections(InboxQueries.BuiltIn, owner))
            stack = stack.WithView(Controls.MeshSearch
                // A provider's own band has no catalog key: its id is its title.
                .WithTitle(BandOrder.Contains(band) ? host.Localize(BandTitleKey(band)) : band)
                .WithHiddenQuery(query)
                .WithShowSearchBox(false)
                .WithShowEmptyMessage(true)
                .WithRenderMode(MeshSearchRenderMode.Flat)
                .WithCollapsibleSections(false)
                .WithSectionCounts(false)
                .WithItemLimit(20)
                .WithMaxColumns(1)
                .WithReactiveMode(true));
        return Observable.Return<UiControl?>(stack);
    }

    /// <summary>
    /// The sections of the page: every enabled leg resolved for <paramref name="owner"/>, in band
    /// order, each distinct query once. Pure — the page's shape is testable without a hub.
    /// </summary>
    internal static IReadOnlyList<(string Band, string Query)> Sections(IEnumerable<InboxQueryLeg> legs, string owner)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return InboxQueries.Resolve(legs, owner)
            .OrderBy(leg => BandOrder.IndexOf(leg.Band) is var i and >= 0 ? i : BandOrder.Length)
            .ThenBy(leg => leg.Band, StringComparer.Ordinal)
            .ThenBy(leg => leg.Order)
            .Where(leg => seen.Add(leg.Query))
            .Select(leg => (leg.Band, leg.Query))
            .ToArray();
    }

    /// <summary>The localization key of a band's title.</summary>
    internal static string BandTitleKey(string band) => "inbox.band." + band;
}
