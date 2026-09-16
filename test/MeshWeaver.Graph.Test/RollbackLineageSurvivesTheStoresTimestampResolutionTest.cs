using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
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
/// 🚨 <b>THE CREATE ROLLBACK DISOWNED THE ROW IT HAD JUST WRITTEN</b>
/// (<see href="https://github.com/Systemorph/MeshWeaver/issues/4506">#4506</see>).
///
/// <para><b>The safety property.</b> <c>CompensateFailedCreate</c> (#638, and every bulk rollback
/// built on it in #4503) refuses to delete a node this request did not create by re-reading the row
/// and comparing <see cref="MeshNode.CreatedDate"/>. That comparison is exact, and it compares an
/// IN-MEMORY value against the same value <i>after a round trip through a column</i>.</para>
///
/// <para><b>The defect.</b> A <see cref="DateTimeOffset"/> tick is 100 ns; PostgreSQL
/// <c>timestamptz</c> — where <c>created_date</c> lives — holds MICROSECONDS. A stamp with any
/// sub-microsecond tick therefore comes back from its OWN row different, the lineage check reads
/// that as "somebody else's node", and the rollback stands down on the row it wrote. The caller is
/// then told the partially-created node is still there and must be removed by hand — which is
/// exactly the unrecoverable ghost #638 exists to prevent, now reported as if it were a safety
/// feature.</para>
///
/// <para><b>Measured, not reasoned</b> (memex.meshweaver.cloud, 2026-09-16, ONE node —
/// <c>Doc/_Activity/import-f7f86c9f5020ab36</c>): timestamps riding inside that node's JSON
/// <c>content</c> keep their tick — <c>…:47.9123072Z</c>, <c>…:47.9123038Z</c>,
/// <c>…:47.9123109Z</c>, three of three ending in a NON-ZERO sub-microsecond digit — while the same
/// node's column-backed <c>createdDate</c>/<c>lastModified</c> come back at exactly six fractional
/// digits. Same process, same instant, two different values.</para>
///
/// <para><b>Why the existing rollback tests could not see it.</b> Every one of them runs on
/// <c>InMemoryStorageAdapter</c>, which holds the CLR instance and hands the same object back — so
/// the lineage check compares a <see cref="DateTimeOffset"/> against ITSELF and no round trip
/// happens at all. The defect lives entirely in the gap between that backend and a real column.</para>
///
/// <para>🚨 <b>And the sub-microsecond tick is driven in EXPLICITLY, never taken from the clock.</b>
/// <c>DateTimeOffset.UtcNow</c>'s resolution is a property of the OS: measured 2026-09-16, this
/// macOS host mints whole microseconds (200 of 200 samples), while the Linux portal that produced
/// the timestamps above does not. A test resting on the ambient clock would therefore be a genuine
/// repro on CI and VACUOUS on a developer's machine — green for a reason that has nothing to do with
/// the fix. Driving the tick in as a caller-supplied <see cref="MeshNode.CreatedDate"/> (the import
/// flow's own shape, which both create paths preserve) makes the repro deterministic on every
/// platform and pins the second half of #4506 — that the stamp is caller-supplied — at the same
/// time.</para>
/// </summary>
public class RollbackLineageSurvivesTheStoresTimestampResolutionTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>Nodes in the batch — enough for "before", "at" and "after" the failure.</summary>
    private const int NodeCount = 3;

    /// <summary>The index whose critical handler faults: one node survives, two are rolled back.</summary>
    private const int FailAt = 1;

    /// <summary>The fault's own words — asserted, so the original cause survives the rollback report.</summary>
    private const string FaultMessage = "the test handler refuses this node";

    /// <summary>
    /// A fixed instant carrying a SUB-MICROSECOND tick (…7), which no
    /// microsecond-resolution column can hold. Fixed rather than sampled: the whole point is that
    /// the repro must not depend on the host clock's resolution.
    /// </summary>
    private static readonly DateTimeOffset AuthoredCreatedDate =
        new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1_234_567);

    // 🚨 A DERIVED FIELD INITIALIZER RUNS BEFORE THE BASE CONSTRUCTOR, and the base constructor is
    // what calls ConfigureMesh — so these are already set when the handler and the adapter
    // decorator below are registered and can be closed over by them.
    private readonly string _partition = "Ts" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Every node the post-creation handler ran for. Instance-owned — never static.</summary>
    private readonly ConcurrentQueue<string> _handlerRan = new();

    /// <summary>The one path whose critical handler faults, or null (the happy-path control).</summary>
    private string? _faultPath;

    /// <summary>
    /// When true the decorator returns rows with NO creation stamp at all — the shape a Postgres
    /// SATELLITE table has, where the authorship columns do not exist and every row is read with
    /// <c>NULL::timestamptz AS created_date</c>.
    /// </summary>
    private bool _stripCreationStamp;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services =>
        {
            services.AddSingleton<INodePostCreationHandler>(
                new FaultsOnOneNodeHandler(_partition, () => _faultPath, _handlerRan.Enqueue));

            // Wrap the LAST non-keyed IStorageAdapter registration — the production decorator
            // chain's outermost layer — so the rollback's own Read/Delete go through the modelled
            // column, exactly as they do in prod. The keyed "inner" registration is skipped
            // deliberately: reading ImplementationFactory off a keyed descriptor throws.
            var registered = services.Last(d => d.ServiceType == typeof(IStorageAdapter) && !d.IsKeyedService);
            services.Remove(registered);
            return services.AddSingleton<IStorageAdapter>(sp =>
                new MicrosecondColumnStorageAdapter(
                    Materialise(registered, sp), () => _stripCreationStamp));
        });

    private static IStorageAdapter Materialise(ServiceDescriptor descriptor, IServiceProvider sp)
        => descriptor.ImplementationFactory is { } factory
            ? (IStorageAdapter)factory(sp)
            : descriptor.ImplementationInstance as IStorageAdapter
              ?? throw new InvalidOperationException(
                  "The IStorageAdapter registration is neither a factory nor an instance, so this test "
                  + "cannot wrap it. If the persistence registration lane changed, update this hook — "
                  + "silently falling back to an unwrapped store would make every assertion below "
                  + "vacuous, because the un-decorated in-memory store cannot lose a tick.");

    private string NodePath(int index) => $"{_partition}/N{index}";

    /// <summary>
    /// 🚨 THE REPRO. The batch's nodes carry an authored <see cref="MeshNode.CreatedDate"/> with a
    /// sub-microsecond tick, the store holds only microseconds, and a critical handler fails — so
    /// the rollback must still recognise its own rows and REMOVE them.
    ///
    /// <para>Without the storage-stable mint this fails on the row assertion: the rollback reads
    /// back a stamp one tick short of the one it is holding, reports LeftInPlace for every ghost,
    /// and all three rows survive. That is the negative control, and it is deterministic on every
    /// platform because the tick is authored rather than sampled.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ACriticalHandlerFailure_RollsBackItsOwnRows_EvenWhenTheStoreHoldsOnlyMicroseconds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPartitionRoot();
        _faultPath = NodePath(FailAt);

        var response = await CreateBatch(AuthoredCreatedDate, cancellationToken);

        response.Success.Should().BeFalse(
            "a critical post-creation handler is part of the create's CONTRACT");

        var remaining = await RemainingPaths(cancellationToken);
        remaining.Should().Equal(
            [NodePath(0)],
            "the rollback must remove the rows THIS request wrote. The stamp it compares is one it "
            + "minted itself, so a column that cannot hold the stamp exactly must not be able to "
            + "make the rollback disown its own row — which is precisely what an unfloored "
            + "DateTimeOffset.UtcNow (100 ns) does against a microsecond column (#4506)");

        response.Error.Should().Contain(FaultMessage,
            "the ORIGINAL cause survives the rollback report");
        response.Error.Should().Contain("Rolled back 2 of the 2",
            "the count says what was actually REMOVED — and before the fix this read 'Rolled back 0 "
            + "of the 2' while telling the operator to clean up two rows by hand");
        response.Error.Should().NotContain("no longer the one this request wrote",
            "the rollback's own rows are NOT somebody else's — that sentence is reserved for a row "
            + "whose stamp really did change");
    }

    /// <summary>
    /// The stamp the create path MINTS is storage-stable too — the production shape, where no caller
    /// supplies a <see cref="MeshNode.CreatedDate"/> at all.
    ///
    /// <para>🚨 Stated honestly: this is a RATCHET, not the repro. On a host whose clock already
    /// ticks in whole microseconds (measured: this macOS host, 200 of 200) it passes with or without
    /// the fix; on Linux — where the portal runs and where CI runs — it is the assertion that a bare
    /// <c>DateTimeOffset.UtcNow</c> stamp cannot come back. The deterministic repro is the test
    /// above.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task EveryStampTheCreatePathMints_IsOneItsOwnColumnCanHold()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPartitionRoot();
        _faultPath = null;

        var response = await CreateBatch(authoredCreatedDate: null, cancellationToken);

        response.Success.Should().BeTrue($"nothing faulted: {response.Error}");
        foreach (var node in response.Created)
        {
            (node.CreatedDate.Ticks % TimeSpan.TicksPerMicrosecond).Should().Be(0,
                $"'{node.Path}' was stamped at {node.CreatedDate:O}, which a microsecond-resolution "
                + "column cannot hold — so the node this create EMITS is not the node its own row "
                + "holds, and every comparison between the two is wrong");
            (node.LastModified.Ticks % TimeSpan.TicksPerMicrosecond).Should().Be(0,
                $"'{node.Path}' carries a LastModified ({node.LastModified:O}) its own column "
                + "cannot hold — the same defect on the same row");
        }
    }

    /// <summary>
    /// 🚨 A row the store returns with NO creation stamp establishes NEITHER lineage NOR a mismatch.
    ///
    /// <para>It happens for real: on Postgres the authorship columns live only on <c>mesh_nodes</c>,
    /// and every satellite table (<c>_Access</c>, <c>_Thread</c>, <c>_Activity</c>, <c>_Comment</c>,
    /// <c>Source</c>, …) is read with <c>NULL::timestamptz AS created_date</c> — so a rollback aimed
    /// at a satellite path reads <c>default</c> for EVERY row, its own included. Answering
    /// <c>LeftInPlace</c> there told the operator a specific, FALSE thing about a row nothing had
    /// been compared on.</para>
    ///
    /// <para>The row is still not deleted — an unestablished lineage must never widen what a
    /// rollback removes. What changes is that the report says what actually happened.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ARowTheStoreReturnsWithNoCreationStamp_IsUndetermined_AndIsNotDeleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPartitionRoot();
        _faultPath = NodePath(FailAt);
        _stripCreationStamp = true;

        var response = await CreateBatch(AuthoredCreatedDate, cancellationToken);

        response.Success.Should().BeFalse("the critical handler still failed");
        response.Error.Should().Contain("could NOT be established",
            "a row with no stamp was never compared, so the outcome is Undetermined — the state that "
            + "says neither 'removed' nor 'it is someone else's'");
        response.Error.Should().NotContain("no longer the one this request wrote",
            "that sentence asserts a MISMATCH, and no comparison took place");

        var remaining = await RemainingPaths(cancellationToken);
        remaining.Should().Equal(
            [NodePath(0), NodePath(1), NodePath(2)],
            "an unestablished lineage must NEVER widen what the rollback deletes — the rows stay, "
            + "and the response tells a human to look at them");
    }

    /// <summary>
    /// Control: through the very same modelled column, a batch with nothing faulting lands in full
    /// and NOTHING is rolled back. Without it, a rollback that fired on the happy path would read as
    /// a pass on the repro above.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task WithNoFailure_TheWholeBatchLands_ThroughTheSameModelledColumn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPartitionRoot();
        _faultPath = null;

        var response = await CreateBatch(AuthoredCreatedDate, cancellationToken);

        response.Success.Should().BeTrue($"nothing faulted: {response.Error}");

        var remaining = await RemainingPaths(cancellationToken);
        remaining.Should().Equal(
            Enumerable.Range(0, NodeCount).Select(NodePath).ToArray(),
            "every row of a clean batch stays — the rollback is reachable ONLY from a critical "
            + "handler failure, whatever the store's timestamp resolution");
    }

    /// <summary>The partition root, seeded as the platform provisioner.</summary>
    private Task<MeshNode> SeedPartitionRoot() => SeedTopLevel(new MeshNode(_partition)
    {
        Name = "Timestamp Lineage",
        NodeType = "Space",
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# Timestamp Lineage\n\nfixture." },
    });

    /// <summary>
    /// Issues the batch through the bulk verb, as an importer does — optionally carrying an authored
    /// <see cref="MeshNode.CreatedDate"/>, which is the flow that makes a caller-supplied stamp the
    /// lineage token.
    /// </summary>
    private async Task<CreateNodesResponse> CreateBatch(
        DateTimeOffset? authoredCreatedDate, CancellationToken cancellationToken)
    {
        var batch = Enumerable.Range(0, NodeCount)
            .Select(i => new MeshNode($"N{i}", _partition)
            {
                Name = $"N{i}",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                // Distinct per node so the stamps are not accidentally interchangeable, and each
                // still carries the sub-microsecond tick that is the subject.
                CreatedDate = authoredCreatedDate is { } authored
                    ? authored.AddTicks(i * TimeSpan.TicksPerMicrosecond)
                    : default,
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
    /// storage adapter — the same instrument the rollback uses.
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
    /// A CRITICAL post-creation handler that faults for ONE chosen node — the shape of the real one
    /// this exists for (the grant that makes a brand-new partition root owned by somebody).
    /// </summary>
    private sealed class FaultsOnOneNodeHandler(
        string partition, Func<string?> faultPath, Action<string> record)
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

    /// <summary>
    /// 🚨 A BACKEND WHOSE TIMESTAMP COLUMNS HOLD MICROSECONDS — i.e. PostgreSQL <c>timestamptz</c>,
    /// modelled over the real in-memory store.
    ///
    /// <para><b>This is not a mock of a core interface.</b> Nothing is stubbed and nothing records
    /// expectations: every call reaches the production adapter chain, the store of record is still
    /// the real <c>InMemoryStorageAdapter</c>, and the only thing this layer does is give the
    /// durable row the resolution a real column has. A storage backend is free to hold less
    /// precision than a <see cref="DateTimeOffset"/> carries — <c>IStorageAdapter</c> promises
    /// nothing about timestamp resolution — so this is a CONFORMING backend, and the defect it
    /// surfaces is one the in-memory adapter structurally cannot (it hands the written CLR instance
    /// straight back, so the lineage check compares a value against itself).</para>
    ///
    /// <para><b>Faithful in both directions, which is the load-bearing part.</b> The write STORES a
    /// floored node but EMITS the node it was handed — exactly what
    /// <c>PostgreSqlStorageAdapter.Write</c> does (<c>return node;</c> after the upsert). Flooring
    /// the emitted node instead would floor the create path's own copy and hide the very gap under
    /// test.</para>
    ///
    /// <para>Forwarding is exhaustive on purpose: <c>IStorageAdapter</c>'s doc-comments require a
    /// decorator to forward <c>Changes</c>, <c>DeleteIfExists</c>, <c>WriteIfVersion</c>,
    /// <c>ResolvePath</c>, <c>ListDescendantPaths</c> and friends, or the behaviour they carry is
    /// silently lost at the outermost decorator that falls back to the interface default.</para>
    /// </summary>
    private sealed class MicrosecondColumnStorageAdapter(IStorageAdapter inner, Func<bool> stripCreationStamp)
        : IStorageAdapter
    {
        /// <summary>What the column would hold: the node with its timestamps floored to whole
        /// microseconds, and — when the satellite-table shape is selected — with no creation stamp
        /// at all.</summary>
        private MeshNode AsStored(MeshNode node) => node with
        {
            CreatedDate = stripCreationStamp()
                ? default
                : node.CreatedDate.AddTicks(-(node.CreatedDate.Ticks % TimeSpan.TicksPerMicrosecond)),
            LastModified = node.LastModified.AddTicks(
                -(node.LastModified.Ticks % TimeSpan.TicksPerMicrosecond)),
        };

        public IObservable<DataChangeNotification> Changes => inner.Changes;

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => inner.Read(path, options).Select(n => n is null ? null : AsStored(n));

        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => inner.ReadMany(paths, options).Select(AsStored);

        // Stores the floored node, emits the ORIGINAL — see the remarks.
        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(AsStored(node), options).Select(saved => saved is null ? null : node);

        public IObservable<IReadOnlyList<MeshNode>> WriteMany(
            IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        {
            var byPath = nodes.ToDictionary(n => n.Path, StringComparer.Ordinal);
            return inner.WriteMany(nodes.Select(AsStored).ToArray(), options)
                .Select(written => (IReadOnlyList<MeshNode>)written
                    .Select(w => byPath.TryGetValue(w.Path, out var original) ? original : w)
                    .ToList());
        }

        public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
            => inner.WriteIfVersion(AsStored(node), expectedVersion, options);

        public IObservable<bool> Exists(string path) => inner.Exists(path);

        public IObservable<bool> ExistsInWritableStorage(string path) => inner.ExistsInWritableStorage(path);

        public IObservable<string> Delete(string path) => inner.Delete(path);

        public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

        public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths)
            => inner.DeleteMany(paths);

        public IObservable<string?> FindDeleteBlockingProvider(string path)
            => inner.FindDeleteBlockingProvider(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath) => inner.ListChildPaths(parentPath);

        public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
            => inner.ListDescendantPaths(rootPath);

        public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
            string fullPath, JsonSerializerOptions options)
            => inner.FindBestPrefixMatch(fullPath, options)
                .Select(r => (r.Node is null ? null : AsStored(r.Node), r.MatchedSegments));

        public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
            string fullPath, JsonSerializerOptions options)
            => inner.ResolvePath(fullPath, options)
                .Select(r => (r.Node is null ? null : AsStored(r.Node), r.MatchedSegments));

        public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
            => inner.ListPartitionSubPaths(nodePath);

        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }
}
