using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Issue #2661 — "Write path fails OPEN: <c>UpdateRemote</c>'s timeout emits an optimistic
/// SUCCESS, and a late <c>DeliveryFailure(Unauthorized)</c> is never surfaced."
///
/// <para><b>What was wrong.</b> A cross-hub <c>stream.Update</c> posts a <c>PatchDataRequest</c>
/// and waits <c>UpdateResponseWaitBound</c> (~2 s) for the owner's verdict. On expiry it used to
/// emit the locally-computed snapshot and COMPLETE THE CALLER AS A SUCCESS. A bound elapsing is not
/// a commit: the owner might still refuse the write, and when it did — an RLS denial is a
/// <c>DeliveryFailure{ErrorType.Unauthorized}</c>, not a <c>PatchDataResponse</c>, so the late watch
/// did not even know about it — the refusal reached nobody. The caller kept "saved" for a write that
/// never happened, which is the silent-failure shape: a UI renders the optimistic value and a
/// workflow proceeds on a write that was refused.</para>
///
/// <para><b>What "saved" means.</b> The owner COMMITTED — not "the DB flushed", and certainly not
/// "two seconds passed with no bad news". <c>add</c> and <c>delete</c> have always waited for
/// exactly that verdict (<c>RequestChange</c> → <c>DataChangeStatus.Committed</c>, else a real
/// failure); <c>update</c> was the odd one out, and that inconsistency WAS the bug. The owner's
/// <c>PatchDataResponse</c> is if anything stronger than <c>Committed</c> — it is posted off an
/// identity-gated post-commit emission plus <c>IPostCommitFlush</c>'s durable flush — so the fix is
/// to make the caller's terminal that verdict, wherever it arrives.</para>
///
/// <para><b>Why these two tests are deterministic.</b> The interleaving is CONSTRUCTED, never raced
/// for. The owner's merge executor is parked behind a gated turn (the device
/// <see cref="LateNackReenqueueTest"/> and <see cref="QueuedWriteAdvancesOnHandoffTest"/> already
/// use), so the ack provably cannot arrive inside the caller's response bound; the denial is then
/// injected as the real wire event the pipeline produces, onto the real seam it travels
/// (<c>DeliveryFailure</c> → cache hub handler → <c>LatePatchResponseRegistry</c>). No cluster, no
/// load, no full-suite luck — the reproduction the issue could only get out of a 376-test run.</para>
/// </summary>
public class WriteWaitsForCommitVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Comfortably past UpdateResponseWaitBound (2s) and comfortably inside
    /// LateResponseWatchBound (30s) — the window in which the pre-fix code had already lied.</summary>
    private static readonly TimeSpan PastTheResponseBound = TimeSpan.FromSeconds(6);

    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(110);

    /// <summary>
    /// THE core invariant: a bound expiring is not a commit. With the owner's merge turn parked,
    /// the caller must still be waiting well past <c>UpdateResponseWaitBound</c> — pre-fix it had
    /// been handed the optimistic snapshot and a completion at ~2 s — and must settle as a SUCCESS
    /// only when the owner actually commits.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Update_WhileTheOwnerIsBusy_ReportsSuccessOnlyWhenTheOwnerCommits()
    {
        var ct = TestContext.Current.CancellationToken;
        const string id = "verdict-node";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(
                new MeshNode(id, TestPartition) { Name = "initial", NodeType = "Markdown" })
            .Should().Emit();

        var workspace = await WarmMirror(path, ct);
        using var gate = ParkOwnerMergeTurn(path);

        try
        {
            MeshNode? terminal = null;
            Exception? error = null;
            var completed = false;
            using var sub = workspace.GetMeshNodeStream(path)
                .Update(n => n with { Name = "committed" })
                .Subscribe(n => terminal = n, ex => error = ex, () => completed = true);

            // The one place a fixed wait is correct: a "nothing should happen yet" assertion has no
            // positive signal to await. Pre-fix this window contained the whole defect — the caller
            // was completed with an unconfirmed value at ~2s while the owner had not even merged.
            await Task.Delay(PastTheResponseBound, ct);
            Output.WriteLine($"[{PastTheResponseBound.TotalSeconds}s] terminal={terminal?.Name ?? "(none)"} "
                             + $"error={error?.GetType().Name ?? "(none)"} completed={completed}");

            completed.Should().BeFalse(
                "a write must not be reported as saved before the owner has committed it — the "
                + "response bound expiring is not a verdict (#2661)");
            terminal.Should().BeNull(
                "the optimistic snapshot must not be handed to the caller as the write's result");
            error.Should().BeNull("nothing has gone wrong yet — the owner is merely busy");

            // Now let the owner commit. THAT is the verdict, and it is what completes the caller.
            gate.Release();
            Output.WriteLine("[owner] merge executor released");

            await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                .Where(_ => completed || error is not null)
                .FirstAsync().Timeout(60.Seconds()).ToTask(ct);

            error.Should().BeNull("the owner accepted the write, so the caller must see a success");
            completed.Should().BeTrue("the owner's commit is the caller's success terminal");
            terminal.Should().NotBeNull();
            terminal!.Name.Should().Be("committed");

            var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
            var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
                .Where(n => n is not null && n!.Name == "committed")
                .FirstAsync().Timeout(45.Seconds()).ToTask(ct);
            persisted!.Name.Should().Be("committed",
                "the success the caller was given must correspond to a write that actually landed");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 🚨 The literal #2661 reproduction. The owner is busy, so the caller's bounded wait expires
    /// with no verdict; the RLS refusal — a <c>DeliveryFailure{Unauthorized}</c> correlated to the
    /// patch — arrives AFTER it. Pre-fix that failure reached nothing at all: the caller's
    /// <c>Observe</c> callback was gone, so <c>MessageHub.HandleCallbacks</c> logged "No subject
    /// found for response message" and marked it processed, and the late watch knew only about
    /// <c>PatchDataResponse</c>. The caller kept a success for a refused write. It must now fault
    /// with the SAME <see cref="UnauthorizedAccessException"/> the early-denial arm raises.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Update_WhenTheDenialArrivesAfterTheResponseBound_FaultsTheCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        const string id = "late-denial-node";
        var path = $"{TestPartition}/{id}";
        const string denial =
            "Access denied: user 'viewer_late' lacks Update permission on 'TestData/late-denial-node'";

        await NodeFactory.CreateNode(
                new MeshNode(id, TestPartition) { Name = "initial", NodeType = "Markdown" })
            .Should().Emit();

        var workspace = await WarmMirror(path, ct);
        var registry = Mesh.ServiceProvider.GetRequiredService<LatePatchResponseRegistry>();
        using var gate = ParkOwnerMergeTurn(path);

        try
        {
            MeshNode? terminal = null;
            Exception? error = null;
            var completed = false;
            using var sub = workspace.GetMeshNodeStream(path)
                .Update(n => n with { Name = "hacked" })
                .Subscribe(n => terminal = n, ex => error = ex, () => completed = true);

            // The patch is in flight and its late watch is armed — that id is the correlation the
            // owner's refusal would carry. Taking it here is what makes the race CONSTRUCTED.
            var requestId = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .Select(_ => registry.ArmedRequestIds.FirstOrDefault())
                .Where(rid => rid is not null)
                .FirstAsync().Timeout(30.Seconds()).ToTask(ct);
            Output.WriteLine($"[patch] armed late watch for request {requestId}");

            await Task.Delay(PastTheResponseBound, ct);
            completed.Should().BeFalse(
                "the caller must still be waiting for a verdict — pre-fix it had already been told "
                + "the write succeeded (#2661)");
            terminal.Should().BeNull();
            error.Should().BeNull();

            // The refusal, as the pipeline posts it: a DeliveryFailure{Unauthorized} carrying the
            // patch's RequestId, delivered to the mirror that submitted the write — the cache hub.
            // It travels the real seam (HandleCallbacks finds no live subject → the cache hub's
            // DeliveryFailure handler → LatePatchResponseRegistry.DispatchFailure).
            var carrier = RequestHub.Post(
                new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))!;
            var cacheAddress = new Address("cache", Mesh.Address.Id);
            RequestHub.Post(
                new DeliveryFailure(carrier, denial) { ErrorType = ErrorType.Unauthorized },
                o => o.WithTarget(cacheAddress).WithProperty(PostOptions.RequestId, requestId!));
            Output.WriteLine($"[denial] DeliveryFailure(Unauthorized) posted to {cacheAddress} for {requestId}");

            await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                .Where(_ => error is not null || completed)
                .FirstAsync().Timeout(60.Seconds()).ToTask(ct);

            completed.Should().BeFalse("a refused write must not complete as a success");
            terminal.Should().BeNull("a refused write must not hand the caller an optimistic value");
            error.Should().BeOfType<UnauthorizedAccessException>(
                "a late RLS denial must reach the caller as a real error, exactly as an early one does");
            error!.Message.Should().Be(denial,
                "the denial's own message names the principal and the permission — it must survive");
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task<IWorkspace> WarmMirror(string path, CancellationToken ct)
    {
        // 🚨 RequestHub, not Mesh — the root mesh hub is the ROUTER and must never be an END of a
        // delivery (RouterAsTestRequestOriginRatchetGuard / #2423).
        await RequestHub.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var workspace = Mesh.GetWorkspace();
        var warm = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask(ct);
        warm.Name.Should().Be("initial");
        return workspace;
    }

    /// <summary>
    /// Parks the owner's merge executor on a gated turn: the patch below is accepted by the owner's
    /// handler, but its merge provably cannot run, so no PatchDataResponse can arrive inside the
    /// caller's response bound. Same device as LateNackReenqueueTest / QueuedWriteAdvancesOnHandoffTest.
    /// </summary>
    private OwnerGate ParkOwnerMergeTurn(string path)
    {
        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("the owner per-node hub must be live before its merge turn is parked");
        var primary = nodeHub!.GetWorkspace().DataContext
            .GetDataSourceForType(typeof(MeshNode))!
            .GetStreamForPartition(null)!;
        var gate = new OwnerGate();
        primary.Update((Func<EntityStore?, ChangeItem<EntityStore>?>)(_ =>
        {
            gate.Entered.Set();
            gate.Released.Wait(TimeSpan.FromSeconds(120));
            return null;
        }), _ => { });
        gate.Entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
            "the gated turn must be running on the primary stream's executor before the write");
        return gate;
    }

    private sealed class OwnerGate : IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Released { get; } = new(false);
        public void Release() => Released.Set();
        // Release only — never Dispose the events. The parked turn's thread is still inside
        // Released.Wait when this runs, and disposing the handle out from under it throws there.
        public void Dispose() => Released.Set();
    }
}
