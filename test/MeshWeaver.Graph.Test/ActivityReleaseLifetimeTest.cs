using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

public class ActivityReleaseLifetimeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public async Task TerminalCompletion_DoesNotResolveServicesFromTheDisposedWriter()
    {
        using var scope = Mesh.ServiceProvider.CreateScope();
        var writer = scope.ServiceProvider.CreateMessageHub(
            new Address("client/release-lifetime"), configuration => configuration.AddData())!;
        writer.ServiceProvider.Should().NotBeSameAs(Mesh.ServiceProvider);
        var releaseMirror = ActivityLogAppender.PrepareMirrorRelease(
            writer, "TestData/_Activity/release-lifetime");
        Action completed = () => releaseMirror(ActivityStatus.Succeeded);

        writer.Dispose();
        await writer.DisposalCompleted.Timeout(TimeSpan.FromSeconds(10));
        scope.Dispose();

        var failure = Record.Exception(completed);
        failure.Should().BeNull("a completed write must not fail while releasing its mirror after the writer retires");
    }
}
