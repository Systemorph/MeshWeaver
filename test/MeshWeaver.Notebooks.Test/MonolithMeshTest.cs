using FluentAssertions;
using MeshWeaver.Application;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Notebooks.Test;

public class MonolithMeshTest(ITestOutputHelper output) : MeshTestBase(output)
{

    protected override MeshBuilder CreateBuilder()
        => base
                .CreateBuilder()
                .AddMonolithMesh()
                .ConfigureMesh(mesh =>
                    mesh.WithMeshNodeFactory((addressType, id) =>
                        addressType == typeof(ApplicationAddress).FullName && id == TestApplicationAttribute.Test
                            ? MeshExtensions.GetMeshNode(addressType, id, GetType().Assembly.Location)
                            : null));


    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
    {
        return base.ConfigureClient(config)
            .WithTypes(typeof(Ping), typeof(Pong));
    }

    [Fact]
    public async Task BasicMessage()
    {
        var response = await GetClient()
            .AwaitResponse(new Ping(), o => o.WithTarget(TestApplicationAttribute.Address));
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<Pong>();
    }
}
