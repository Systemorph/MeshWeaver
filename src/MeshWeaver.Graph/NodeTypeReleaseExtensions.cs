using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The canonical, permission-gated entry point for the user-facing "Create Release"
/// operation. Every caller — the GUI button on the NodeType Configuration pane, MCP
/// agents, tests — goes through <see cref="RequestNodeTypeRelease"/>; there is no other
/// surface for a user to author a release.
///
/// <para>Two forms, ONE implementation: <see cref="ObserveNodeTypeRelease"/> returns the cold
/// <c>IObservable&lt;bool&gt;</c> and is what a caller composes against when it must ORDER other
/// work around the flip landing; <see cref="RequestNodeTypeRelease"/> is that observable plus a
/// Subscribe, for the click handlers that have nothing to sequence.</para>
///
/// <para><b>The credential split this entry point enforces:</b></para>
/// <list type="number">
///   <item><b>Authorization is the USER's.</b> Creating a release is a privileged user
///     action gated by <see cref="Permission.Compile"/> (Space editors hold it by default).
///     This method checks the CALLER has <c>Compile</c> on the target NodeType and refuses
///     cleanly — no release, <c>onError</c> invoked — when they don't. The
///     request is stamped with the caller's id (<see cref="NodeTypeDefinition.RequestedReleaseBy"/>)
///     so the resulting <c>Release</c> MeshNode is attributable to its author (owner = caller).</item>
///   <item><b>Execution is the SYSTEM's.</b> The actual compilation that fills the assembly
///     cache (Roslyn, status write-back, compile <c>_Activity</c>) runs under
///     <c>accessService.ImpersonateAsSystem()</c> in the per-NodeType hub's compile watcher —
///     NOT the caller — so it succeeds even on a partition the caller cannot write
///     (the read-only <c>Doc</c> partition is the canonical case). See
///     <c>NodeTypeCompilationHelpers.RunCompile</c> and <c>AccessContextScope.AsSystem</c>.</item>
/// </list>
///
/// <para>The release is ATOMIC: the per-NodeType hub creates the <c>Release</c> MeshNode only
/// after a SUCCESSFUL compile (<c>RunCompile</c>'s <c>ok</c> branch). A compile failure leaves
/// <c>CompilationStatus = Error</c> and no <c>Release</c> node — never a partial release.</para>
///
/// <para>Mutation flows through the canonical <c>stream.Update</c> trigger
/// (<see cref="NodeTypeDefinition.RequestedReleaseAt"/>) — no verb-shaped
/// <c>CreateReleaseRequest</c>. The per-NodeType hub's <c>InstallReleaseRequestWatcher</c>
/// observes the timestamp moving past <c>LastReleaseRequestHandledAt</c> and flips
/// <c>CompilationStatus = Pending</c>; <c>InstallCompileWatcher</c> runs Roslyn from there.</para>
/// </summary>
public static class NodeTypeReleaseExtensions
{
    /// <summary>
    /// 🚨 The ORDERED inner bound on ONE release request — issue #3510.
    ///
    /// <para><b>What it is for.</b> <see cref="ObserveNodeTypeRelease"/> promises, in its own
    /// remarks and in its closing <c>DefaultIfEmpty(false)</c>, that it produces EXACTLY ONE
    /// emission, always. That promise covered three of Rx's four outcomes — it emits, or it faults,
    /// or it completes empty. The fourth is the one that bites: a source that NEITHER emits NOR faults
    /// NOR completes. <c>DefaultIfEmpty</c> cannot see it and <c>Catch</c> cannot see it; only a
    /// deadline can. The composed leg now carries one, so the promise is true by construction.</para>
    ///
    /// <para><b>What it costs when it is missing.</b> The package installer's release wave is
    /// <c>nodeTypePaths.Select(ObserveNodeTypeRelease).Merge().ToList()</c> — one non-terminating leg
    /// parks the ENTIRE install, silently, until the CD bake+seal gate's own 600 s
    /// <c>InstallTimeout</c> reports <c>install: TimeoutException</c> against a package that
    /// finished writing its nodes eight minutes earlier. That is #3510's measured signature (core CD
    /// 7976: <c>Installed node-repo plugin Hosting: 144 written</c> at 05:45:28Z, the install's
    /// deferred release wave at 05:46:04Z, then NOT ONE log line and NOT ONE pending callback
    /// anywhere for eight minutes, and no <c>warmed installed root Hosting</c> — the step that
    /// follows the wave — ever printed), and it was named in advance by
    /// <c>MeshNodeStreamHandle.BaseStateSource</c>'s remarks: "no per-leg bound and no outer bound".
    /// Roughly one CD run in two lost its seal to it.</para>
    ///
    /// <para>🚨 <b>Ordered, not tightened</b> (Doc/Architecture/BoundsMustBeOrdered). It sits ABOVE
    /// every bound the write it wraps already carries — <c>BaseStateWaitBound</c> 30 s, then up to
    /// three verdict windows of <c>LateResponseWatchBound</c> + <c>VerdictBoundGrace</c> = 31 s
    /// apiece across <c>MaxConflictRetries</c>, ≈124 s in total — so it can only ever fire when
    /// something is genuinely non-terminating, never when a write is merely slow. And it sits far
    /// BELOW the installer's 600 s bound, which is the point: the outer bound knows only that the
    /// install did not finish, while this one knows WHICH NodeType never answered and says so.
    /// Nothing here is retried, nothing is swallowed and no existing bound moves.</para>
    /// </summary>
    internal static readonly TimeSpan ReleaseRequestBound = TimeSpan.FromSeconds(180);

    /// <summary>
    /// The release leg's totality, as a PURE composition — no hub, no mesh, no wall clock — so the
    /// property it guarantees is drivable from a <c>TestScheduler</c>
    /// (<c>ReleaseWaveLegIsTotalTest</c>). <paramref name="leg"/> is the permission check and the
    /// trigger write composed exactly as <see cref="ObserveNodeTypeRelease"/> composes them; this
    /// adds the one thing that composition cannot express about itself: an answer when the leg
    /// produces none.
    ///
    /// <para><c>Take(1)</c> comes FIRST on purpose. Rx's <c>Timeout(TimeSpan)</c> is an
    /// INTER-EMISSION deadline that restarts on every <c>OnNext</c> it sees, so a chatty source
    /// resets it forever — the exact defect <c>BaseStateSource</c> was rewritten to fix. Reducing
    /// the sequence to at most one emission first makes the deadline a TOTAL one.</para>
    /// </summary>
    /// <param name="leg">The composed permission-check-then-write sequence for one NodeType.</param>
    /// <param name="nodeTypePath">Path of the NodeType — named in the refusal, so a parked leg is
    /// attributable from one log line instead of a full bake-log read.</param>
    /// <param name="bound">The ordered deadline; <see cref="ReleaseRequestBound"/> in production.</param>
    /// <param name="onError">The caller's refusal sink — invoked with the same reason that is logged.</param>
    /// <param name="report">Warning sink (the logger in production), given the reason.</param>
    /// <param name="scheduler">Timer seam; <see cref="Scheduler.Default"/> in production.</param>
    internal static IObservable<bool> BoundReleaseLeg(
        IObservable<bool> leg,
        string nodeTypePath,
        TimeSpan bound,
        Action<string>? onError,
        Action<string>? report,
        IScheduler? scheduler = null)
        => leg
            .Take(1)
            .Timeout(
                bound,
                Observable.Defer(() =>
                {
                    var reason =
                        $"The release request for '{nodeTypePath}' produced no answer within "
                        + $"{bound.TotalSeconds:0}s — neither the Compile check nor the trigger write "
                        + "emitted, faulted or completed. Treating it as NOT released so the caller "
                        + "is answered; the release did not happen and this NodeType keeps the "
                        + "assembly it already had.";
                    report?.Invoke(reason);
                    onError?.Invoke(reason);
                    return Observable.Return(false);
                }),
                scheduler ?? Scheduler.Default);

    /// <summary>
    /// Request a release of the NodeType at <paramref name="nodeTypePath"/>. Checks the caller
    /// holds <see cref="Permission.Compile"/> on the target; on success flips the
    /// <see cref="NodeTypeDefinition.RequestedReleaseAt"/> trigger (stamped with the caller's
    /// identity); on denial invokes <paramref name="onError"/> with a clear message and does
    /// NOTHING else — no trigger, no release.
    /// </summary>
    /// <param name="hub">The hub whose AccessContext identifies the caller.</param>
    /// <param name="nodeTypePath">Path of the NodeType to release.</param>
    /// <param name="force">When <c>true</c>, bypass the "sources unchanged since last compile"
    /// short-circuit and always run a fresh compile (the "Up to Date" button path).</param>
    /// <param name="releaseNotes">Optional markdown release notes to stamp alongside the trigger.
    /// When <c>null</c>, whatever the author already auto-saved on
    /// <see cref="NodeTypeDefinition.ReleaseNotes"/> is used.</param>
    /// <param name="onError">Invoked (with a human-readable reason) when the caller lacks
    /// <c>Compile</c> or the trigger write fails — the clean refusal path.</param>
    public static void RequestNodeTypeRelease(
        this IMessageHub hub,
        string nodeTypePath,
        bool force = false,
        string? releaseNotes = null,
        Action<string>? onError = null)
        => hub.ObserveNodeTypeRelease(nodeTypePath, force, releaseNotes, onError)
            .Subscribe(_ => { });

    /// <summary>
    /// The OBSERVABLE form of <see cref="RequestNodeTypeRelease"/>: identical semantics, but the
    /// caller learns when the trigger has actually LANDED and can therefore ORDER other work
    /// against it. Emits <c>true</c> once the <see cref="NodeTypeDefinition.RequestedReleaseAt"/>
    /// flip has been written, <c>false</c> when the caller was refused
    /// (<see cref="Permission.Compile"/>) or the write failed — the same outcomes
    /// <paramref name="onError"/> reports. Never faults: a refusal is an answer, not a fault, and
    /// a caller batching many releases must not lose the rest to one of them.
    ///
    /// <para>🚨 This exists because a fire-and-forget release is UNORDERABLE. The package
    /// installer used to launch every installed type's compile through the void overload and then,
    /// in the same continuation chain, recycle the package ROOT hub those very compiles read
    /// (<c>ValidateCellSurfaceSingleHome</c> → <c>GetMeshNode('&lt;packageRoot&gt;')</c>) — a
    /// teardown deliberately raced against work the same code path had just started, and nothing
    /// in the chain could sequence the two because the release side returned nothing (#1732). The
    /// installer now composes on this observable instead.</para>
    /// </summary>
    /// <param name="hub">The hub whose AccessContext identifies the caller.</param>
    /// <param name="nodeTypePath">Path of the NodeType to release.</param>
    /// <param name="force">When <c>true</c>, bypass the "sources unchanged since last compile"
    /// short-circuit and always run a fresh compile (the "Up to Date" button path).</param>
    /// <param name="releaseNotes">Optional markdown release notes to stamp alongside the trigger.
    /// When <c>null</c>, whatever the author already auto-saved on
    /// <see cref="NodeTypeDefinition.ReleaseNotes"/> is used.</param>
    /// <param name="onError">Invoked (with a human-readable reason) when the caller lacks
    /// <c>Compile</c> or the trigger write fails — the clean refusal path.</param>
    /// <returns>A COLD observable; Subscribe to request the release.</returns>
    public static IObservable<bool> ObserveNodeTypeRelease(
        this IMessageHub hub,
        string nodeTypePath,
        bool force = false,
        string? releaseNotes = null,
        Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(nodeTypePath))
        {
            onError?.Invoke("RequestNodeTypeRelease requires a NodeType path.");
            return Observable.Return(false);
        }

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeTypeReleaseExtensions");

        // 🚨 #3510 — the leg is composed here and made TOTAL by BoundReleaseLeg below. Everything
        // between this Defer and the closing DefaultIfEmpty answers the caller for three of Rx's
        // four outcomes; the fourth — a source that never terminates at all — is what parked the
        // installer's whole release wave and, with it, the CD seal. See ReleaseRequestBound.
        return BoundReleaseLeg(
            Observable.Defer(() =>
        {
            // Capture the caller's FULL AccessContext synchronously, on the SUBSCRIBING thread —
            // before CheckPermission's reactive chain can hop schedulers (PermissionEvaluator reads
            // through a TaskPool-scheduled synced query, and AsyncLocal does NOT flow through that
            // hop). We re-establish this exact context around the trigger write below so the flip's
            // PatchDataRequest always runs under the caller's identity (it needs Update on the node),
            // and stamp RequestedReleaseBy for the owner-attributed release-node creation.
            // Defer, not the enclosing method body: a cold observable's identity is fixed at
            // Subscribe, and the installer subscribes inside its own System impersonation scope.
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            var callerContext = accessService?.Context ?? accessService?.CircuitContext;
            var userId = callerContext?.ObjectId;

            var check = string.IsNullOrEmpty(userId)
                ? hub.CheckPermission(nodeTypePath, Permission.Compile)
                : hub.CheckPermission(nodeTypePath, userId, Permission.Compile);

            return check
                .Take(1)
                .SelectMany(granted =>
                {
                    if (!granted)
                    {
                        logger?.LogInformation(
                            "[RequestNodeTypeRelease] Refused: user '{User}' lacks Compile on '{Path}'",
                            userId ?? "(anonymous)", nodeTypePath);
                        onError?.Invoke(
                            "You need the Compile permission (Editor or above) to create a release.");
                        return Observable.Return(false);
                    }

                    var triggerAt = DateTimeOffset.UtcNow;
                    // Re-establish the caller's identity for the write (it may have been lost to a
                    // scheduler hop in CheckPermission). Update is COLD, so the scope has to be
                    // alive when it is SUBSCRIBED, not merely when it is composed — Observable.Using
                    // is what guarantees that (the old shape subscribed synchronously inside a
                    // `using`, which this SelectMany continuation no longer is).
                    return Observable.Using(
                            () => (callerContext is not null
                                ? accessService?.SwitchAccessContext(callerContext)
                                : null) ?? Disposable.Empty,
                            _ => hub.GetWorkspace().GetMeshNodeStream(nodeTypePath).Update(curr =>
                            {
                                if (curr?.Content is not NodeTypeDefinition def) return curr!;
                                return curr with
                                {
                                    Content = def with
                                    {
                                        RequestedReleaseAt = triggerAt,
                                        RequestedReleaseForce = force,
                                        RequestedReleaseBy = userId,
                                        ReleaseNotes = releaseNotes ?? def.ReleaseNotes
                                    }
                                };
                            }))
                        .Take(1)
                        .Select(_ => true)
                        .Catch((Exception ex) =>
                        {
                            logger?.LogWarning(ex,
                                "[RequestNodeTypeRelease] Trigger write failed for {Path}", nodeTypePath);
                            onError?.Invoke($"Failed to start the release: {ex.Message}");
                            return Observable.Return(false);
                        });
                })
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "[RequestNodeTypeRelease] Permission check faulted for {Path}", nodeTypePath);
                    onError?.Invoke($"Failed to verify Compile permission: {ex.Message}");
                    return Observable.Return(false);
                })
                // EXACTLY one emission, always — a permission source that completes without
                // answering must not turn into a sequence that completes without answering, or a
                // caller composing `.FirstAsync()` on it faults instead of learning "no release".
                .DefaultIfEmpty(false);
            }),
            nodeTypePath,
            ReleaseRequestBound,
            onError,
            reason => logger?.LogWarning(
                "[RequestNodeTypeRelease] {Path}: {Reason}", nodeTypePath, reason));
    }
}
