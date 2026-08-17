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

        return Observable.Defer(() =>
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
        });
    }
}
