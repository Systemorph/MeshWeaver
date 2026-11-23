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
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A write's terminal means the node is READABLE — from any hub.</b> The cross-hub
/// read-after-write triage item behind Plugins#5816.
///
/// <para><b>The mechanism.</b> A node's owner keeps TWO reduced streams of the node: the plain one the
/// cross-hub patch handler watches for its commit echo, and the shared READ view
/// (<c>GetNullableStream</c>) every <c>GetDataRequest</c> is answered from. Each is its own
/// <c>sync/{id}</c> hub with its own turn queue (#3432 split them to stop reads minting a hub per
/// call). The ack used to be posted on the FIRST stream's echo, so it could leave while the read view
/// still held the node as it stood before the write: the writer's terminal was delivered, a read
/// issued after it from another hub (<c>portal/reads-{meshId}</c>, which orders nothing against the
/// writer) answered the pre-write node.</para>
///
/// <para><b>The barrier.</b> The test holds the read view's turn queue, which freezes it at the
/// pre-write node while the owner commits — the interleaving a loaded runner produces by chance,
/// made deterministic. Held: the write's terminal must NOT be delivered (before the fix it is, and the
/// read that follows answers the old name, or waits behind the held view). Released: the terminal is
/// delivered and the read answers the new name. Negative control: nothing held, the terminal and the
/// read agree at once, so the red is the held view and nothing else.</para>
/// </summary>
public class AWriteTerminalMeansTheNodeIsReadableFromAnyHubTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static string NewPath() => $"{TestPartition}/wr{Guid.NewGuid().ToString("N")[..10]}";

    private async Task<string> Create(string path, string name)
    {
        await Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = "Markdown", Name = name, State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit($"'{name}' is created", TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>The name a read issued from the mesh's shared read hub answers.</summary>
    private IObservable<string?> ReadName(string path, TimeSpan budget) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeOutcome(path, budget, ReadTimeoutBehavior.EmitNull))
            .Select(o => o.Node?.Name);

    private ReplaySubject<MeshNode> Write(string path, string name)
    {
        var terminal = new ReplaySubject<MeshNode>(1);
        Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(path).Update(n => n with { Name = name }))
            .Subscribe(terminal);
        return terminal;
    }

    [Fact(Timeout = 240_000)]
    public async Task AWriteIsNotAcknowledged_WhileTheReadViewStillHoldsTheOldNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await Create(NewPath(), "before");
        (await ReadName(path, TestTimeouts.Convergence).Should().Within(TestTimeouts.Convergence).Emit("warm read", ct))
            .Should().Be("before");

        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull("precondition: the read activated the owner");
        var readView = owner!.GetWorkspace().GetNullableStream(new MeshNodeReference());
        await readView.Should().Within(TestTimeouts.Convergence).Match(c => c.Value?.Name == "before");

        var entered = new AsyncSubject<Unit>();
        var release = 0;
        readView.Hub.InvokeAsync(() =>
        {
            entered.OnNext(Unit.Default);
            entered.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
        });
        try
        {
            await entered.Should().Within(TestTimeouts.Quick).Emit("the read view's turn is held", ct);

            var terminal = Write(path, "after");
            await terminal.Should().NotEmit(TimeSpan.FromSeconds(1),
                "the write's terminal must not be delivered while the view reads are answered from still "
                + "holds the node as it stood before the write: a read issued after the terminal, from "
                + "another hub, would answer the OLD name");

            Volatile.Write(ref release, 1);
            await terminal.Should().Within(TestTimeouts.Convergence).Emit("the terminal follows once the read view caught up", ct);
            (await ReadName(path, TestTimeouts.Convergence).Should().Within(TestTimeouts.Convergence)
                    .Emit("read after the terminal", ct))
                .Should().Be("after", "a read issued after the write's terminal sees the write");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>Negative control: nothing held, the terminal and the read agree at once.</summary>
    [Fact(Timeout = 240_000)]
    public async Task WithNothingHeld_TheTerminalAndTheReadAgree()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await Create(NewPath(), "before");
        var terminal = Write(path, "after");
        await terminal.Should().Within(TestTimeouts.Convergence).Emit("the write is acknowledged", ct);
        (await ReadName(path, TestTimeouts.Convergence).Should().Within(TestTimeouts.Convergence)
                .Emit("read after the terminal", ct))
            .Should().Be("after");
    }
}
