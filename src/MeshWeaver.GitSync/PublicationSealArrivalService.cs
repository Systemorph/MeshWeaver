using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 <b>The seal-triggered reconcile, armed for the WHOLE process lifetime instead of only at
/// boot</b> (Systemorph/MeshWeaver#4063, convergence item 1).
///
/// <para><b>Why "it advances on the next green build" is false, and that is the defect.</b> The
/// <c>workflow_run</c> hook fires when a repository's build goes green, which is BEFORE its
/// publish-bake job seals the bundles for this instance's framework identity — so
/// <see cref="SealedSyncGate"/> correctly HOLDS. When the seal lands minutes later, nothing
/// re-evaluates it. And the NEXT green build does not rescue it either: that delivery asks whether
/// the seal is at the NEW head sha, which it is not, so it is held in turn. A repository whose
/// bake seals after its webhook therefore <b>never advances inside one process lifetime</b> — only
/// a restart, which is what runs <c>ShippedPrebuiltBundles.SeedPublishedRoot</c> and with it the
/// only existing call of <see cref="IPublicationSyncReconciler"/>. That is the whole of why nine
/// hours of tagged releases sat undelivered on two production portals on 2026-09-12.</para>
///
/// <para><b>The trigger is the FACT, never a timer.</b> The publishing lane announces each sealed
/// publication in the mesh as <c>Hosting/PlatformBuilds/&lt;source&gt;</c> — a node write, relayed
/// post-commit into EVERY replica through <see cref="IMeshInvalidationFeed"/> (durable backends
/// relay their cross-process notifications into it, so a write served by one replica reaches all of
/// them). So the held source is released by the seal's own arrival: no poller, no watchdog, no
/// resubscribe loop, no retry. Nothing here recovers from a state that "shouldn't happen" — it
/// consumes an event that already exists and was simply not listened to.</para>
///
/// <para>🚨 <b>What it deliberately does NOT do.</b> It hands the reconciler an EMPTY declined-type
/// set, so only <c>SealedSyncReconcile.Action.ImportAtSealedCommit</c> can fire. The
/// <c>ReconcileAtSealedCommit</c> arm exists for types the adoption sweep declined on their source
/// fingerprint, and that measurement belongs to the sweep — claiming it here would report "nothing
/// was declined" for a population this service never looked at. It also never releases a source for
/// ANOTHER identity's publication: whether it should is the open design question #4063 names, and
/// it is the same question <c>Modules:VersionStrictness</c> answers for bundle adoption.</para>
///
/// <para>Idempotent by construction, so a burst of announcements costs at most a re-read: the
/// reconciler imports only where the source is BEHIND the sealed commit, and a source already at it
/// with nothing declined is its steady state — neither logged nor recorded.</para>
/// </summary>
internal sealed class PublicationSealArrivalService(
    IMessageHub hub,
    IConfiguration? configuration = null,
    ILogger<PublicationSealArrivalService>? logger = null) : IHostedService, IDisposable
{
    /// <summary>
    /// The mesh path prefix the publishing lane announces each sealed publication under, as
    /// <see cref="FrameworkBroadcastOptions.PlatformBuildsTarget"/> defines it. A trailing
    /// separator, so the folder node itself is not an announcement of anything.
    /// </summary>
    private static readonly string AnnouncementPrefix =
        FrameworkBroadcastOptions.PlatformBuildsTarget + "/";

    private IDisposable? subscription;

    /// <summary>Whether a committed change is a publication announcement. Pure, so the predicate
    /// that decides whether anything happens at all is testable without a mesh.</summary>
    /// <param name="path">The changed node's path.</param>
    /// <returns>True when the path names a publication announcement.</returns>
    internal static bool IsPublicationAnnouncement(string? path) =>
        path is { Length: > 0 }
        && path.StartsWith(AnnouncementPrefix, StringComparison.OrdinalIgnoreCase)
        && path.Length > AnnouncementPrefix.Length;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A watcher that cannot be armed must never fail host startup: the worst case is the
        // pre-fix world (a held source waits for a restart), never a host that will not boot.
        try
        {
            if (hub.ServiceProvider.GetService<IMeshInvalidationFeed>() is { } feed)
                subscription = feed.Subscribe(OnChange);
            else
                logger?.LogDebug(
                    "[SealedSync] no IMeshInvalidationFeed is registered, so a publication sealed "
                    + "after a green build cannot release its sources until the next boot");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "[SealedSync] could not arm the publication-seal watcher — held sources will "
                + "advance on the next boot, as they did before MeshWeaver#4063");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        subscription?.Dispose();
        subscription = null;
    }

    private void OnChange(MeshChangeEvent change)
    {
        if (change.Kind == MeshChangeKind.Deleted || !IsPublicationAnnouncement(change.Path))
            return;
        var publishedRoot = configuration?[ShippedPrebuiltBundles.PublishedRootConfigKey];
        if (string.IsNullOrWhiteSpace(publishedRoot))
            return;
        if (hub.ServiceProvider.GetService<IPublicationSyncReconciler>() is not { } reconciler)
            return;

        var identity = PrebuiltAssemblySeeder.LiveFrameworkMvid;
        // 🚨 The seal index is a SHARE read — blocking file I/O on whatever thread published the
        // change. It goes through the mesh's bounded pool like every other I/O leaf; running it
        // inline would put an unbounded share read on the notification path.
        var pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
                   ?? IoPool.Unbounded;
        var census = hub.ServiceProvider.GetService<SealedSyncCensus>();
        var access = hub.ServiceProvider.GetService<AccessService>();

        // RunAsSystem: a publication announcement carries no user, and the reads the reconcile
        // makes (the sync configs of every space) are RLS-filtered. Without it the reconcile would
        // see an empty config set and report "nothing to do" — a false clean, which is the one
        // shape #4063 must never grow more of.
        access.RunAsSystem(() => pool
                .InvokeBlocking(_ => SealedPublicationIndex.ReadFor(publishedRoot, identity, logger))
                .Do(sealedForThisIdentity => census?.RecordPublication(new SealedPublicationReading(
                    identity, publishedRoot, [.. sealedForThisIdentity], DateTimeOffset.UtcNow)))
                .SelectMany(sealedForThisIdentity =>
                    reconciler.Reconcile(identity, sealedForThisIdentity, [])))
            .Subscribe(
                dispatched =>
                {
                    if (dispatched > 0)
                        logger?.LogInformation(
                            "[SealedSync] the publication announced at {Path} released {Dispatched} "
                            + "held sync source(s) for framework identity {Identity} — without it they "
                            + "would have waited for the next boot (MeshWeaver#4063).",
                            change.Path, dispatched, identity);
                },
                ex => logger?.LogWarning(ex,
                    "[SealedSync] reconciling the sync sources after the publication announced at "
                    + "{Path} failed — the sources stay where they are", change.Path));
    }
}
