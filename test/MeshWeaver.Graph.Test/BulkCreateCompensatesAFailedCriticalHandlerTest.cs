using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A BULK CREATE COMPENSATES A FAILED CRITICAL POST-CREATION HANDLER — per node, exactly as
/// the singular create does</b> (Systemorph/MeshWeaver#4449 item 2).
///
/// <para><b>The defect.</b> The singular create has rolled the row back since #638, because
/// reporting <c>Fail</c> while LEAVING it is unrecoverable for the caller: a retry answers "already
/// exists" and nobody holds rights on the half-provisioned partition to clean it up. The bulk
/// sibling carried a comment claiming "same semantics as the singular create" and had no
/// compensation at all.</para>
///
/// <para><b>And it is broader than "the failed node is left behind".</b> Storage writes ALL rows
/// before the handlers run (phase 6 is one all-or-nothing <c>WriteMany</c>), and the handlers then
/// run sequentially. A critical fault at index <i>k</i> therefore leaves TWO populations of ghost
/// rows: node <i>k</i>, whose handler failed, AND nodes <i>k+1…n</i>, whose handlers NEVER RAN — no
/// owner grant attempted, nothing announced, and indistinguishable from a successful create by
/// inspection. Nodes <i>0…k-1</i> completed and must be KEPT.</para>
///
/// <para><b>What makes the assertion load-bearing.</b> The durable rows are read back through the
/// storage adapter in ONE <c>ReadMany</c> — the same instrument the rollback itself uses — so the
/// test asserts about the STORE, not about a cache or a query index that trails it. And the handler
/// records every node it ran for, so "the later nodes' handlers never ran" is measured rather than
/// assumed: it is what turns them into ghosts, and it is also the proof that the fix's per-index
/// <c>Catch</c> stops the chain at the first failure instead of compensating the batch twice.</para>
/// </summary>
public class BulkCreateCompensatesAFailedCriticalHandlerTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>Nodes in the batch. More than three, so "before", "at" and "after" are all plural.</summary>
    private const int NodeCount = 5;

    /// <summary>The index whose critical handler faults — with nodes both before and after it.</summary>
    private const int FailAt = 2;

    /// <summary>The fault's own words, asserted in the response: the ORIGINAL cause must survive
    /// the rollback report, exactly as it does on the singular path.</summary>
    private const string FaultMessage = "the test handler refuses this node";

    // 🚨 A DERIVED FIELD INITIALIZER RUNS BEFORE THE BASE CONSTRUCTOR, and the base constructor is
    // what calls ConfigureMesh — so both of these are already set when the handler below is
    // registered and can be closed over by it.
    private readonly string _partition = "Bc" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Every node the post-creation handler actually ran for, in order. Instance-owned and
    /// concurrent — never a static collection.</summary>
    private readonly ConcurrentQueue<string> _handlerRan = new();

    /// <summary>The one path whose critical handler faults, or null (the happy-path control). Set by
    /// the test before it issues the batch.</summary>
    private string? _faultPath;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodePostCreationHandler>(
                new FaultsOnOneNodeHandler(_partition, () => _faultPath, _handlerRan.Enqueue)));

    private string NodePath(int index) => $"{_partition}/N{index}";

    /// <summary>
    /// 🚨 THE PROPERTY: <c>0…k-1</c> survive, <c>k…n</c> are gone — from the STORE, not from a
    /// cache — and the response names the original cause, the rollback and what was kept.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ACriticalHandlerFailure_RollsBackThatNodeAndEveryNodeAfterIt_AndKeepsTheOnesBefore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPartitionRoot();
        _faultPath = NodePath(FailAt);

        var response = await CreateBatch(cancellationToken);

        response.Success.Should().BeFalse(
            "a critical post-creation handler is part of the create's CONTRACT — a batch whose "
            + "contract was not met must not be answered Ok");
        response.FailedPath.Should().Be(NodePath(FailAt),
            "the failure is attributable to exactly one node, and naming it is how a caller knows "
            + "which one to fix");
        response.Error.Should().Contain(FaultMessage,
            "the ORIGINAL cause survives the rollback report — a caller told only 'rolled back' "
            + "cannot tell a refused node from a store that went away");
        response.Error.Should().Contain("rolled back",
            "the rollback OUTCOME is reported beside the cause, as the singular path reports it");

        response.Created.Select(n => n.Path).Should().Equal(
            [NodePath(0), NodePath(1)],
            "the nodes whose handlers COMPLETED are kept, and the response says which they are — "
            + "they are the only rows this request leaves behind");

        var remaining = await RemainingPaths(cancellationToken);
        remaining.Should().Equal(
            [NodePath(0), NodePath(1)],
            $"the store must hold ONLY the nodes before the failure. '{NodePath(FailAt)}' is the "
            + "singular path's case (its critical handler failed), and "
            + $"'{NodePath(FailAt + 1)}'…'{NodePath(NodeCount - 1)}' are the second, invisible ghost "
            + "population: rows whose post-creation handlers never ran, which nobody can tell from a "
            + "successful create and which the caller can neither retry over nor clean up");

        _handlerRan.Should().Equal(
            [NodePath(0), NodePath(1), NodePath(FailAt)],
            "the chain stops AT the first critical failure: the later nodes' handlers are never "
            + "subscribed (which is exactly why those rows have to be rolled back), and no node's "
            + "handler runs twice");
    }

    /// <summary>
    /// Control: with nothing faulting, the same batch lands in full and NOTHING is rolled back. A
    /// compensation that fired on the happy path would be a far worse defect than the one fixed.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task WithNoFailure_TheWholeBatchLands_AndNothingIsRolledBack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPartitionRoot();
        _faultPath = null;

        var response = await CreateBatch(cancellationToken);

        response.Success.Should().BeTrue($"nothing faulted, so the batch must land: {response.Error}");
        response.Created.Select(n => n.Path).Should().Equal(
            Enumerable.Range(0, NodeCount).Select(NodePath).ToArray());

        var remaining = await RemainingPaths(cancellationToken);
        remaining.Should().Equal(
            Enumerable.Range(0, NodeCount).Select(NodePath).ToArray(),
            "every row of a clean batch stays — the rollback must be reachable ONLY from a critical "
            + "handler failure");

        _handlerRan.Should().Equal(
            Enumerable.Range(0, NodeCount).Select(NodePath).ToArray(),
            "every node's handlers run, in caller order");
    }

    /// <summary>
    /// The partition root, seeded as the platform provisioner. Seeded EXPLICITLY so the batch's own
    /// nodes are the only rows the request creates — the rollback's scope is the batch, and a
    /// partition artifact it deliberately does not drop must not be confused with one.
    /// </summary>
    private Task<MeshNode> SeedPartitionRoot() => SeedTopLevel(new MeshNode(_partition)
    {
        Name = "Bulk Compensation",
        NodeType = "Space",
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# Bulk Compensation\n\nfixture." },
    });

    /// <summary>
    /// Issues the batch through the bulk verb, as an importer does: ONE
    /// <see cref="CreateNodesRequest"/> at the node-operation hub, under the platform identity.
    /// </summary>
    private async Task<CreateNodesResponse> CreateBatch(CancellationToken cancellationToken)
    {
        var batch = Enumerable.Range(0, NodeCount)
            .Select(i => new MeshNode($"N{i}", _partition)
            {
                Name = $"N{i}",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = $"# N{i}\n\npage" },
            })
            .ToImmutableList();

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var delivery = await access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodesRequest(batch)))
            .FirstAsync()
            .Timeout(120.Seconds())
            .Await(cancellationToken);

        Output.WriteLine(
            $"[bulk] success={delivery.Message.Success} failedPath={delivery.Message.FailedPath} "
            + $"created=[{string.Join(", ", delivery.Message.Created.Select(n => n.Path))}] "
            + $"error={delivery.Message.Error}");
        return delivery.Message;
    }

    /// <summary>
    /// The batch's paths that still have a DURABLE ROW, read in ONE <c>ReadMany</c> straight off the
    /// storage adapter — the authoritative answer about the store, and the same instrument the
    /// rollback uses. A point read per path would ask the routing layer about nodes that are
    /// deliberately absent.
    /// </summary>
    private async Task<IList<string>> RemainingPaths(CancellationToken cancellationToken)
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var paths = await persistence
            .ReadMany(Enumerable.Range(0, NodeCount).Select(NodePath).ToArray(), Mesh.JsonSerializerOptions)
            .Select(n => n.Path)
            .ToList()
            .Timeout(60.Seconds())
            .Await(cancellationToken);

        var ordered = paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        Output.WriteLine($"[store] rows remaining: [{string.Join(", ", ordered)}]");
        return ordered;
    }

    /// <summary>
    /// A CRITICAL post-creation handler (<see cref="INodePostCreationHandler.FailsCreateOnError"/>)
    /// that faults for ONE chosen node of this test's partition — the shape of the real one this
    /// exists for: the grant that makes a brand-new partition root owned by somebody.
    ///
    /// <para>It matches STRUCTURALLY (this partition, ids <c>N…</c>) rather than by NodeType, so it
    /// is invisible to the partition bootstrap's own writes and to every other test.</para>
    /// </summary>
    private sealed class FaultsOnOneNodeHandler(string partition, Func<string?> faultPath, Action<string> record)
        : INodePostCreationHandler
    {
        /// <summary>Diagnostic label only — <see cref="Matches"/> decides.</summary>
        public string NodeType => "Markdown";

        /// <inheritdoc />
        public bool Matches(MeshNode createdNode)
            => string.Equals(createdNode.Namespace, partition, StringComparison.Ordinal)
               && createdNode.Id.StartsWith('N');

        /// <inheritdoc />
        public bool FailsCreateOnError => true;

        /// <inheritdoc />
        public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy)
        {
            record(createdNode.Path);
            return string.Equals(createdNode.Path, faultPath(), StringComparison.Ordinal)
                ? Observable.Throw<Unit>(new InvalidOperationException(FaultMessage))
                : Observable.Return(Unit.Default);
        }
    }
}
