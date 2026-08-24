using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// An installed-app record adopts the icon of the APP it points at, on the record hub's
/// INITIALIZATION.
///
/// <para>A record carries its own <see cref="MeshNode.Icon"/> so the home's Apps grid can paint
/// from query rows alone — no per-tile hub, no content read. But a record seeded from
/// <c>Admin/HomeConfig.DefaultApps</c>, or written by an install flow with nothing better to hand,
/// gets the generic placeholder, and a grid of identical placeholders defeats the point of an icon
/// grid: you should recognise an app before you read its label.</para>
///
/// <para>🚨 <b>Why here and not on the home's render path.</b> The first attempt repaired icons
/// inside <c>CatalogAreaView</c>'s reactive selector, and that was wrong twice over. Its
/// "run once" flag lived on the SUBSCRIPTION, so every navigation and every reconnect re-ran a
/// cross-partition query plus writes — the storm shape the record model exists to avoid. And the
/// selector runs after the layout area returns, by which point the ambient
/// <c>AccessService</c> context is cleared, so the query and the writes would have executed with
/// NO viewer identity: private targets filtered out, cross-partition writes made as nobody. The
/// record's OWN hub has neither problem — it initialises once per activation, it owns the node it
/// writes, and nothing about it is tied to a viewer's page.</para>
///
/// <para>Bounded by construction: it reads its own node, does nothing unless the icon is missing
/// or generic, resolves exactly ONE target, and writes only when the target actually has a better
/// icon — so a target with no icon of its own leaves the record untouched instead of rewriting the
/// same placeholder on every activation.</para>
///
/// <para>Long term the STORE stamps the real icon when it writes the record and this becomes a
/// no-op. Until then the platform repairs what it renders, because a placeholder grid is a broken
/// feature regardless of which side was supposed to fill it in.</para>
/// </summary>
public static class AppIconAdoption
{
    /// <summary>The placeholder a record wears when nobody supplied a real icon.</summary>
    internal const string GenericIcon = "/static/NodeTypeIcons/puzzlepiece.svg";

    /// <summary>True when a record has no icon, or still wears the placeholder.</summary>
    internal static bool NeedsIcon(MeshNode? record) =>
        record is not null
        && (string.IsNullOrEmpty(record.Icon)
            || string.Equals(record.Icon, GenericIcon, StringComparison.OrdinalIgnoreCase));

    /// <summary>The app this record opens: its <see cref="MeshNode.MainNode"/>, unless that is
    /// just the record's own path (nothing to adopt from).</summary>
    internal static string? TargetOf(MeshNode? record) =>
        record is { MainNode: { Length: > 0 } target }
        && !string.Equals(target, record.Path, StringComparison.OrdinalIgnoreCase)
            ? target
            : null;

    /// <summary>The icon a record should end up with, given its current state and the target's
    /// icon: <c>null</c> means "leave it alone". Pure, so the decision is unit-testable without a
    /// hub.</summary>
    internal static string? IconToAdopt(MeshNode? record, string? targetIcon) =>
        NeedsIcon(record)
        && !string.IsNullOrEmpty(targetIcon)
        && !string.Equals(targetIcon, GenericIcon, StringComparison.OrdinalIgnoreCase)
            ? targetIcon
            : null;

    /// <summary>
    /// The hub-initialization hook. Reactive end to end — no <c>async</c>, no <c>Task</c>, and the
    /// init gate opens as soon as this emits, so a slow or absent target never holds the hub shut.
    /// </summary>
    public static IObservable<Unit> AdoptOnInit(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.AppIconAdoption");
        var workspace = hub.GetWorkspace();
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Return(Unit.Default);

        return workspace.GetMeshNodeStream()
            .Where(node => node is not null)
            .Take(1)
            .SelectMany(record =>
            {
                // Nothing to do — by far the common case once the Store stamps icons, and the
                // reason this costs an activation nothing rather than a query.
                if (!NeedsIcon(record) || TargetOf(record) is not { } target)
                    return Observable.Return(Unit.Default);

                return mesh
                    .Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"path:{target} select:path,id,namespace,name,nodeType,icon"))
                    .Where(change => change.ChangeType == QueryChangeType.Initial)
                    .Select(change => change.Items.FirstOrDefault()?.Icon)
                    .Take(1)
                    .SelectMany(targetIcon =>
                    {
                        if (IconToAdopt(record, targetIcon) is not { } icon)
                            return Observable.Return(Unit.Default);
                        logger?.LogDebug(
                            "Adopting icon of {Target} onto app record {Path}", target, record!.Path);
                        // Re-check inside the update: the record may have been given a real icon
                        // between the read and the write (the Store installing over it), and this
                        // repair must never overwrite a better answer than its own.
                        return workspace.GetMeshNodeStream()
                            .Update(cur => NeedsIcon(cur) ? cur with { Icon = icon } : cur)
                            .Select(_ => Unit.Default);
                    });
            })
            // The init gate must open regardless: an unreachable target is a cosmetic miss, never
            // a reason to hold a hub shut. Timeout bounds the wait; Catch turns any failure into a
            // logged non-event rather than a stuck activation.
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogDebug(ex, "App icon adoption skipped for {Hub}", hub.Address);
                return Observable.Return(Unit.Default);
            })
            .DefaultIfEmpty(Unit.Default);
    }
}
