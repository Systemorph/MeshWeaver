#pragma warning disable CS1591

using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit coverage for the new <see cref="PatchDataRequest"/> + handler. This is the
/// user-facing partial-update primitive: a caller posts a JSON merge patch against
/// a <see cref="WorkspaceReference"/> on some target hub; the handler applies the
/// merge to the stream's current value and commits via <c>stream.Update</c> — no
/// pre-existing subscription required, no client-side read needed.
///
/// Covers: applies a partial patch, leaves omitted fields intact, post-patch
/// GetDataRequest sees the new state (round-trip consistency).
/// </summary>
public class PatchDataRequestTest : MonolithMeshTestBase
{
    private const string TestNodeType = nameof(TestProduct);
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData-patchreq");

    public PatchDataRequestTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddMeshNodes(new MeshNode(TestNodeType)
            {
                Name = "Test Product",
                AssemblyLocation = typeof(PatchDataRequestTest).Assembly.Location,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<TestProduct>())
                    .AddDefaultLayoutAreas()
            });

    [Fact(Timeout = 30_000)]
    public async Task PatchDataRequest_MergesPartialFields_LeavesOmittedIntact()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var id = $"pdr-{Guid.NewGuid():N}";
        await mesh.CreateNode(new MeshNode(id, "ACME")
        {
            Name = "Original",
            NodeType = TestNodeType,
            Content = new TestProduct { Name = "Widget", Price = 1.00m, Quantity = 1 }
        });

        var path = $"ACME/{id}";

        // Post the PatchDataRequest with only { name: "Patched" } — the hub handler
        // applies this as a merge patch on its own MeshNode workspace stream.
        var patchJson = JsonSerializer.Serialize(new { name = "Patched" });
        var patchTcs = new TaskCompletionSource<PatchDataResponse>();
        var delivery = Mesh.Post(
            new PatchDataRequest(new MeshNodeReference(), new RawJson(patchJson)),
            o => o.WithTarget(new Address(path)))!;
        _ = Mesh.RegisterCallback(delivery, (d, _) =>
        {
            if (d is IMessageDelivery<PatchDataResponse> r) patchTcs.TrySetResult(r.Message);
            else patchTcs.TrySetException(new InvalidOperationException(
                $"Unexpected response: {d.Message?.GetType().Name}"));
            return Task.FromResult(d);
        }, default);

        var patchResp = await patchTcs.Task;
        patchResp.Success.Should().BeTrue(patchResp.Error ?? "no error provided");

        // Round-trip: GetDataRequest on MeshNodeReference must see the merged state.
        var getTcs = new TaskCompletionSource<MeshNode?>();
        var getDelivery = Mesh.Post(
            new GetDataRequest(new MeshNodeReference()),
            o => o.WithTarget(new Address(path)))!;
        _ = Mesh.RegisterCallback(getDelivery, (d, _) =>
        {
            if (d is IMessageDelivery<GetDataResponse> r) getTcs.TrySetResult(r.Message.Data as MeshNode);
            else getTcs.TrySetResult(null);
            return Task.FromResult(d);
        }, default);

        var node = await getTcs.Task;
        node.Should().NotBeNull();
        node!.Name.Should().Be("Patched",
            because: "PatchDataRequest merged only the 'name' field");
        node.NodeType.Should().Be(TestNodeType,
            because: "NodeType was not in the patch — must be preserved");
        node.Content.Should().NotBeNull(
            because: "Content was not in the patch — must be preserved");
    }
}
