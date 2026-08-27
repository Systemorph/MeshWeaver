using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Helpers that produce an <see cref="IDisposable"/> identity scope from
/// a piece of state — typically a <see cref="MeshNode"/> that triggered a
/// hub-init watcher's Subscribe callback. Watchers fire on schedulers that
/// don't carry the originating user's <see cref="AccessService.Context"/>
/// (workspace emission, TaskPool), so any write the watcher performs needs
/// an explicit re-stamp of identity before it posts.
///
/// <para>This helper centralises the lookup so watchers stop hand-rolling
/// the equivalent <c>accessService.SetContext(new AccessContext { ObjectId = thread.CreatedBy, ... })</c>
/// pattern (no restore — leaks). Each call returns a scoped disposable that
/// restores the previous AsyncLocal value on Dispose.</para>
///
/// <para>🚨 <b>NEVER as an <c>Observable.Using</c> resource factory.</b>
/// <c>Observable.Using(() =&gt; AccessContextScope.AsSystem(access), _ =&gt; work)</c>
/// is the identity-latch defect (#1444/#1790) — these factories are
/// <c>ImpersonateAsSystem</c> / <c>SwitchAccessContext</c> under another name,
/// so wearing this helper's name changes nothing about the shape. Rx runs the
/// factory on the SUBSCRIBING thread and disposes it when the inner observable
/// TERMINATES — for a cross-hub read or write, the owning hub's response thread
/// — so the subscriber is left running as the impersonated identity with
/// nothing to restore it. Use
/// <c>ImpersonationScopeExtensions.RunAsSystem/RunAsHub/RunAs</c>, which owns
/// both ends inside one <c>Subscribe</c>; where the capture is genuinely
/// SYNCHRONOUS, a plain <c>using</c> around the call and its
/// <c>Subscribe</c> is equally correct — that is what these factories are for.
/// <c>ImpersonationScopeSiteRatchetGuard</c> fails a new site of either
/// spelling.</para>
///
/// <para><b>Operation classes — choose the right factory:</b></para>
/// <list type="bullet">
///   <item><see cref="FromNode"/> — for operations that should run under the
///   node's <em>owner</em>'s identity. Thread execution is the canonical
///   case: every read/write inside a round (drain pending, allocate
///   response cell, stream LLM output) MUST be attributed to the thread
///   owner. The access check happened at submit time (the user with no
///   thread access can't have submitted in the first place), so the round
///   inherits the trust the submit already verified.</item>
///
///   <item><see cref="AsSystem"/> — for framework infrastructure operations
///   that cross-cut all users and bypass access checks by design. NodeType
///   compilation is the canonical case: the access check is "is this user
///   allowed to request a recompile" (verified upfront when the user flips
///   <c>RequestedReleaseAt</c>); once requested, the compile activity runs
///   as <c>system-security</c> so it can read every source file, write the
///   activity log, and emit the compiled assembly without per-flag RLS
///   probing on every internal write.</item>
/// </list>
///
/// <para><b>Fallback policy for <see cref="FromNode"/>:</b> when the node
/// has no <see cref="MeshNode.CreatedBy"/> (framework-owned nodes whose
/// CreatedBy is null — file-system-imported seeds, factory-bootstrapped
/// roots), the scope falls back to system identity. Audit-logged at Debug
/// level so accidental system-impersonation of user-driven work surfaces
/// in trace logs.</para>
/// </summary>
public static class AccessContextScope
{
    /// <summary>
    /// Opens an identity scope built from <paramref name="node"/>'s
    /// <see cref="MeshNode.CreatedBy"/> (or
    /// <see cref="MeshNode.LastModifiedBy"/> if explicitly preferred — see
    /// the <paramref name="preferLastModified"/> flag). When neither is
    /// available, falls back to <see cref="AccessService.ImpersonateAsSystem"/>
    /// with a Debug-level audit log.
    ///
    /// <para>Use this for operations that should run as the node's owner —
    /// thread execution, satellite-write workflows, owner-attributed
    /// activities. NOT for compilation or other system infrastructure
    /// (see <see cref="AsSystem"/>).</para>
    /// </summary>
    public static IDisposable FromNode(
        MeshNode? node,
        AccessService? accessService,
        ILogger? logger = null,
        bool preferLastModified = false)
    {
        if (accessService is null) return EmptyDisposable.Instance;

        var principalId = preferLastModified
            ? (node?.LastModifiedBy ?? node?.CreatedBy)
            : (node?.CreatedBy ?? node?.LastModifiedBy);

        if (!string.IsNullOrEmpty(principalId))
        {
            return accessService.SwitchAccessContext(new AccessContext
            {
                ObjectId = principalId,
                Name = principalId
            });
        }

        logger?.LogDebug(
            "[AccessContextScope.FromNode] Falling back to system identity " +
            "for node {Path} (CreatedBy={CreatedBy}, LastModifiedBy={LastModifiedBy})",
            node?.Path ?? "(null)",
            node?.CreatedBy ?? "(null)",
            node?.LastModifiedBy ?? "(null)");
        return accessService.ImpersonateAsSystem();
    }

    /// <summary>
    /// Opens a system-identity scope. Compilation, persistence hydration,
    /// SyncStream heartbeats, and similar framework infrastructure that
    /// must read/write across every user's data should run inside this
    /// scope.
    ///
    /// <para>The access check that gates this operation MUST happen
    /// upstream (e.g. before the compile is dispatched, the user's
    /// <c>RequestedReleaseAt</c> flip is permission-checked by the owning
    /// hub's RLS). Once we're inside this scope, all internal writes
    /// bypass RLS — that's the contract.</para>
    ///
    /// <para>Returns an <see cref="EmptyDisposable"/> if
    /// <paramref name="accessService"/> is null (minimal test fixtures);
    /// callers should treat that as a no-op.</para>
    /// </summary>
    public static IDisposable AsSystem(AccessService? accessService) =>
        accessService is null
            ? (IDisposable)EmptyDisposable.Instance
            : accessService.ImpersonateAsSystem();

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
