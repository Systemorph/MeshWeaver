using System;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Boundary tests for the sanctioned <c>cache/mesh-node-cache</c> identity.
/// Documented in <c>Doc/Architecture/AccessContextPropagation.md</c> →
/// "Sanctioned exceptions — fine-grained, exact, controlled".
///
/// <para>The cache identity is internal to <c>MeshWeaver.Hosting</c>. Tests
/// here reference the address as a string literal — that mirrors what any
/// caller outside the cache assembly would have to do, and the tests prove
/// that even a forged impersonation gets exactly the narrow permissions the
/// cache needs (Read) and nothing else (Create / Update / Delete must fail
/// with <see cref="UnauthorizedAccessException"/>).</para>
///
/// <para>If any of these tests start passing in unexpected directions
/// (e.g. a write under the cache identity succeeds), a code change has
/// silently widened the grant — break the build before the privilege
/// escalation reaches prod.</para>
/// </summary>
public class MeshNodeCacheIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The dedicated cache identity. Mirrors the <c>internal const</c>
    /// in <c>MeshWeaver.Hosting/MeshNodeCacheIdentity.cs</c>. Using the literal
    /// here is deliberate — it documents the public contract any external
    /// caller would have to forge.</summary>
    private const string CacheIdentityAddress = "cache/mesh-node-cache";

    private static readonly AccessContext CacheContext = new()
    {
        ObjectId = CacheIdentityAddress,
        Name = CacheIdentityAddress
    };

    private CancellationToken TestTimeout => new CancellationTokenSource(15.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    /// <summary>
    /// Under the cache identity, <c>SecurityService.GetEffectivePermissions</c>
    /// returns exactly <see cref="Permission.Read"/> — no more, no less.
    /// The whole boundary turns on this single grant being narrow.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CacheIdentity_HasOnlyReadPermission()
    {
        // hub is Mesh — permission checks use hub.GetEffectivePermissions

        var permissions = await Mesh.GetEffectivePermissions("any/path", CacheIdentityAddress)
            .Take(1)
            .Timeout(5.Seconds())
            .ToTask(TestTimeout);

        permissions.Should().Be(Permission.Read,
            "cache identity is sanctioned for hydration reads only — Create / Update / " +
            "Delete must NOT be granted, otherwise a write under this identity would " +
            "succeed and silently widen the cache's bypass into full system access.");
    }

    /// <summary>
    /// <see cref="Permission.HasFlag"/> spot-checks on the same response: the
    /// only flag that survives is Read. Documents the exact shape for future
    /// maintainers.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CacheIdentity_FlagBreakdown()
    {
        // hub is Mesh — permission checks use hub.GetEffectivePermissions
        var permissions = await Mesh.GetEffectivePermissions("any/path", CacheIdentityAddress)
            .Take(1).Timeout(5.Seconds()).ToTask(TestTimeout);

        permissions.HasFlag(Permission.Read).Should().BeTrue(
            "Read is the whole point of the cache identity — hydration needs it");
        permissions.HasFlag(Permission.Create).Should().BeFalse(
            "cache identity must NOT have Create — it is a read-only hydrator");
        permissions.HasFlag(Permission.Update).Should().BeFalse(
            "cache identity must NOT have Update — writes go through user identity");
        permissions.HasFlag(Permission.Delete).Should().BeFalse(
            "cache identity must NOT have Delete");
        permissions.HasFlag(Permission.Comment).Should().BeFalse(
            "cache identity must NOT have Comment");
        permissions.HasFlag(Permission.Execute).Should().BeFalse(
            "cache identity must NOT have Execute — the narrow grant is the whole security model");
    }

    /// <summary>
    /// Creating a node under the cache identity must fail with
    /// <see cref="UnauthorizedAccessException"/>. The error message must
    /// indicate authorization failure (not silently succeed-then-fail later).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CacheIdentity_CreateNode_IsDenied()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var nodePath = $"CacheIdProbe/Create_{Guid.NewGuid().AsString()}";
        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Forged write under cache identity",
            NodeType = "Markdown"
        };

        using (accessService.SwitchAccessContext(CacheContext))
        {
            var act = async () => await meshService.CreateNode(node)
                .Take(1).Timeout(10.Seconds()).ToTask(TestTimeout);

            var ex = await act.Should().ThrowAsync<UnauthorizedAccessException>();
            ex.Which.Message.Should().Contain("Access denied",
                "the denial must surface as an authorization failure, not a generic exception");
        }

        // Belt-and-braces: ensure the node was never persisted.
        TestUsers.DevLogin(Mesh);
        var probe = await meshService
            .ObserveQuery<MeshNode>(new MeshQueryRequest { Query = $"path:{nodePath}", Limit = 1 })
            .Take(1).Timeout(5.Seconds())
            .ToTask(TestTimeout);
        probe.Items.Should().BeEmpty(
            "the forged create must not have left any node behind even partially");
    }

    /// <summary>
    /// Update under the cache identity must fail. Set up: admin creates a
    /// node legitimately, then we forge cache identity and try to mutate it.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CacheIdentity_UpdateNode_IsDenied()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        TestUsers.DevLogin(Mesh);

        var nodePath = $"CacheIdProbe/Update_{Guid.NewGuid().AsString()}";
        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Original (admin-created)",
            NodeType = "Markdown"
        };
        await meshService.CreateNode(node).Take(1).Timeout(10.Seconds()).ToTask(TestTimeout);

        var mutated = node with { Name = "Forged update under cache identity" };

        using (accessService.SwitchAccessContext(CacheContext))
        {
            var act = async () => await meshService.UpdateNode(mutated)
                .Take(1).Timeout(10.Seconds()).ToTask(TestTimeout);

            var ex = await act.Should().ThrowAsync<UnauthorizedAccessException>();
            ex.Which.Message.Should().Contain("Access denied");
        }
    }

    /// <summary>
    /// Delete under the cache identity must fail.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CacheIdentity_DeleteNode_IsDenied()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        TestUsers.DevLogin(Mesh);

        var nodePath = $"CacheIdProbe/Delete_{Guid.NewGuid().AsString()}";
        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "To-be-deleted",
            NodeType = "Markdown"
        };
        await meshService.CreateNode(node).Take(1).Timeout(10.Seconds()).ToTask(TestTimeout);

        using (accessService.SwitchAccessContext(CacheContext))
        {
            var act = async () => await meshService.DeleteNode(nodePath)
                .Take(1).Timeout(10.Seconds()).ToTask(TestTimeout);

            var ex = await act.Should().ThrowAsync<UnauthorizedAccessException>();
            // Delete handler emits "Delete permission denied for '{path}'" on
            // NodeDeletionRejectionReason.Unauthorized; MeshService maps that
            // to UnauthorizedAccessException with the original message.
            ex.Which.Message.Should().Contain("permission denied",
                "denial must clearly indicate the authorization failure");
        }
    }

    /// <summary>
    /// Sanity check on the other side of the boundary: reads via
    /// <c>meshService.QueryAsync</c> under the cache identity succeed. This is
    /// the WHOLE POINT of the sanctioned identity — without this, the cache
    /// can't hydrate.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CacheIdentity_Read_Succeeds()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        TestUsers.DevLogin(Mesh);

        var nodePath = $"CacheIdProbe/Read_{Guid.NewGuid().AsString()}";
        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Cache-readable",
            NodeType = "Markdown"
        };
        await meshService.CreateNode(node).Take(1).Timeout(10.Seconds()).ToTask(TestTimeout);

        using (accessService.SwitchAccessContext(CacheContext))
        {
            // Query path: the cache uses similar paths during hydration.
            // No exception should be raised; a result is fine even if empty
            // (the security gate is what's under test, not query semantics).
            // The library's NotThrowAsync is non-generic (asserts NO exception of any
            // kind). Here only UnauthorizedAccessException must not be raised — a timeout
            // or empty completion is tolerated — so the type-specific assertion is done
            // manually: run the act, swallow everything except UnauthorizedAccessException.
            UnauthorizedAccessException? denied = null;
            try
            {
                await meshService
                    .ObserveQuery<MeshNode>(new MeshQueryRequest { Query = $"path:{nodePath}", Limit = 1 })
                    .Take(1).Timeout(5.Seconds()).ToTask(TestTimeout);
            }
            catch (UnauthorizedAccessException ex)
            {
                denied = ex;
            }
            catch
            {
                // Other exceptions (e.g. timeout on an empty query) are not under test.
            }
            denied.Should().BeNull(
                "cache identity must be allowed to read — hydration depends on it");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Cross-cutting AccessContext propagation through cache.Update
    // ─────────────────────────────────────────────────────────────────────
    // The tests below pin the contract documented in AsynchronousCalls.md:1120-1137
    // (and corollaries in CqrsAndContentAccess.md). The promise:
    //   "Every framework write primitive automatically captures the caller's
    //    AccessContext at invocation time and re-stamps it on every emission
    //    of the returned cold pipeline."
    //
    // Today (post-commit 178734555) `IMeshNodeStreamCache.Update` routes through
    // a per-path Subject → Concat → Subscribe queue. The Concat thread inherits
    // whatever AsyncLocal value happens to be current — usually null on a fresh
    // ThreadPool thread, or `sync/<streamid>` if the upstream emission was a
    // workspace-driven echo. The framework wrap `CarryAccessContext` is a
    // pass-through (AccessContextCaptureExtensions.cs:63-94), so the caller's
    // identity is NOT restored on the OnNext callback.
    //
    // PR2 of the AccessContext-propagation plan will reintroduce a leak-free
    // per-callback restore inside the framework primitive. These tests are the
    // regression suite that pin the desired behaviour. They are EXPECTED to fail
    // on the current code (`CarryAccessContext` no-op) and pass after PR2.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When a caller invokes <see cref="IMeshNodeStreamCache.Update"/> under
    /// their AccessContext, the OnNext callback of the returned cold observable
    /// MUST observe the caller's identity on AsyncLocal — even though the
    /// callback fires on the per-path serial Concat queue's thread (which has
    /// no AsyncLocal value of its own).
    ///
    /// <para>This is the most direct test of "AccessContext rides for free" on
    /// the cache.Update primitive. Without the framework wrap restoring the
    /// captured context per callback, the OnNext lambda observes whatever
    /// AsyncLocal is current on the Concat scheduler — typically null.</para>
    ///
    /// <para>Failure mode under current code: <c>observed</c> is null because
    /// <c>CarryAccessContext</c> is pass-through and the Concat thread did not
    /// inherit the test's AsyncLocal value through the Subject hop.</para>
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CacheUpdate_Concat_PreservesCallerIdentity()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        TestUsers.DevLogin(Mesh);

        // 1. Create a node under admin identity so the cache has something to read.
        var nodePath = $"CacheIdProbe/CacheUpdateIdentity_{Guid.NewGuid().AsString()}";
        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Original",
            NodeType = "Markdown"
        };
        await meshService.CreateNode(node).Take(1).Timeout(10.Seconds()).ToTask(TestTimeout);

        // 2. Switch to a real user — Alice — and call cache.Update under her identity.
        //    The OnNext callback's observed AsyncLocal value is the contract.
        var alice = new AccessContext { ObjectId = "alice@example.com", Name = "Alice" };

        // Capture from EITHER OnNext (if the update is granted — identity is
        // restored on the success callback) OR OnError (when the typed
        // MeshNodeStreamException fires with AccessDenied — the error's Path
        // and Message carry the principal). The contract under test is
        // "identity propagates"; both outcomes prove it. Pre-2026-05-29 the
        // silent denial path meant OnError never fired and OnNext was the
        // only signal — see TypedErrorPropagationTest for the contract that
        // now makes the denial loud.
        var observedContext = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (accessService.SwitchAccessContext(alice))
        {
            cache.Update(nodePath, n => n with { Name = "Updated by Alice" })
                .Subscribe(
                    _ => observedContext.TrySetResult(accessService.Context?.ObjectId),
                    ex => observedContext.TrySetResult(ExtractPrincipal(ex)));
        }

        var observed = await observedContext.Task.WaitAsync(15.Seconds(), TestTimeout);

        observed.Should().Be("alice@example.com",
            because: "cache.Update's returned cold observable must restore the caller's " +
                     "captured AccessContext — observable either as AsyncLocal on the " +
                     "OnNext callback (granted) or as the principal stamped on the typed " +
                     "AccessDenied MeshNodeError (denied). The per-path serial Concat " +
                     "queue runs on a ThreadPool thread with no AsyncLocal of its own, " +
                     "so without the framework wrap's leak-free per-callback restore the " +
                     "OnNext callback observes either null or the Concat thread's stale " +
                     "ambient value, and the outbound PatchDataRequest is unattributed " +
                     "(making the owner-side denial name 'sync/…' instead of alice).");
    }

    /// <summary>
    /// Pulls the caller's principal out of either a typed
    /// <see cref="MeshNodeStreamException"/> (AccessDenied carries the principal
    /// in the diagnostic message) or any other exception's message. Returns
    /// null when no email-shaped substring is present.
    /// </summary>
    private static string? ExtractPrincipal(Exception ex)
    {
        var text = ex switch
        {
            MeshNodeStreamException mse => mse.Error.Message + " " + (mse.Error.Diagnostic ?? ""),
            _ => ex.Message
        };
        // The owner-side denial message is shaped:
        //   "Access denied: user 'bob@example.com' lacks Update permission on '...'"
        // Pull the quoted principal between the first pair of single quotes
        // after "user".
        var idx = text.IndexOf("user '", StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + "user '".Length;
        var end = text.IndexOf('\'', start);
        return end > start ? text[start..end] : null;
    }

    /// <summary>
    /// Same contract for the synchronous Subscribe inside a <c>using</c>
    /// scope: when the caller invokes <see cref="IMeshNodeStreamCache.Update"/>
    /// and immediately disposes the <see cref="AccessService.SwitchAccessContext"/>
    /// scope, the captured identity must still ride the cold observable's
    /// emissions. This pins that the wrap captures by VALUE at call time, not
    /// by reference to the live AsyncLocal slot.
    ///
    /// <para>Failure mode under current code: same as
    /// <see cref="CacheUpdate_Concat_PreservesCallerIdentity"/> — the
    /// callback observes null because <c>CarryAccessContext</c> is
    /// pass-through. The added value of this test is locking down the
    /// capture-by-value semantic so future refactors can't quietly switch
    /// to a "read AsyncLocal at emission time" implementation that would
    /// observe the post-dispose value.</para>
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CacheUpdate_AfterCallerScopeDisposed_StillCarriesCapturedIdentity()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        TestUsers.DevLogin(Mesh);

        var nodePath = $"CacheIdProbe/CapturedAfterDispose_{Guid.NewGuid().AsString()}";
        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Original",
            NodeType = "Markdown"
        };
        await meshService.CreateNode(node).Take(1).Timeout(10.Seconds()).ToTask(TestTimeout);

        var bob = new AccessContext { ObjectId = "bob@example.com", Name = "Bob" };
        var observedContext = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        IObservable<MeshNode> updateObservable;
        using (accessService.SwitchAccessContext(bob))
        {
            // Build the cold observable INSIDE the using — capture must happen
            // at this moment. We Subscribe OUTSIDE the using so the scope is
            // already disposed by the time the inner Defer body runs on the
            // Concat thread.
            updateObservable = cache.Update(nodePath, n => n with { Name = "Updated by Bob" });
        }

        // Scope disposed — AsyncLocal is back to whatever DevLogin left it.
        // The framework wrap's captured value must STILL be Bob.
        // Same dual-path capture as CacheUpdate_Concat_PreservesCallerIdentity:
        // either OnNext (granted) sees AsyncLocal = bob, or OnError (denied)
        // carries bob as the principal in the typed AccessDenied error.
        updateObservable.Subscribe(
            _ => observedContext.TrySetResult(accessService.Context?.ObjectId),
            ex => observedContext.TrySetResult(ExtractPrincipal(ex)));

        var observed = await observedContext.Task.WaitAsync(15.Seconds(), TestTimeout);

        observed.Should().Be("bob@example.com",
            because: "the framework primitive must capture the caller's AccessContext by " +
                     "VALUE at Update(...) call time. Subscribing after the caller's scope " +
                     "is disposed must still observe Bob — either inside the OnNext " +
                     "callback (granted) or as the principal stamped on the typed " +
                     "AccessDenied MeshNodeError (denied). If the wrap reads AsyncLocal at " +
                     "emission time instead, this test would observe DevLogin's admin " +
                     "(or null) — the capture-by-value contract is what makes the wrap " +
                     "safe across scope-elided Subscribe sites.");
    }
}
