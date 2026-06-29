using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the identity contract that <c>DataSourceWithStorage.Synchronize</c> relies on:
/// the durable persistence write runs under <b>System</b>, and that identity SURVIVES the
/// <c>persistenceHub.InvokeAsync(async ct => …)</c> await — even though the write is kicked
/// off from the workspace emission scheduler, a background thread that has WIPED the ambient
/// <see cref="AccessContext"/>.
///
/// <para>Why this matters (atioz 2026-06-17): authorization happens ONCE at the user-facing
/// write (RLS on the action block, user identity live). By the time the change reaches the
/// persistence queue it is already approved — so the DB write runs as System and must never
/// fail-closed on the wiped ambient context. A fail-closed persist silently drops the change;
/// when the dropped write is an <c>_Activity</c> node, every progress reader then subscribes
/// to a node that does not exist → <c>[ROUTE] NotFound</c> resubscribe storm → partition wedge.
/// See AccessContextPropagation.md → "Persistence runs as System".</para>
/// </summary>
public class PersistenceRunsAsSystemTest : IDisposable
{
    private const string SystemObjectId = "system-security";
    private readonly AccessService _access = new();

    public void Dispose() { }

    /// <summary>
    /// The exact shape of the fix: a genuinely-null ambient context (the emission-scheduler
    /// condition), wrap the awaited persistence body in <see cref="AccessService.ImpersonateAsSystem"/>,
    /// and assert System is observed for the WHOLE body — before AND after the await — then
    /// cleanly restored to null afterwards (no identity leak past the write).
    /// </summary>
    [Fact]
    public async Task ImpersonateAsSystem_RunsPersistenceBodyAsSystem_AcrossAwait_FromNullAmbient()
    {
        // Emission-scheduler condition: neither per-request nor circuit identity present.
        _access.SetContext(null);
        _access.SetCircuitContext(null);

        string? beforeAwait = null;
        string? afterAwait = null;

        // Mirrors DataSourceWithStorage.Synchronize's persistenceHub.InvokeAsync(async ct => …).
        async Task PersistAsync()
        {
            using (_access.ImpersonateAsSystem())
            {
                beforeAwait = _access.Context?.ObjectId;
                await Task.Yield();          // cross the await — System must flow via ExecutionContext
                afterAwait = _access.Context?.ObjectId;
            }
        }

        var ct = TestContext.Current.CancellationToken;
        // Run on a pool thread where the ambient AsyncLocal is null — the production condition.
        await Task.Run(PersistAsync, ct).WaitAsync(5.Seconds(), ct);

        beforeAwait.Should().Be(SystemObjectId,
            because: "the persistence write is wrapped in ImpersonateAsSystem — already authorized upstream, so the durable store runs as System rather than the wiped null ambient that would fail-closed.");
        afterAwait.Should().Be(SystemObjectId,
            because: "ImpersonateAsSystem sets AsyncLocal, which flows across the await via ExecutionContext — the ENTIRE awaited persistence body runs as System, not just the synchronous prologue.");
        _access.Context.Should().BeNull(
            because: "the scope disposed when the persistence body returned — System must never leak past the write into the caller's logical execution context.");
    }

    /// <summary>
    /// Negative control: WITHOUT the impersonation wrap, the persistence body would observe the
    /// (null) ambient context — the fail-closed condition the fix eliminates. This pins WHY the
    /// wrap is load-bearing: remove it and the durable write runs context-null.
    /// </summary>
    [Fact]
    public async Task WithoutImpersonation_PersistenceBodyObservesNullAmbient_TheFailClosedCondition()
    {
        _access.SetContext(null);
        _access.SetCircuitContext(null);

        string? observed = "unset";

        async Task PersistWithoutWrap()
        {
            observed = _access.Context?.ObjectId;
            await Task.Yield();
            observed = _access.Context?.ObjectId;
        }

        var ct = TestContext.Current.CancellationToken;
        await Task.Run(PersistWithoutWrap, ct).WaitAsync(5.Seconds(), ct);

        observed.Should().BeNull(
            because: "with no impersonation the persistence body inherits the emission scheduler's wiped ambient context — null — which is exactly the fail-closed persist the System wrap prevents.");
    }
}
