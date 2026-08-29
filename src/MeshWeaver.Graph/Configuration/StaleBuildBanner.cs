using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
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
