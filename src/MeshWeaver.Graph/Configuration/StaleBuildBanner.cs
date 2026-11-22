using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>What the banner is ABOUT — two states that look alike and need opposite advice.</summary>
public enum StaleBuildKind
{
    /// <summary>
    /// The type has PUBLISHED a newer build and this instance is still on the previous one. Fully
    /// functional, and a recycle picks the new one up — so the banner is an offer.
    /// </summary>
    NewerBuildAvailable,

    /// <summary>
    /// 🚨 The bytes this instance is EXECUTING are not the bytes the type says it published — the
    /// served assembly's MVID differs from <see cref="NodeTypeDefinition.LatestAssemblyMvid"/>.
    ///
    /// <para>This is NOT an offer, and the difference matters to the reader: the node's
    /// <c>Ok</c> is not evidence about what is being served, and a recycle re-binds the same local
    /// copy, so pressing the button changes nothing. Systemorph/MeshWeaver#2471 — measured on memex
    /// 2026-08-26 across two NodeType recycles, four instance recycles and a forced compile, with
    /// this very adornment empty throughout because a PATH comparison cannot see it.</para>
    /// </summary>
    ServedBuildIsNotPublished,
}

/// <summary>
/// The OFFER a per-instance hub publishes when its NodeType has compiled a build newer than the
/// assembly this instance actually bound — or, since #2471, when the bytes it is EXECUTING are not
/// the bytes the type claims to have published at all. Held on the instance hub (see
/// <see cref="StaleBuildBanner"/>) and rendered as a banner ABOVE the instance's real content.
/// </summary>
/// <param name="NodeType">The NodeType path that published the newer build.</param>
/// <param name="PublishedAssemblyPath">The build the type now publishes.</param>
/// <param name="BoundAssemblyPath">The build this instance is executing.</param>
public sealed record StaleBuildOffer(
    string NodeType,
    string? PublishedAssemblyPath,
    string? BoundAssemblyPath)
{
    /// <summary>
    /// Which of the two states this is. 🚨 An init-only PROPERTY, never a primary-constructor
    /// parameter: adding a defaulted parameter to a public record's primary ctor is
    /// binary-breaking and <c>scripts/check-record-signatures.py</c> refuses it. Defaults to
    /// <see cref="StaleBuildKind.NewerBuildAvailable"/>, so an existing construction keeps meaning
    /// exactly what it meant.
    /// </summary>
    public StaleBuildKind Kind { get; init; } = StaleBuildKind.NewerBuildAvailable;

    /// <summary>The MVID the NodeType records for the build it published; null when unknown.</summary>
    public string? PublishedAssemblyMvid { get; init; }

    /// <summary>The MVID of the bytes this instance actually bound; null when unknown.</summary>
    public string? BoundAssemblyMvid { get; init; }
}

/// <summary>
/// Renders "a newer build of this type is available — recycle to pick it up" as an ADORNMENT above
/// every layout area of an instance whose NodeType has since published a different assembly.
///
/// <para>🚨 <b>An offer by default, convergence by deployment choice.</b> This REPLACED an
/// unguarded auto-recycle: the stale-assembly watcher used to post a self-<c>DisposeRequest</c>
/// the moment a type published, so every live instance of that type restarted on every publish —
/// publication frequency became restart frequency, and a user mid-edit lost their hub without
/// asking for it. The user now decides. The stated consequence: an instance whose viewer never
/// clicks keeps serving the OLDER assembly indefinitely. That is deliberate and safe — the old
/// build is a build that worked — but it does mean "published" no longer implies "every instance
/// is running it", and on a production portal that inversion IS the outage (memex 2026-08-25: a
/// package update recompiled the Store green while every serving instance stayed on the previous
/// assembly behind this banner). A deployment whose invariant is convergence therefore opts back
/// into the automatic recycle — now bounded by the assembly-path gate, the settle window and
/// <c>Take(1)</c> — via <see cref="NodeTypeEnrichmentHelpers.AutoRecycleConfigKey"/>; the banner
/// remains the default everywhere else.</para>
///
/// <para>🚨 <b>Adornment, not replacement.</b> The compilation-error overlay
/// (<c>NodeTypeEnrichmentHelpers.WithCompilationErrorOverlay</c>) SWAPS the hub's
/// <c>HubConfiguration</c>, which is right for "this type is broken" — there is nothing good to
/// show. An instance on a superseded assembly is fully FUNCTIONAL, so taking its page away because
/// a newer build exists would be a regression. This is wired with <c>AddLayout</c>, which APPENDS
/// (<c>LayoutExtensions.AddLayout</c>) and is therefore structurally incapable of replacing the
/// configuration the way the overlay does — the two states stay distinct by construction rather
/// than by wording.</para>
///
/// <para><b>Why a sidecar area and not a wrapper.</b> There is no hook that decorates an area's
/// OUTPUT, and building one is not merely awkward but wrong three times over: a renderer writing
/// <c>context.Area</c> first runs <c>DisposeExistingAreas</c> and wipes the real view plus its
/// nested subscriptions; the live generator path delivers each emission through
/// <c>LayoutAreaHost.UpdateArea</c>, bypassing <c>LayoutDefinition.Render</c> entirely, so any wrap
/// survives exactly until the next emission; and a global renderer returning a never-completing
/// observable disables the completion-gated "Area not found" fallback for EVERY area on the hub.
/// So this follows the framework's existing adornment idiom — <c>$Menu</c> / <c>$Dialog</c>: write
/// a DIFFERENT area key and let the chrome render that slot around the content.</para>
/// </summary>
public static class StaleBuildBanner
{
    /// <summary>
    /// Sidecar area key the banner control is written to — read by the Blazor chrome
    /// (<c>LayoutAreaView</c>) and rendered above the area content. Not a real area: it is never a
    /// <c>/</c>-descendant of one, so no area teardown reaps it (see the disposal note in
    /// <see cref="Render"/>).
    /// </summary>
    public const string BannerArea = LayoutAreaSlots.StaleBuildBanner;

    /// <summary>Disposable key for the per-render subscription — see <see cref="Render"/>.</summary>
    private const string SubscriptionKey = "stale-build-banner:" + BannerArea;

    /// <summary>
    /// Global predicate renderer (<c>WithRenderer(_ =&gt; true, Render)</c>): subscribes the hub's
    /// offer and pushes the banner into <see cref="BannerArea"/>.
    ///
    /// <para>🚨 Returns the store UNCHANGED and SYNCHRONOUSLY. A global renderer that returns a
    /// never-completing observable would disable <c>LayoutDefinition</c>'s completion-gated
    /// "Area not found" placeholder for every area on this hub, turning an unknown area into an
    /// eternal spinner. The live work happens off-band through <c>host.UpdateArea</c>, exactly as
    /// <c>NodeMenuItemsExtensions.RenderMenus</c> does.</para>
    ///
    /// <para>🚨 <c>ReplaceDisposable</c> keyed on the written area — never the APPENDING
    /// <c>RegisterForDisposal</c> (issue #606). This runs on EVERY area render, and area teardown
    /// only reaps keys that are <c>context.Area</c> or its <c>/</c>-descendants — which
    /// <c>$Banner</c> never is — so the appending form would stack one live subscription per
    /// re-render, unreaped for the hub's lifetime, each an additional writer of the same area.</para>
    /// </summary>
    public static EntityStoreAndUpdates Render(
        LayoutAreaHost host, RenderingContext context, EntityStore store)
    {
        var unchanged = new EntityStoreAndUpdates(store, [], host.Stream.StreamId);
        var offers = host.Hub.Get<BehaviorSubject<StaleBuildOffer?>>();
        if (offers is null)
            return unchanged;   // no watcher armed on this hub (nothing published to compare)

        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(StaleBuildBanner));
        var areaContext = new RenderingContext(BannerArea);
        var nodePath = host.Hub.Address.Path;

        host.ReplaceDisposable(
            SubscriptionKey,
            offers
                .DistinctUntilChanged()
                .Subscribe(
                    offer => host.UpdateArea(areaContext, BuildControl(host, nodePath, offer)),
                    ex => logger?.LogWarning(ex,
                        "Stale-build banner render failed for '{InstancePath}'", nodePath)));

        return unchanged;
    }

    /// <summary>
    /// The menu context the portal top bar's <b>Update</b> button reads (<c>$Menu:Update</c>),
    /// registered on every instance hub that arms the stale-build watcher. It holds ONE entry
    /// exactly while the hub's offer is <see cref="StaleBuildKind.NewerBuildAvailable"/>, and
    /// nothing otherwise, so the button shows iff pressing it would bind a newer build.
    /// </summary>
    public const string UpdateMenuContext = "Update";

    /// <summary>
    /// The <see cref="UpdateMenuContext"/> provider: the hub's live offer, projected through
    /// <see cref="UpdateMenuItems"/>. Emits an empty slice (never <c>Observable.Empty</c>) on a hub
    /// with no watcher armed, so the menu aggregator never stalls on it.
    /// </summary>
    public static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> UpdateMenuProvider(
        LayoutAreaHost host, RenderingContext context)
    {
        var nodePath = host.Hub.Address.Path;
        var offers = host.Hub.Get<BehaviorSubject<StaleBuildOffer?>>();
        return offers is null
            ? Observable.Return(UpdateMenuItems(nodePath, null))
            : offers.DistinctUntilChanged().Select(offer => UpdateMenuItems(nodePath, offer));
    }

    /// <summary>
    /// The execution-time half of the Update button (<see cref="UpdateToNewestBuildExtensions"/>):
    /// the hub recycles ITSELF, and only while its CURRENT offer is
    /// <see cref="StaleBuildKind.NewerBuildAvailable"/>. The menu entry is a render-time snapshot;
    /// this is the decision at the press.
    ///
    /// <para>🚨 <b>Claim, answer, dispose — in that order, in one handler turn.</b> The handler runs
    /// on the hub's single-threaded action block, so claiming the offer (setting it to null) is
    /// atomic: of two overlapping presses exactly one finds the offer, and the other is answered
    /// <c>false</c> with nothing torn down. The answer is posted before the
    /// <see cref="DisposeRequest"/>, and both are posted before the caller can send its follow-up
    /// read, so that read queues behind the dispose and is answered by the fresh activation.
    /// Clearing the offer also clears the button and the banner as the recycle begins.</para>
    ///
    /// <para>The dispose is posted directly rather than through
    /// <c>RecycleSelfAfterAcceptedWork</c>: a handler runs only on a hub whose initialization gates
    /// have opened, which is the case that helper exists for, and a deferred post would lose the
    /// ordering above. Like the node menu's Recycle, a press does not wait for a step in flight; the
    /// viewer asked for it.</para>
    /// </summary>
    internal static IMessageDelivery HandleUpdateToNewestBuild(
        IMessageHub hub, IMessageDelivery<UpdateToNewestBuildRequest> request)
    {
        var offers = hub.Get<BehaviorSubject<StaleBuildOffer?>>();
        var claimed = offers?.Value is { Kind: StaleBuildKind.NewerBuildAvailable } offer ? offer : null;
        if (claimed is not null)
            offers?.OnNext(null);
        hub.Post(new UpdateToNewestBuildResponse(claimed is not null), o => o.ResponseFor(request));
        if (claimed is not null)
            hub.Post(new DisposeRequest
            {
                Reason = $"{request.Message.Reason} (NodeType '{claimed.NodeType}' published build "
                         + $"'{claimed.PublishedAssemblyPath}', superseding the bound '{claimed.BoundAssemblyPath}')",
            }, o => o.WithTarget(hub.Address));
        return request.Processed();
    }

    /// <summary>
    /// The top bar's Update entry for <paramref name="offer"/>. Pure, so the "iff stale" rule is
    /// testable without a hub.
    ///
    /// <para><b>One entry only for <see cref="StaleBuildKind.NewerBuildAvailable"/>.</b> No offer:
    /// there is nothing to update to. <see cref="StaleBuildKind.ServedBuildIsNotPublished"/>: a
    /// recycle re-binds the same local copy (#2471), so the button would be measured to change
    /// nothing; the banner says so instead.</para>
    ///
    /// <para>🚨 <b>No Update permission is required, deliberately.</b> The entry runs the same
    /// <see cref="MenuActions.Recycle"/> command as the node menu's Recycle, which DOES require
    /// Update, but in this one state the recycle does nothing a reader could object to. It writes
    /// no data. It rebinds this one address to a build the type has ALREADY published (compiled
    /// <c>Ok</c> under the type's own authority). It is exactly what the platform does by itself
    /// where <see cref="NodeTypeEnrichmentHelpers.AutoRecycleConfigKey"/> is on. And it limits
    /// itself: the fresh activation binds the newest build, its offer is null, and the entry is
    /// gone, so viewers can trigger at most one recycle per publication, the same bound as the
    /// automatic recycle's <c>Take(1)</c>. The bound is enforced at the PRESS, not at render: the
    /// portal runs <see cref="UpdateToNewestBuildExtensions.UpdateToNewestBuild"/>, and this hub
    /// (<see cref="HandleUpdateToNewestBuild"/>) claims its offer and recycles itself only while it
    /// is stale, so a button left on screen, or a second overlapping press, recycles nothing.
    /// Recycling a CURRENT hub is a different act (it interrupts other viewers and gains no build)
    /// and stays behind Update in the node menu.</para>
    /// </summary>
    /// <param name="nodePath">The instance the entry recycles.</param>
    /// <param name="offer">The hub's current offer, or null.</param>
    public static IReadOnlyCollection<NodeMenuItemDefinition> UpdateMenuItems(
        string nodePath, StaleBuildOffer? offer)
        => offer is { Kind: StaleBuildKind.NewerBuildAvailable }
            ?
            [
                new NodeMenuItemDefinition("Update", MeshNodeLayoutAreas.RecycleArea,
                    RequiredPermission: Permission.Read, Order: 0,
                    Href: RecycleLayoutArea.LandingHref(nodePath))
                {
                    LabelKey = "menu.update",
                    TooltipKey = "menu.updateTooltip",
                    Action = MenuActions.Recycle,
                },
            ]
            : [];

    /// <summary>
    /// The banner itself, or an EMPTY control when there is no offer — the empty control is how the
    /// slot is cleared, since the banner must disappear if the offer is ever withdrawn.
    /// A markdown link to the node's Recycle area, so the button the user presses is the one
    /// <c>RecycleLayoutArea</c> owns (confirmation included) rather than a second teardown path.
    /// </summary>
    private static UiControl BuildControl(LayoutAreaHost host, string nodePath, StaleBuildOffer? offer)
    {
        if (offer is null)
            return Controls.Stack;

        // 🚨 A served-build MISMATCH gets NO recycle link, and that omission is the message
        // (#2471). Recycling re-binds the same local copy of the same store key, so offering the
        // button here would hand the viewer the exact remedy that was measured to report success
        // and change nothing — six times over, on memex. The banner's job in that state is to say
        // the status is not evidence, not to suggest a cure it does not have.
        if (offer.Kind == StaleBuildKind.ServedBuildIsNotPublished)
            return Controls.Markdown(host.Localize("ui.mdServedBuildIsNotPublished"));

        var recycleHref = MeshNodeLayoutAreas.BuildUrl(nodePath, MeshNodeLayoutAreas.RecycleArea);
        return Controls.Markdown(
            $"{host.Localize("ui.mdStaleBuildAvailable")} [{host.Localize("menu.recycle")}]({recycleHref})");
    }
}
