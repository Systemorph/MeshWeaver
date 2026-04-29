using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans port of <c>GetDataRequestPropagationTest</c>: does an
/// <c>UpdateNodeRequest</c> against the owning grain become visible to a
/// separate client's repeated <c>GetDataRequest</c> polls?
///
/// <para>
/// Polling pattern — each call creates a fresh per-call reduce stream wrapper.
/// If the framework has a SetCurrentRequest race in the per-call reduce
/// pipeline, polls return Data=null indefinitely. The monolith counterpart
/// passes; this checks the Orleans grain boundary doesn't introduce the bug.
/// </para>
/// </summary>
[Collection(nameof(OrleansClusterCollection))]
public class OrleansGetDataRequestPropagationTest(SharedOrleansFixture fixture, ITestOutputHelper output)
    : OrleansSharedTestBase(fixture, output)
{
    [Fact(Timeout = 60_000)]
    public async Task LocalUpdate_VisibleViaPolledGetDataRequest_AcrossGrains()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        Output.WriteLine("[test] start");

        // 1. Create node a (plain Markdown — same NodeType as the working
        //    Orleans 3-node test). Use a creator client to avoid mixing roles.
        var aId = $"poll-a-{Guid.NewGuid():N}";
        var pathA = $"User/TestUser/{aId}";

        var creator = await GetClientAsync($"creator-{Guid.NewGuid():N}", "TestUser");
        var createResp = await creator.Observe(
                new CreateNodeRequest(new MeshNode(aId, "User/TestUser")
                {
                    Name = "A0",
                    NodeType = "Markdown",
                }),
                o => o.WithTarget(new Address("User/TestUser")))
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);
        Output.WriteLine($"[test] CreateNode succeeded: {pathA}");

        // 2. Read via polled GetDataRequest from a SEPARATE client. Retry the
        //    initial read — grain activation + MeshDataSource init runs lazily.
        var reader = await GetClientAsync($"reader-{Guid.NewGuid():N}", "TestUser");
        MeshNode? initial = null;
        for (var i = 0; i < 50; i++)
        {
            initial = await ReadViaGetDataRequestAsync(reader, pathA, ct);
            if (initial != null) break;
            Output.WriteLine($"[init-poll #{i}] still null");
            await Task.Delay(100, ct);
        }
        initial.Should().NotBeNull("initial read should succeed within 5s");
        initial!.Name.Should().Be("A0");
        Output.WriteLine($"[poll] initial: Name={initial.Name}");

        // 3. Update via UpdateNodeRequest — this routes to a's grain hub which
        //    invokes its local UpdateMeshNode handler.
        var updResp = await creator.Observe(
                new UpdateNodeRequest(initial with { Name = "A1" }),
                o => o.WithTarget(new Address(pathA)))
            .FirstAsync().ToTask(ct);
        updResp.Message.Success.Should().BeTrue(updResp.Message.Error);
        Output.WriteLine("[update] UpdateNodeRequest succeeded");

        // 4. Poll fresh GetDataRequests until A1 appears.
        for (var i = 0; i < 100; i++)
        {
            var current = await ReadViaGetDataRequestAsync(reader, pathA, ct);
            if (current?.Name == "A1")
            {
                Output.WriteLine($"[poll #{i}] saw A1");
                return;
            }
            Output.WriteLine($"[poll #{i}] Name={current?.Name ?? "(null)"}");
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("After 100 polls (10s) Name still wasn't A1");
    }

    private async Task<MeshNode?> ReadViaGetDataRequestAsync(IMessageHub client, string path, CancellationToken ct)
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
