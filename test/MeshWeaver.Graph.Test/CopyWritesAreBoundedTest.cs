using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A copy's per-node creates must reach the node-operations hub at most <c>batchSize</c> at a
/// time — the core instance of the defect behind Systemorph/MeshWeaver#7464.
///
/// <para><b>The defect.</b> <see cref="NodeCopyHelper"/> built each node's write as
/// <c>issuingHub.Observe(new CreateNodeRequest(…))</c> inside
/// <c>levels[level].Select(CopyOne).ToObservable().MergeBounded(batchSize)</c>. But
/// <c>IMessageHub.Observe(request)</c> POSTS AT THE CALL, not on Subscribe, and the bounded merge
/// enumerates its source inline — so every node of a level was POSTED the moment the level started,
/// and the bound limited only how many ANSWERS were awaited. A level of thousands of siblings (a
/// document's parts all sit on one level) put thousands of creates on the node-operations hub at
/// once — the same shape that, in the content indexer's part writer, left 7,875 creates without a
/// verdict at stage <c>write</c>.</para>
///
/// <para><b>The instrument.</b> A creation validator that PARKS every create under the copy's
/// target until the test releases it, so the number of creates the owner is handling at once is held
/// still and can be read. The release is a reactive signal; nothing blocks a thread.</para>
///
/// <para><b>Both directions and a positive control.</b> The peak must REACH the bound (an
/// over-serialised copy would stall at 1), must never EXCEED it (the defect reaches every sibling),
/// and once released EVERY node must land — a bound that wrote nothing would satisfy the ceiling on
/// its own.</para>
/// </summary>
public class CopyWritesAreBoundedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The bound under test, passed explicitly as the copy's <c>batchSize</c>.</summary>
    private const int Bound = 4;

    /// <summary>How many siblings the copied level holds — comfortably more than the bound.</summary>
    private const int ChildCount = 24;

    /// <summary>
    /// How long the "no more than the bound" negative window holds the first <see cref="Bound"/>
    /// creates parked. Deliberately NOT a CI-scaled <c>TestTimeouts</c> value: the parked creates
    /// run against the owner's 30 s CreateNode verdict deadline, which does not scale, and a window
    /// longer than it would time every parked create out before the release. A short window loses no
    /// sensitivity — the defect posts every sibling when the level STARTS, so the surplus creates are
    /// already at the owner by the time the first <see cref="Bound"/> are parked.
    /// </summary>
    private static readonly TimeSpan BoundNegativeWindow = TimeSpan.FromSeconds(5);

    private const string SourceRoot = $"{TestPartition}/BulkSource";
    private const string Target = $"{TestPartition}/BulkTarget";

    /// <summary>The copied root's path under <see cref="Target"/>; its children are what get parked.</summary>
    private const string TargetRoot = $"{Target}/BulkSource";

    // Field initializers run BEFORE the base constructor (which calls ConfigureMesh), so the instance
    // the mesh resolves is the one the assertions read.
    private readonly ParkingChildValidator validator = new(TargetRoot + "/");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(validator));

    public override async ValueTask DisposeAsync()
    {
        // Release FIRST and unconditionally: a failed assertion must never leave parked creates
        // behind for the teardown to wait on.
        validator.Release();
        await base.DisposeAsync();
    }

    private static string ChildId(int i) => $"Part{i:D3}";

    [Fact(Timeout = 120_000)]
    public async Task ALevelOfManySiblings_NeverReachesTheOwnerMoreThanTheBoundAtOnce_AndEveryNodeLands()
    {
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in new[] { SourceRoot }
                     .Concat(Enumerable.Range(0, ChildCount).Select(i => $"{SourceRoot}/{ChildId(i)}")))
            await NodeFactory
                .CreateNode(MeshNode.FromPath(path) with
                {
                    Name = path, NodeType = "Markdown", State = MeshNodeState.Active,
                })
                .Should().Within(TestTimeouts.Convergence).Emit($"the admin owns {TestPartition}",
                    cancellationToken: ct);
        validator.Validated.Should().Be(0, "seeding the SOURCE parks nothing — only the copy's target is parked");

        var completed = new AsyncSubject<NodeCopyOutcome>();
        using var subscription = NodeCopyHelper
            .CopyNodeTreeOutcome(MeshQuery, NodeFactory, Mesh, SourceRoot, Target, force: false, batchSize: Bound)
            .Subscribe(completed);

        // ── Direction 1: the copy admits the FULL bound (guards against over-serialising) ──
        SpinWait.SpinUntil(() => validator.Peak >= Bound, TestTimeouts.Convergence).Should().BeTrue(
            $"the copy must have {Bound} creates in flight at once — only {validator.Peak} ever reached "
            + "the owner together, so the writes are more serialised than the bound says");

        // ── Direction 2: and NO MORE. The other siblings are queued in the copy while these are
        // parked, so none may reach the owner. A non-event has no positive signal to filter for, so
        // this is the sanctioned negative window.
        SpinWait.SpinUntil(() => validator.Peak > Bound, BoundNegativeWindow).Should().BeFalse(
            $"a copy's creates must reach the node-operations hub at most {Bound} (batchSize) at a time: "
            + $"{validator.Peak} of {ChildCount} sibling creates were in flight together. Unbounded, a "
            + "level of thousands of siblings floods the write stage and its creates reach no verdict "
            + "within their deadline (Systemorph/MeshWeaver#7464)");

        Output.WriteLine($"peak concurrent copy creates: {validator.Peak} (bound {Bound}) of {ChildCount}");

        // ── The POSITIVE control: release, and every node must reach a verdict and land ──
        validator.Release();
        var outcome = await completed.Timeout(TestTimeouts.Convergence).Await(ct);
        outcome.IsComplete.Should().BeTrue($"the bound may DELAY a write, never DROP one: {outcome.Describe()}");

        var written = await Task.WhenAll(Enumerable.Range(0, ChildCount).Select(i =>
            ReadNode($"{TargetRoot}/{ChildId(i)}")
                .Should().Within(TestTimeouts.Convergence).Emit($"child {i} is read back", cancellationToken: ct)));
        written.Select((node, i) => (node, i)).Where(t => t.node is null).Select(t => t.i)
            .Should().BeEmpty("every sibling of the copied level lands");
        validator.Peak.Should().Be(Bound, "the ceiling holds over the whole copy, not just the sampled window");
    }

    /// <summary>
    /// Parks every create whose path starts with the given prefix at the owner's validator stage
    /// until <see cref="Release"/>, and records how many were parked at once. Anything else passes
    /// straight through.
    /// </summary>
    private sealed class ParkingChildValidator(string prefix) : INodeValidator
    {
        private readonly AsyncSubject<Unit> released = new();
        private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.Ordinal);
        private int peak;
        private int validated;

        public int Peak => Volatile.Read(ref peak);

        public int Validated => Volatile.Read(ref validated);

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

        public void Release()
        {
            released.OnNext(Unit.Default);
            released.OnCompleted();
        }

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        {
            var path = context.Node?.Path;
            if (path is null || !path.StartsWith(prefix, StringComparison.Ordinal))
                return Observable.Return(NodeValidationResult.Valid());
            return Observable.Defer(() =>
            {
                if (inFlight.TryAdd(path, 0))
                {
                    Interlocked.Increment(ref validated);
                    var now = inFlight.Count;
                    int seen;
                    while (now > (seen = Volatile.Read(ref peak))
                           && Interlocked.CompareExchange(ref peak, now, seen) != seen)
                    {
                    }
                }
                return released
                    .Take(1)
                    .Select(_ => NodeValidationResult.Valid())
                    .Finally(() => inFlight.TryRemove(path, out _));
            });
        }
    }
}
