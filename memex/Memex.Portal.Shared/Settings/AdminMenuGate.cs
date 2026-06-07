using System.Reactive.Linq;
using System.Threading.Channels;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Gate for the platform-wide Admin menu (the GlobalSettings area). A tab is shown only to a viewer
/// holding <see cref="Permission.All"/> at the <c>Admin</c> scope — the platform-admin scope.
/// Bridges the <c>IObservable</c> permission check to <c>IAsyncEnumerable</c> via a Channel (no
/// <c>.ToTask()</c> on a hub round-trip — see AsynchronousCalls.md).
///
/// <para>The platform-admin grant is an AccessAssignment in the <c>Admin/_Access</c> namespace (routed to
/// the <c>admin</c> schema). The evaluator derives the assignment's scope from that namespace
/// (<c>Admin/_Access</c> → scope <c>Admin</c>, stripping <c>/_Access</c>), so the gate checks
/// <c>GetEffectivePermissions("Admin")</c>. The old check used root scope <c>""</c>, which matched no
/// grant once global-admin moved out of the root <c>_Access</c> namespace — so Invitations/Inbox never
/// appeared.</para>
///
/// <para>🚨 We must WAIT for the first emission that grants <see cref="Permission.All"/>, NOT snapshot
/// the first emission with <c>FirstAsync()</c>. The grant is a RUNTIME AccessAssignment row, so
/// <see cref="PermissionEvaluator.GetEffectivePermissions(IMessageHub, string, string)"/> emits an empty
/// static seed first and only enriches to <c>All</c> once the synced AccessAssignment query lands.
/// <c>FirstAsync()</c> would capture that premature empty → the gate ALWAYS returned false. Filtering for
/// the positive (<c>Where(isAdmin)</c>) with a bounded wait makes the answer correct: an admin's grant
/// arrives within the window; a non-admin never emits a positive and falls through to the safe default
/// (false / hide the menu). One-shot + disposed, so this is NOT the long-lived synced-state Timeout
/// anti-pattern.</para>
/// </summary>
internal static class AdminMenuGate
{
    /// <summary>The scope whose Admin grant designates a platform (global) admin. The evaluator derives
    /// scope from the AccessAssignment NAMESPACE (<c>Admin/_Access</c> → scope <c>Admin</c>) via an
    /// ordinal match, so this MUST match the stored namespace casing — <c>Admin</c>, not <c>admin</c>.</summary>
    private const string AdminScope = "Admin";

    // Bounded wait for the admin grant to surface on the live permission stream. The grant is typically
    // present in the first enriched emission; this is the ceiling for the synced query's cold-start,
    // after which a non-admin (no positive ever emitted) resolves to "not admin".
    private static readonly TimeSpan GrantWait = TimeSpan.FromSeconds(5);

    public static async Task<bool> IsPlatformAdminAsync(LayoutAreaHost host)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        if (string.IsNullOrEmpty(viewerId))
            return false;

        var channel = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

        // Only a POSITIVE (root All) completes the channel early — premature empty/None emissions
        // during the synced-query cold start are filtered out, so we never decide "not admin" off a
        // not-yet-loaded snapshot.
        using var sub = host.Hub.GetEffectivePermissions(AdminScope, viewerId)
            .Select(perm => perm.HasFlag(Permission.All))
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Subscribe(
                _ => { channel.Writer.TryWrite(true); channel.Writer.TryComplete(); },
                _ => channel.Writer.TryComplete(),
                () => channel.Writer.TryComplete());

        // Bounded wait: an admin's grant arrives within GrantWait; a non-admin never emits a positive,
        // so the timeout fires and we fall through to the safe default below.
        using var cts = new CancellationTokenSource(GrantWait);
        try
        {
            await foreach (var isAdmin in channel.Reader.ReadAllAsync(cts.Token))
                return isAdmin;
        }
        catch (OperationCanceledException)
        {
            // No positive within the window → treat as not root admin.
        }
        return false;
    }
}
