using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
    [Fact(Timeout = 30_000)]
    public async Task LocalUpdate_VisibleViaPolledGetDataRequest()
    {
        Output.WriteLine("[test] start");
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        // 1. Create node `a` (plain Markdown — same NodeType as the working
        //    3-node test).
        var pathA = $"{TestPartition}/poll-a-{Guid.NewGuid():N}";
        var aId = pathA.Split('/').Last();
        Output.WriteLine($"[test] creating node {pathA}");
        await NodeFactory.CreateNode(
            new MeshNode(aId, TestPartition) { Name = "A0", NodeType = "Markdown" });
        Output.WriteLine("[test] CreateNode succeeded");

        // 2. Read via GetDataRequest from a separate client. This is the
        //    polling read pattern — not a subscription.
        var client = GetClient(c => c.AddData());
        var initial = await ReadViaGetDataRequest(client, pathA, ct);
        initial.Should().NotBeNull("initial read should succeed");
        initial!.Name.Should().Be("A0");
        Output.WriteLine($"[poll] initial read: Name={initial.Name}");

        // 3. Update via NodeFactory (which posts an UpdateNodeRequest — same
        //    surface area used in production where the layout area / handler
        //    invokes workspace.UpdateMeshNode locally on the owning hub).
        await NodeFactory.UpdateNode(initial with { Name = "A1" });
        Output.WriteLine("[update] posted A1");

        // 4. Poll via fresh GetDataRequests until we see A1. If the per-call
        //    fresh reduce stream has the SetCurrentRequest race, this loop
        //    times out.
        for (var i = 0; i < 50; i++)
        {
            var current = await ReadViaGetDataRequest(client, pathA, ct);
            Output.WriteLine($"[poll #{i}] Name={current?.Name ?? "(null)"}");
            if (current?.Name == "A1") return;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("After 50 polls Name still wasn't A1");
    }

    private async Task<MeshNode?> ReadViaGetDataRequest(IMessageHub client, string path, CancellationToken ct)
    {
        var resp = await client.Observe(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(path)))
            .FirstAsync().ToTask(ct);
        var node = resp.Message?.Data as MeshNode;
        if (node == null && resp.Message?.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(client.JsonSerializerOptions);
        return node;
    }
}
