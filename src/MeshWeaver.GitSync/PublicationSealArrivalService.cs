using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// publication in the mesh as <c>Hosting/PlatformBuilds/&lt;source&gt;</c>. The held source is
/// released by that arrival: no poller, no watchdog, no resubscribe loop, no retry. Nothing here
/// recovers from a state that "shouldn't happen" — it consumes an event that already existed and
/// was simply not listened to.</para>
///
/// <para>🚨 <b>The LOGICAL feed, and the seam choice is the whole defence against an import
/// storm.</b> <see cref="IMeshChangeFeed"/> delivers once, in the process that performed the write;
/// <see cref="IMeshInvalidationFeed"/> deliberately delivers in EVERY replica, because cache
/// invalidation must run everywhere. Subscribing to the latter would have every replica launch its
/// own reconcile of the same sources, and the gate's idempotence only applies after one import has
/// written — so N replicas would each dispatch the same GitHub fetch. The logical feed is the seam
/// whose own contract names this case: <i>"Logical event consumers can send mail, RUN AN INSTANCE
/// SYNC or append an outbox entry and therefore must retain the publisher's single logical
/// delivery."</i> One announcement, one reconcile, fleet-wide.</para>
///
/// <para>…and within that one process the announcements are SERIALIZED through a subject whose
/// runs are <c>Concat</c>-ed, never run concurrently: two sources sealing seconds apart, or one
/// source announced twice, would otherwise have two reconciles reading the same pre-import config
/// and dispatching the same import twice. That is the house serialization channel, not a gate — no
/// <c>SemaphoreSlim</c>, no lock, nothing that can park a turn.</para>
///
/// <para>🚨 <b>It hands the reconciler <c>null</c>, not an empty declined-type set</b>
/// (MeshWeaver#4620). The paragraph here used to say the opposite, and the reasoning was half
/// right: this service does not take the adoption sweep's measurement, so claiming "nothing was
/// declined" would report a clean partition for a population it never looked at. What it missed is
/// that an EMPTY set says exactly that — the reconciler reads an at-the-seal source with nothing
/// declined as its STEADY STATE and moves nothing. So <c>ReconcileAtSealedCommit</c> could never
/// fire on this, the only post-boot trigger, and a partition left holding one file from each of two
/// trees stayed that way while its sync recorded success (measured in two of nineteen partitions on
/// memex.systemorph.com, 2026-09-17). Null means "I did not measure", and the reconciler then takes
/// the measurement itself, where the bundle inventory already is. Both arms can now fire.</para>
///
/// <para>🚨 <b>What it still deliberately does NOT do.</b> It never releases a source for ANOTHER
/// identity's publication: whether it should is the open design question #4063 names, and it is the
/// same question <c>Modules:VersionStrictness</c> answers for bundle adoption.</para>
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

    private readonly Subject<MeshChangeEvent> announcements = new();
    private IDisposable? subscription;
    private IDisposable? pump;

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
            // 🚨 Concat, so one reconcile finishes before the next starts. A burst of announcements
            // costs one reconcile each, in order, never two reading the same pre-import config.
            pump = announcements
                .Select(Reconcile)
                .Concat()
                .Subscribe(
                    _ => { },
                    ex => logger?.LogWarning(ex,
                        "[SealedSync] the publication-seal reconcile channel faulted — held sources "
                        + "will advance on the next boot"));
            if (hub.ServiceProvider.GetService<IMeshChangeFeed>() is { } feed)
                subscription = feed.Subscribe(OnChange);
            else
                logger?.LogDebug(
                    "[SealedSync] no IMeshChangeFeed is registered, so a publication sealed "
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
        pump?.Dispose();
        pump = null;
        announcements.Dispose();
    }

    private void OnChange(MeshChangeEvent change)
    {
        if (change.Kind == MeshChangeKind.Deleted || !IsPublicationAnnouncement(change.Path))
            return;
        announcements.OnNext(change);
    }

    private IObservable<int> Reconcile(MeshChangeEvent change)
    {
        var publishedRoot = configuration?[ShippedPrebuiltBundles.PublishedRootConfigKey];
        if (string.IsNullOrWhiteSpace(publishedRoot))
            return Observable.Return(0);
        if (hub.ServiceProvider.GetService<IPublicationSyncReconciler>() is not { } reconciler)
            return Observable.Return(0);
        // 🚨 The bounded pool is RESOLVED, never fallen back on. A share read on an unbounded pool
        // is invisible to IoPoolRegistry's teardown drain, so it can still be running after the
        // mesh scope and its collectible ALCs are gone — the exact straggler the pool exists to
        // prevent. No registry means this mesh is not composed the way this service needs, and the
        // honest answer is to do nothing and say so, never to run untracked I/O.
        if (hub.ServiceProvider.GetService<IoPoolRegistry>() is not { } pools)
        {
            logger?.LogWarning(
                "[SealedSync] no IoPoolRegistry is registered, so the publication announced at "
                + "{Path} cannot be read on a drained pool — not reading it. Held sources advance "
                + "on the next boot.", change.Path);
            return Observable.Return(0);
        }

        var identity = PrebuiltAssemblySeeder.LiveFrameworkMvid;
        var pool = pools.Get(IoPoolNames.FileSystem);
        var census = hub.ServiceProvider.GetService<SealedSyncCensus>();
        var access = hub.ServiceProvider.GetService<AccessService>();

        // RunAsSystem: a publication announcement carries no user, and the reads the reconcile
        // makes (the sync configs of every space) are RLS-filtered. Without it the reconcile would
        // see an empty config set and report "nothing to do" — a false clean, which is the one
        // shape #4063 must never grow more of.
        return access.RunAsSystem(() => pool
                .InvokeBlocking(_ => SealedPublicationIndex.ReadFor(publishedRoot, identity, logger))
                .Do(sealedForThisIdentity => census?.RecordPublication(new SealedPublicationReading(
                    identity, publishedRoot, [.. sealedForThisIdentity], DateTimeOffset.UtcNow)))
                .SelectMany(sealedForThisIdentity =>
                    // 🚨 `null`, not `[]` (MeshWeaver#4620). An empty set is a MEASUREMENT — "I
                    // compared the partition against the commit's tree and nothing had drifted" —
                    // and the reconciler reads it as the steady state and moves nothing. This
                    // trigger measures nothing of the kind: the declined set is a by-product of the
                    // boot sweep's bundle-adoption walk, which does not run here. Passing `[]`
                    // therefore asserted a clean partition on no evidence, and it was the ONLY
                    // post-boot path into the drift detector — so after boot the detector could
                    // never fire. Null says "I did not look", and the reconciler takes the
                    // measurement itself, where the bundle inventory already is.
                    reconciler.Reconcile(identity, sealedForThisIdentity, declinedTypePaths: null)))
            .Do(
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
                    + "{Path} failed — the sources stay where they are", change.Path))
            // A faulted reconcile must not tear down the channel: the next announcement is a fresh
            // fact and deserves a fresh attempt. This is not a retry — nothing re-runs the failed
            // read; it simply does not poison the subscription.
            .Catch<int, Exception>(_ => Observable.Return(0));
    }
}
