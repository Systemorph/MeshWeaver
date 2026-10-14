using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#6589 — a sender's gated writes are acted on in the order they reached the node.</b>
///
/// <para>A <c>[RequiresPermission]</c> delivery is handled when its permission verdict arrives,
/// and that is always OFF the turn that received it (the decision hops to the pool). Acting on each
/// verdict the moment it arrived let a sender's second write run its handler before its first, so
/// patches posted in order committed out of order and the last one posted did not stay —
/// <c>StampedFieldsTest.ContentDeclaredType_ClientWritesPostedInOrder_CommitInOrder</c> on CI.</para>
///
/// <para><b>The lever is the framework's own evaluator seam</b>
/// (<see cref="MessageHubPermissionExtensions.WithPermissionEvaluator"/>), narrowed to one identity
/// and to one armed check: the FIRST write's verdict is held until the test releases it, every other
/// check is answered at once. So the second write's verdict provably arrives first — the condition
/// that used to reorder — deterministically, with no sleep.</para>
///
/// <para>The control is a write from a DIFFERENT sender while the first is held: it lands. The
/// ordering is per sender, so a held verdict never blocks the hub — which is what keeps a verdict that
/// itself needs a reply from this hub from waiting on itself.</para>
/// </summary>
public class GatedWritesKeepTheSendersOrderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Writer = "ordered-release-writer";

    /// <summary>1 ⇒ the next permission check for <see cref="Writer"/> is answered by <see cref="held"/>.</summary>
    private int armed;

    /// <summary>The held verdict. Completed by the test (always, in a <c>finally</c>).</summary>
    private readonly AsyncSubject<Permission> held = new();

    /// <summary>Completed when the armed check has been asked — i.e. the first write reached the gate.</summary>
    private readonly AsyncSubject<Unit> heldAsked = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Applied after AddRowLevelSecurity, so the per-node hubs carry this delegate. Every
            // identity but Writer keeps the real evaluator.
            .ConfigureDefaultNodeHub(c => c.WithPermissionEvaluator(Evaluate));

    private IObservable<Permission> Evaluate(IMessageHub hub, string nodePath, string userId)
    {
        if (userId != Writer)
            return PermissionEvaluator.GetEffectivePermissions(hub, nodePath, userId);
        if (Interlocked.Exchange(ref armed, 0) == 1)
        {
            heldAsked.OnNext(Unit.Default);
            heldAsked.OnCompleted();
            return held;
        }
        return Observable.Return(Permission.All);
    }

    private void ReleaseHeldVerdict()
    {
        held.OnNext(Permission.All);
        held.OnCompleted();
    }

    private static AccessContext WriterContext => new() { ObjectId = Writer, Name = Writer };

    /// <summary>Posts a rename of <paramref name="path"/> as <see cref="Writer"/> from <paramref name="sender"/>, now.</summary>
    private static IConnectableObservable<bool> PostRename(IMessageHub sender, string path, string name)
    {
        var ack = sender
            .Observe<PatchDataResponse>(
                new PatchDataRequest(new MeshNodeReference(), new RawJson("{\"name\":\"" + name + "\"}")),
                o => o.WithTarget(new Address(path)).WithAccessContext(WriterContext))
            .Take(1)
            .Select(r => r.Message.Success)
            .Replay(1);
        ack.Connect();
        return ack;
    }

    private IObservable<MeshNode?> Named(string path, string name)
        => Mesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n?.Name == name);

    /// <summary>Creates the node and activates its hub with one ordinary write, so the hold below is the only variable.</summary>
    private async Task<string> WarmNode(string id)
    {
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition) { Name = "w0", NodeType = "Markdown" })
            .Should().Emit();
        (await PostRename(RequestHub, path, "warm").Should().Within(TestTimeouts.Convergence)
                .Emit("the node hub is active and the writer may write"))
            .Should().BeTrue();
        return path;
    }

    [Fact]
    public async Task ASendersSecondWrite_WaitsForItsFirst_AndTheLastOnePostedStays()
    {
        var path = await WarmNode("gated-order");
        try
        {
            Volatile.Write(ref armed, 1);
            var first = PostRename(RequestHub, path, "w1");
            await heldAsked.Should().Within(TestTimeouts.Convergence)
                .Emit("the first write reached the permission gate, whose verdict is now held");

            // The second write's verdict is answered at once — before the first one's.
            var second = PostRename(RequestHub, path, "w2");
            await Named(path, "w2").Should().NotEmit(TimeSpan.FromSeconds(2),
                "the second write from the same sender must not commit while the first is still "
                + "waiting for its verdict — that is the reorder #6589 measured",
                cancellationToken: TestContext.Current.CancellationToken);

            ReleaseHeldVerdict();
            (await first.Should().Within(TestTimeouts.Convergence).Emit("the first write is answered")).Should().BeTrue();
            (await second.Should().Within(TestTimeouts.Convergence).Emit("the second write is answered")).Should().BeTrue();
            await Named(path, "w2").Should().Within(TestTimeouts.Convergence)
                .Emit("the last write posted is the value that stays");
        }
        finally
        {
            ReleaseHeldVerdict();
        }
    }

    [Fact]
    public async Task AnotherSendersWrite_IsNotHeldBehindAHeldVerdict()
    {
        var path = await WarmNode("gated-order-other-sender");
        try
        {
            Volatile.Write(ref armed, 1);
            var first = PostRename(RequestHub, path, "held");
            await heldAsked.Should().Within(TestTimeouts.Convergence)
                .Emit("the first write reached the permission gate, whose verdict is now held");

            var other = PostRename(GetClient(), path, "other");
            (await other.Should().Within(TestTimeouts.Convergence)
                    .Emit("a write from another sender is ordered only behind ITS sender's writes"))
                .Should().BeTrue();
            await Named(path, "other").Should().Within(TestTimeouts.Convergence)
                .Emit("the other sender's write committed while the first sender's verdict was held");

            ReleaseHeldVerdict();
            (await first.Should().Within(TestTimeouts.Convergence).Emit("the held write is answered once released"))
                .Should().BeTrue();
        }
        finally
        {
            ReleaseHeldVerdict();
        }
    }
}
