using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Connection.Orleans;
using Orleans;

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Hosting.Orleans.Test;

[Collection(nameof(OrleansClusterCollection))]
public class OrleansMeshTests(SharedOrleansFixture fixture, ITestOutputHelper output) : TestBase(output)
{
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await fixture.GetClientAsync($"mesh-{name}-{Guid.NewGuid():N}", "Roland");

    [Fact(Timeout = 30000)]
    public async Task PingPong()
    {
        var client = await GetClientAsync();
        var response = await client
            .Observe(new PingRequest(), o => o.WithTarget(OrleansTestMeshNodeAttribute.Address)).FirstAsync().ToTask(new CancellationTokenSource(20.Seconds()).Token);
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }

    [Theory(Timeout = 30000)]
    [InlineData("HubFactory")]
    [InlineData("Kernel")]
    public async Task HubWorksAfterDisposal(string id)
    {
        var client = await GetClientAsync();
        var address = AddressExtensions.CreateAppAddress(id);

        var response = await client
            .Observe(new PingRequest(), o => o.WithTarget(address)).FirstAsync().ToTask(new CancellationTokenSource(20.Seconds()).Token);
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();

        client.Post(new DisposeRequest(), o => o.WithTarget(address));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        response = await client
            .Observe(new PingRequest(), o => o.WithTarget(address)).FirstAsync().ToTask(new CancellationTokenSource(20.Seconds()).Token);
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }
}
