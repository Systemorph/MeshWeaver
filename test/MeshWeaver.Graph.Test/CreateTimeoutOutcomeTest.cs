using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

public class CreateTimeoutOutcomeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly HeldPostCreation handler = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).ConfigureServices(s => s.AddSingleton<INodePostCreationHandler>(handler));

    [Fact]
    public async Task CreateTimeout_DoesNotClaimAnAlreadyPersistedNodeWasNotApplied()
    {
        var path = $"{TestPartition}/held-create-outcome";
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        try
        {
            var pending = access.RunAsSystem(() => ObserveNodeOperation(
                    new CreateOrUpdateNodeRequest(new MeshNode("held-create-outcome", TestPartition)
                    {
                        Name = "Persisted before the response", NodeType = "Markdown"
                    })))
                .FirstAsync().Await(TestContext.Current.CancellationToken);
            await handler.Entered.Should().Within(TestTimeouts.Convergence).Emit(
                "the post-creation handler holds the reply only after the row has been saved");
            var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
            var persisted = await storage.Read(path, Mesh.JsonSerializerOptions)
                .FirstAsync().Await(TestContext.Current.CancellationToken);
            persisted.Should().NotBeNull();

            // Let the actual production verdict bound expire; never shorten it for this test.
            var answer = await pending.WaitAsync(
                MeshExtensions.InnerCreateVerdictBound + TestTimeouts.Convergence,
                TestContext.Current.CancellationToken);
            answer.Message.Success.Should().BeFalse();
            answer.Message.Error.Should().Contain("outcome is unknown");
            answer.Message.Error.Should().NotContain("NOT applied",
                "the row is already persisted and a missing reply cannot prove otherwise");
        }
        finally
        {
            handler.Release.OnNext(Unit.Default);
            handler.Release.OnCompleted();
        }
    }

    private sealed class HeldPostCreation : INodePostCreationHandler
    {
        public string NodeType => "Markdown";
        public AsyncSubject<Unit> Entered { get; } = new();
        public AsyncSubject<Unit> Release { get; } = new();

        public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy)
        {
            if (createdNode.Id != "held-create-outcome")
                return Observable.Return(Unit.Default);
            Entered.OnNext(Unit.Default);
            Entered.OnCompleted();
            return Release;
        }
    }
}
