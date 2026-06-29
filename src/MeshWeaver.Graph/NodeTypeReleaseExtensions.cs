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
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(nodeTypePath))
        {
            onError?.Invoke("RequestNodeTypeRelease requires a NodeType path.");
            return;
        }

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeTypeReleaseExtensions");

        // Capture the caller's FULL AccessContext synchronously, here on the calling thread —
        // before CheckPermission's reactive chain can hop schedulers (PermissionEvaluator reads
        // through a TaskPool-scheduled synced query, and AsyncLocal does NOT flow through that
        // hop). We re-establish this exact context around the trigger write below so the flip's
        // PatchDataRequest always runs under the caller's identity (it needs Update on the node),
        // and stamp RequestedReleaseBy for the owner-attributed release-node creation.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var callerContext = accessService?.Context ?? accessService?.CircuitContext;
        var userId = callerContext?.ObjectId;

        var check = string.IsNullOrEmpty(userId)
            ? hub.CheckPermission(nodeTypePath, Permission.Compile)
            : hub.CheckPermission(nodeTypePath, userId, Permission.Compile);

        check
            .Take(1)
            .Subscribe(
                granted =>
                {
                    if (!granted)
                    {
                        logger?.LogInformation(
                            "[RequestNodeTypeRelease] Refused: user '{User}' lacks Compile on '{Path}'",
                            userId ?? "(anonymous)", nodeTypePath);
                        onError?.Invoke(
                            "You need the Compile permission (Editor or above) to create a release.");
                        return;
                    }

                    var triggerAt = DateTimeOffset.UtcNow;
                    // Re-establish the caller's identity for the write (it may have been lost to a
                    // scheduler hop in CheckPermission). The Subscribe runs synchronously inside the
                    // scope, so the PatchDataRequest captures the caller — never the ambient hub/null.
                    using (callerContext is not null
                        ? accessService?.SwitchAccessContext(callerContext)
                        : null)
                        hub.GetWorkspace().GetMeshNodeStream(nodeTypePath).Update(curr =>
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
                        }).Subscribe(
                            _ => { },
                            ex =>
                            {
                                logger?.LogWarning(ex,
                                    "[RequestNodeTypeRelease] Trigger write failed for {Path}", nodeTypePath);
                                onError?.Invoke($"Failed to start the release: {ex.Message}");
                            });
                },
                ex =>
                {
                    logger?.LogWarning(ex,
                        "[RequestNodeTypeRelease] Permission check faulted for {Path}", nodeTypePath);
                    onError?.Invoke($"Failed to verify Compile permission: {ex.Message}");
                });
    }
}
