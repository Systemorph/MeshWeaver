using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Isolation: does the LOCAL <c>workspace.UpdateMeshNode</c> write on a hub
/// become visible to a separate client's repeated <c>GetDataRequest</c> polls
/// against the same hub?
///
/// <para>
/// This is the read pattern that fails in
/// <c>OrleansHostedHubRoutingTest.ThreadHub_LocalWorkspaceWrite_VisibleViaGetDataRequest</c>:
/// no long-lived stream subscription, just one-shot <c>GetDataRequest</c>s.
/// Each request creates a fresh reduce stream wrapper. If the framework's
/// per-call reduce wrappers have a timing race against
/// <c>SynchronizationStream.SetCurrentRequest</c>, polls return Data=null.
/// </para>
///
/// <para>
/// Distinct from <c>ThreeNodePropagationTest</c> (long-lived subscription)
/// — that pattern works because the cached <c>Store</c> persists across
/// emissions. The polling pattern allocates fresh wrappers each call.
/// </para>
/// </summary>
public class GetDataRequestPropagationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    [Fact(Timeout = 30_000)]
    public void LocalUpdate_VisibleViaPolledGetDataRequest()
    {
        Output.WriteLine("[test] start");

        // 1. Create node `a` (plain Markdown — same NodeType as the working
        //    3-node test).
        var pathA = $"{TestPartition}/poll-a-{Guid.NewGuid():N}";
        var aId = pathA.Split('/').Last();
        Output.WriteLine($"[test] creating node {pathA}");
        NodeFactory.CreateNode(
            new MeshNode(aId, TestPartition) { Name = "A0", NodeType = "Markdown" }).Should().Emit();
        Output.WriteLine("[test] CreateNode succeeded");

        // 2. Read via GetDataRequest from a separate client. This is the
        //    polling read pattern — not a subscription.
        var client = GetClient(c => c.AddData());
        var initial = ReadViaGetDataRequest(client, pathA).Should().Match(n => n is not null);
        initial!.Name.Should().Be("A0");
        Output.WriteLine($"[poll] initial read: Name={initial.Name}");

        // 3. Update via NodeFactory (which posts an UpdateNodeRequest — same
        //    surface area used in production where the layout area / handler
        //    invokes workspace.UpdateMeshNode locally on the owning hub).
        NodeFactory.UpdateNode(initial with { Name = "A1" }).Should().Emit();
        Output.WriteLine("[update] posted A1");

        // 4. Poll via fresh GetDataRequests until we see A1. The interval IS the
        //    polling cadence; the .Match predicate IS the condition. If the
        //    per-call fresh reduce stream has the SetCurrentRequest race, this
        //    times out.
        Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => ReadViaGetDataRequest(client, pathA))
            .Should().Within(20.Seconds()).Match(current =>
            {
                Output.WriteLine($"[poll] Name={current?.Name ?? "(null)"}");
                return current?.Name == "A1";
            });
    }

    private IObservable<MeshNode?> ReadViaGetDataRequest(IMessageHub client, string path)
        => client.Observe(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(path)))
            .Select(resp =>
            {
                var node = resp.Message?.Data as MeshNode;
                if (node == null && resp.Message?.Data is JsonElement je)
                    node = je.Deserialize<MeshNode>(client.JsonSerializerOptions);
                return node;
            });
}
