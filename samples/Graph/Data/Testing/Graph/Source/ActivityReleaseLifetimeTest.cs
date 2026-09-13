// <meshweaver>
// Id: Testing/Graph/ActivityReleaseLifetimeTest
// DisplayName: Testing/Graph/ActivityReleaseLifetimeTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

public class ActivityReleaseLifetimeTest(MeshTestContext context) : InMeshTestBase(context)
{
    [MeshFact]
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
        using var disposalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await writer.DisposalCompleted.ObserveCompletion(
            ex => Output.WriteLine($"Writer disposal failed after completion: {ex}"),
            disposalDeadline.Token);
        scope.Dispose();

        var failure = Record.Exception(completed);
        failure.Should().BeNull("a completed write must not fail while releasing its mirror after the writer retires");
    }
}
