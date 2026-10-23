using System.Buffers.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The inbox's READING VIEW — <c>/{owner}/Inbox/{item}</c>, where a click on a row goes. A row
/// ALWAYS opens inside the portal, as a mail client opens a mail in its reading pane (owner,
/// 2026-10-16: "when clicking, it should always give me a memex view with the content, not
/// redirect me"). The provider's own client is a secondary link under the content, never the
/// click.
///
/// <para>The item is named by its node PATH, base64url-encoded into one segment
/// (<see cref="ItemId"/>) so a path's slashes never reach the router. An item that lives outside
/// the mesh — a live mailbox or chat row, which no store holds — is READ by the
/// <see cref="IInboxItemReader"/> that handles its path, live and as the owner; nothing it returns
/// is stored. A stored item no reader handles is shown by its own page, embedded.</para>
///
/// <para>🚨 Only the owner's OWN items are opened: the path must lie in the owner's partition,
/// whatever the address says. Everything shown is PLAIN TEXT rendered escaped — a mail's markup
/// never reaches the portal.</para>
/// </summary>
public static class InboxReadingView
{
    /// <summary>The one URL segment that names <paramref name="path"/> — base64url of its UTF-8 bytes. Pure.</summary>
    public static string ItemId(string path) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(path.Trim('/')));

    /// <summary>The path an <see cref="ItemId"/> names, or null for anything that is not one. Pure.</summary>
    public static string? PathOf(object? id)
    {
        if (LayoutAreaReference.NormalizeScalar(id) is not { Length: > 0 } text)
            return null;
        try
        {
            var path = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(text.Trim('/')));
            return path.Length > 0 && !path.Any(char.IsControl) ? path : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Where a click on the row of <paramref name="path"/> goes: the owner's reading view. Pure.</summary>
    public static string Url(string owner, string path)
        => $"/{owner.Trim('/')}/{InboxLayoutArea.AreaName}/{ItemId(path)}";

    /// <summary>
    /// Whether <paramref name="path"/> is an item of <paramref name="owner"/>'s own partition — the
    /// only items the reading view opens. Segment-exact: <c>alice2/…</c> is not <c>alice</c>'s. Pure.
    /// </summary>
    public static bool IsOwnersItem(string owner, string path)
    {
        var o = owner.Trim('/');
        var p = path.Trim('/');
        return o.Length > 0 && p.Length > o.Length + 1 && p[o.Length] == '/'
            && p.StartsWith(o, StringComparison.OrdinalIgnoreCase)
            && !p.Split('/').Any(segment => segment is "" or "." or "..");
    }

    /// <summary>The reader that handles <paramref name="path"/>, or null when none does.</summary>
    public static IInboxItemReader? ReaderFor(IEnumerable<IInboxItemReader> readers, string path)
        => readers.FirstOrDefault(r => r.Handles(path));

    /// <summary>The reading view of <paramref name="path"/> for <paramref name="owner"/>.</summary>
    internal static IObservable<UiControl?> Render(LayoutAreaHost host, string owner, string? zone, string? path)
    {
        var back = Controls.NavLink(host.Localize("inbox.read.back"), FluentIcons.ArrowLeft(),
            $"/{owner.Trim('/')}/{InboxLayoutArea.AreaName}");
        UiControl Page(UiControl content) => Controls.Stack.WithWidth("100%").WithVerticalGap(12)
            .WithView(back, "Back")
            .WithView(content, "Item");
        if (path is null || !IsOwnersItem(owner, path))
            return Observable.Return<UiControl?>(Page(Controls.Markdown(host.Localize("inbox.read.notFound"))));

        var hub = host.Hub;
        if (ReaderFor(hub.ServiceProvider.GetServices<IInboxItemReader>(), path) is not { } reader)
            // A stored item: its own page, as its type renders it — except an approval request, which
            // is only the announcement of an approval: opening it goes to THAT node.
            return StoredItem(host, owner, path, Page);

        // Resolved ONCE, on the render turn: the read answers on another thread.
        var locale = host.ViewerLocale();
        var culture = InboxLayoutArea.ViewerCulture(locale);
        var texts = new Texts(
            Loading: host.Localize("inbox.read.loading"),
            Failed: host.Localize("inbox.read.failed"),
            From: host.Localize("inbox.read.from"),
            Account: host.Localize("inbox.read.account"),
            Received: host.Localize("inbox.read.received"),
            OpenIn: host.Localize("inbox.read.openIn"),
            // The read answers on another thread, where the viewer's language is no longer ambient:
            // the locale is captured HERE and the provider's name filled in with it.
            OpenInProvider: provider => LocalizationCatalog.Get("inbox.read.openInProvider", locale, provider));
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxReadingView));
        IObservable<InboxItemContent> read;
        try
        {
            read = reader.Read(hub, path, owner);
        }
        catch (Exception ex)
        {
            read = Observable.Throw<InboxItemContent>(ex);
        }
        // The template at once, the content when the read answers (Doc/GUI/DataBinding → "Templates
        // first, data later").
        return Observable.Return<UiControl?>(Page(Controls.Markdown(texts.Loading)))
            .Concat(read.Take(1)
                .Select(content => (UiControl?)Page(Content(content, zone, culture, texts)))
                .DefaultIfEmpty(Page(Controls.Markdown(texts.Failed)))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "Inbox: the reader for {Path} faulted", path);
                    return Observable.Return<UiControl?>(Page(Controls.Markdown(texts.Failed)));
                }));
    }

    /// <summary>
    /// The reading view of a STORED item: its own page AT ONCE, replaced by a redirect when the item turns out to
    /// announce an approval (<see cref="InboxApprovalTargets.TargetOf"/>) — the notification of an approval request
    /// takes the viewer to the approval it names. The classification reads the item's AUTHORITATIVE content (the
    /// node's own stream, once an exact-path listing says it exists — an index snapshot can trail a delivery) and
    /// never delays the page: a slow, failing or absent read leaves the item's own page where it is.
    /// </summary>
    private static IObservable<UiControl?> StoredItem(LayoutAreaHost host, string owner, string path, Func<UiControl, UiControl> page)
    {
        var hub = host.Hub;
        var access = hub.ServiceProvider.GetService<AccessService>();
        // Captured on the render turn: the read lands on another one.
        var viewer = access?.Context ?? access?.CircuitContext;
        var own = Observable.Return<UiControl?>(page(Controls.LayoutArea(new Address(path), MeshNodeLayoutAreas.OverviewArea)));
        // What the item announces is read on the DATA side (InboxApprovalTargets): the page only decides what to show.
        var redirect = InboxApprovalTargets.AnnouncedTarget(hub, viewer, owner, path)
            .Select(target => (UiControl?)Controls.Redirect("/" + target.TrimStart('/')));
        return own.Concat(redirect);
    }

    /// <summary>The texts a reading view shows, resolved on the render turn.</summary>
    internal sealed record Texts(string Loading, string Failed, string From, string Account, string Received,
        string OpenIn, Func<string, string> OpenInProvider);

    /// <summary>
    /// One read item as controls: subject, who and when, the body (plain text, escaped, line breaks
    /// kept), a conversation's messages, and the provider's own link last. Pure.
    /// </summary>
    internal static UiControl Content(InboxItemContent content, string? zone, CultureInfo culture, Texts texts)
        => content.Refusal is { Length: > 0 } refusal
            ? Controls.Label(refusal)
            : Parts(content, zone, culture, texts)
                .Aggregate(Controls.Stack.WithVerticalGap(8), (view, part) => view.WithView(part.View, part.Id));

    /// <summary>The named parts of <see cref="Content"/>, in page order. Pure.</summary>
    internal static ImmutableList<(string Id, UiControl View)> Parts(InboxItemContent content, string? zone, CultureInfo culture, Texts texts)
    {
        string When(DateTimeOffset at) => DisplayTimeExtensions.ToDisplayTime(at, zone).ToString("g", culture);
        var parts = ImmutableList.Create<(string Id, UiControl View)>(
            ("Subject", Controls.H2(content.Subject.Length > 0 ? content.Subject : "—").WithStyle("margin: 0;")));
        var meta = ImmutableList<string>.Empty;
        if (content.From.Length > 0)
            meta = meta.Add($"{texts.From}: {content.From}");
        if (content.Account.Length > 0)
            meta = meta.Add($"{texts.Account}: {content.Account}");
        if (content.ReceivedAt is { } received)
            meta = meta.Add($"{texts.Received}: {When(received)}");
        if (!meta.IsEmpty)
            parts = parts.Add(("Meta", Controls.Label(string.Join(" · ", meta)).WithStyle("opacity: 0.75;")));
        if (content.Body.Length > 0)
            parts = parts.Add(("Body", Text(content.Body)));
        for (var i = 0; i < content.Messages.Count; i++)
        {
            var message = content.Messages[i];
            var head = message.SentAt is { } sent ? $"{message.From} · {When(sent)}" : message.From;
            parts = parts.Add(("Message" + i.ToString(CultureInfo.InvariantCulture), Controls.Stack.WithVerticalGap(2)
                .WithView(Controls.Label(head).WithStyle("font-weight: 600;"), "From")
                .WithView(Text(message.Body), "Text")));
        }
        if (IsHttps(content.WebLink))
            parts = parts.Add(("OpenIn", Controls.NavLink(
                content.Provider.Length > 0 ? texts.OpenInProvider(content.Provider) : texts.OpenIn,
                FluentIcons.Open(), content.WebLink)));
        return parts;
    }

    /// <summary>Plain text as a label that keeps its line breaks — rendered escaped, never as markup.</summary>
    private static UiControl Text(string text)
        => Controls.Label(text).WithStyle("white-space: pre-wrap; overflow-wrap: anywhere;");

    /// <summary>Whether <paramref name="link"/> is an absolute https URL. Pure.</summary>
    internal static bool IsHttps(string? link)
        => Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
