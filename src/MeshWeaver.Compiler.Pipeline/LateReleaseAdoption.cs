using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 Issue #5057, the residual #5201 recorded but did not repair: <b>a release that lands after
/// BOTH bounds is adopted when it lands</b>, instead of leaving the type pointing at an earlier
/// build's release for good.
///
/// <para><b>The shape, as measured.</b> The settle's release create and the re-cut are each bounded by
/// <see cref="NodeTypeBuildState.CreateBound"/>. The bound stops this process WAITING, not the create,
/// and since #5201 the re-cut is issued at the SAME id as the first attempt, so it adopts a late landing
/// that arrives inside its own bound. A landing slower than both bounds still ends the settle with no
/// release: the node is stamped <see cref="NodeTypeDefinition.UnreleasedBuildPath"/> = the id both
/// attempts were minting, and then the node LANDS at that id. On the control instance on 2026-09-23 the
/// named attempt <c>Hosting/TriageItem/Release/20260923055416-Hd-IFSiA</c> landed 21 s after its id was
/// minted, and #5474 folded 69 more such lines on the image carrying #5201. Before this change nothing
/// read the stamp again. The release existed, the stamp said where, and the type kept binding the
/// previous build's release until someone asked for a new one.</para>
///
/// <para><b>The repair follows the FACT, not a clock.</b> While the NodeType's own record carries an
/// <see cref="NodeTypeDefinition.UnreleasedBuildPath"/>, its hub watches that one path through a synced
/// <c>path:</c> query. The query is empty while the node is absent and holds the node when it lands. It
/// is never a point read of an absent path, which is the storm shape the stream cache's breaker exists
/// for. When the node is there, the pointer is advanced in ONE owner write that re-checks the stamp. No
/// bound is widened and no timer or poll is added. A release that never lands leaves the stamp standing,
/// and the stamp is the report. The watch is re-derived from the record, so it also covers the landing
/// that happened while no activation was alive: the next activation's first listing already holds the
/// node.</para>
///
/// <para><b>Why the id alone is enough evidence.</b> A release id is
/// <c>{second}-{8 chars of SHA256(Collection/ContentPath)}</c>, and the stamp is written only for an id
/// minted for THIS build's bytes (<see cref="NodeTypeBuildState.IsReusableAttempt"/> guards the reuse).
/// A node at that path therefore names these bytes. The stamp describes the current build only: every
/// later settle rewrites or clears it, so "the stamp still names this path" in the owner write is the
/// whole guard.</para>
/// </summary>
public static class LateReleaseAdoption
{
    /// <summary>
    /// The pure decision: the definition with <paramref name="landedPath"/> adopted as its release,
    /// or <c>null</c> when the stamp no longer names that path. It no longer names it when a later
    /// settle rewrote or cleared it, when another adoption already ran, or when the path is some other
    /// release. <c>null</c> means leave the node alone.
    /// </summary>
    /// <param name="definition">The NodeType definition as the owner currently holds it.</param>
    /// <param name="landedPath">The release path observed to exist.</param>
    internal static NodeTypeDefinition? Adopt(NodeTypeDefinition? definition, string landedPath)
    {
        if (definition?.UnreleasedBuildPath is not { Length: > 0 } stamped
            || !string.Equals(stamped, landedPath, StringComparison.Ordinal))
            return null;
        // The same field set ApplyCompileSuccess writes when a release DID land on the settle: the
        // pointer moves to it, the stamp and its reason go, and the release notes the author wrote
        // for this release are spent on it.
        return definition with
        {
            LatestReleasePath = stamped,
            UnreleasedBuildPath = null,
            UnreleasedBuildReason = null,
            ReleaseNotes = null,
        };
    }

    /// <summary>
    /// Emits the stamped <see cref="NodeTypeDefinition.UnreleasedBuildPath"/> once the node at that
    /// path EXISTS. The path is re-derived whenever the own record changes, and a new path supersedes
    /// the watch on the old one (<c>Switch</c>). No stamp means nothing is subscribed.
    ///
    /// <para>🚨 ONE synced query per NodeType, never one per stamped path (review on #5694). The
    /// stream cache keeps each distinct query set's connection for the life of the process, so a
    /// query keyed on the path would grow a resident connection per late release. The listing of the
    /// type's <c>Release</c> children, projected to <c>path</c>, is stable for the type and is
    /// filtered for the stamp in hand.</para>
    /// </summary>
    /// <param name="workspace">The NodeType hub's workspace, which hosts the synced query.</param>
    /// <param name="ownStream">The NodeType's own MeshNode stream.</param>
    /// <param name="accessService">Reads as System, for the same reason the source-set read does:
    /// a per-user read of a node UNDER this NodeType routes a permission check back into this
    /// activation.</param>
    /// <param name="hubPath">The NodeType's path, which keys the synced query.</param>
    /// <param name="options">The hub's serializer options, used to read the own record.</param>
    internal static IObservable<string> Landings(
        IWorkspace workspace,
        IObservable<MeshNode?> ownStream,
        AccessService? accessService,
        string hubPath,
        JsonSerializerOptions options)
    {
        var releaseNamespace = $"{hubPath}/{GraphNodeTypeNames.ReleaseSegment}";
        return ownStream
            .Select(node => node.ContentAs<NodeTypeDefinition>(options)?.UnreleasedBuildPath)
            .DistinctUntilChanged()
            .Select(path => string.IsNullOrEmpty(path)
                ? Observable.Never<string>()
                // RunAsSystem, never Observable.Using(ImpersonateAsSystem, …): that shape leaves the
                // subscriber impersonated (#1790).
                : accessService.RunAsSystem(() => workspace
                    .GetQuery($"nodetype-releases:{hubPath}",
                        $"path:{releaseNamespace} scope:children select:path")
                    .Where(items => items.Any(n =>
                        string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)))
                    .Take(1)
                    .Select(_ => path!)))
            .Switch();
    }

    /// <summary>
    /// Installs the watch on a per-NodeType hub, next to the compile and release-request watchers.
    /// Returns the subscription so the caller can register it for disposal with the hub.
    ///
    /// <para>🚨 The owner write is PART of the watched chain, not a side subscription in the
    /// callback (review on #5694). A faulted write then faults the watcher, whose re-establish
    /// re-derives the stamp and the listing and retries the adoption. A write subscribed from the
    /// callback could only log: the landing had already been consumed, the stamp had not changed,
    /// and nothing would ever ask again. And the write runs under SYSTEM, captured at the write
    /// boundary: the callback thread of a synced-query emission carries no identity, and
    /// <c>PostPipeline</c> fails closed without one.</para>
    /// </summary>
    /// <param name="hub">The per-NodeType hub.</param>
    /// <param name="workspace">Its workspace.</param>
    public static IDisposable Install(IMessageHub hub, IWorkspace workspace)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.CompileWatcher");
        var hubPath = hub.Address.Path;
        var ownStream = workspace.GetMeshNodeStream();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;

        return ActivityControlPlaneExtensions.SubscribeHubWatcher(
            hub,
            () => Landings(workspace, ownStream, accessService, hubPath, options)
                .SelectMany(landed => accessService.RunAsSystem(() => workspace.GetMeshNodeStream()
                    .Update(current =>
                        Adopt(current.ContentAs<NodeTypeDefinition>(options), landed) is { } adopted
                            ? current with { Content = adopted }
                            : current))
                    .Take(1)
                    // Report only a write that ADOPTED — a no-op write (the stamp moved on) is silent.
                    .Where(node => string.Equals(
                        node.ContentAs<NodeTypeDefinition>(options)?.LatestReleasePath, landed,
                        StringComparison.Ordinal))
                    .Select(_ => landed)),
            landed => logger?.LogInformation(
                "[ReleasePostCondition] {HubPath}: the release at {ReleasePath} landed after the "
                + "settle stopped waiting for it. Adopted: latestReleasePath names it and "
                + "unreleasedBuildPath is cleared (#5057).",
                hubPath, landed),
            logger,
            $"[LateReleaseAdoption] {hubPath}");
    }
}
