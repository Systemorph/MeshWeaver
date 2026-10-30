using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A <see cref="RollbackNodeRequest"/> restores a historical node through the owner's OWN stream.
/// It is a CLIENT request that needs only Update on the node, and the own-stream write it issues
/// used to commit without the owner's commit guards (<see cref="IEntityCommitGuard"/>). So a restore
/// could commit a state every other route refuses: here, retyping a node back INTO a
/// <c>PartitionAccessPolicy</c>, which is create-only (<see cref="PolicyCommitGuard"/>). Pinned on a
/// file-system store, which retains history (an in-memory store keeps none, and the rollback
/// refuses before it writes). The control: a rollback that restores an ordinary state lands.
/// </summary>
public class RollbackMeetsTheCommitGuardsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-rollback-guards-{Guid.NewGuid():N}");

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => new FileSystemBootstrap(root);

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    [Fact(Timeout = 120_000)]
    public async Task ARollback_BackIntoAPolicy_IsRefused_AndAnOrdinaryRollbackLands()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        Mesh.ServiceProvider.GetRequiredService<IVersionQuery>().RetainsHistory.Should().BeTrue(
            "precondition: a store that retains history, or the rollback refuses before it writes");
        var child = $"{TestPartition}/child-{Guid.NewGuid().ToString("N")[..8]}";
        await NodeFactory.CreateNode(MeshNode.FromPath(child) with { Name = child, NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(TestTimeouts.Convergence).Emit("the scope must exist first", ct);
        // Inheritance kept, so the writer keeps reading the scope; the rule is about the TYPE.
        var policy = AssignmentNodeFactory.Policy(child, new PartitionAccessPolicy());
        await NodeFactory.CreateNode(policy)
            .Should().Within(TestTimeouts.Convergence).Emit("the policy is created where it belongs", ct);
        var path = policy.Path;
        var asPolicy = await Live(path, n => n.NodeType == PartitionAccessPolicyNodeType.NodeType, ct);

        // Retyping OUT of a policy is allowed; retyping back in is not, on any route.
        var asMarkdown = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { NodeType = "Markdown", Content = new MarkdownContent { Content = "v1" } })
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Content = new MarkdownContent { Content = "v2" } })
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"retyped out: {asMarkdown.NodeType} v{asMarkdown.Version}");
        asMarkdown.NodeType.Should().Be("Markdown", "precondition: retyping out of a policy lands");

        var refused = await Rollback(path, asPolicy.Version, ct);
        Output.WriteLine($"rollback to the policy version v{asPolicy.Version}: {refused}");
        refused.Should().StartWith("refused:",
            "a rollback restoring a policy is a retype into one, which the commit point refuses on every route");
        (await Live(path, n => n.NodeType is not null, ct)).NodeType.Should().Be("Markdown",
            "the refused rollback must leave the node as it was");

        var control = await Rollback(path, asMarkdown.Version, ct);
        Output.WriteLine($"rollback to the markdown version v{asMarkdown.Version}: {control}");
        control.Should().Be("landed", "the control: an ordinary rollback lands");
        await Live(path, n => n.Content is not null && n.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content == "v1", ct);
    }

    private Task<string> Rollback(string path, long version, CancellationToken ct) =>
        RequestHub
            .Observe<DataChangeResponse>(new RollbackNodeRequest(path, version), o => o.WithTarget(new Address(path)))
            // The handler answers a refusal with a FAILED log carrying the reason, and a success with
            // a log that records no error. Read by severity: its success log is never finished, so
            // DataChangeResponse.Status reads Failed for a rollback that landed.
            .Select(d => d.Message.Log.MaxSeverity >= Microsoft.Extensions.Logging.LogLevel.Error
                ? "refused:" + string.Join(" | ", d.Message.Log.Messages.Select(m => m.Message))
                : "landed")
            .Catch((Exception ex) => Observable.Return("refused:" + ex.GetType().Name + ": " + ex.Message))
            .Should().Within(TestTimeouts.Convergence).Emit("the rollback must answer", ct);

    private Task<MeshNode> Live(string path, Func<MeshNode, bool> predicate, CancellationToken ct) =>
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && predicate(n))
            .Select(n => n!)
            .Should().Within(TestTimeouts.Convergence).Emit($"{path} must reach the expected state", ct);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    /// <summary>The monolith mesh over a file-system store rooted at a per-test directory.</summary>
    private sealed class FileSystemBootstrap(string baseDirectory) : IMeshBootstrap
    {
        /// <inheritdoc />
        public string Name => "monolith-filesystem";

        /// <inheritdoc />
        public MeshBuilder Bootstrap(MeshBuilder builder)
        {
            Directory.CreateDirectory(baseDirectory);
            return builder.UseMonolithMesh().AddFileSystemPersistence(baseDirectory);
        }
    }
}
