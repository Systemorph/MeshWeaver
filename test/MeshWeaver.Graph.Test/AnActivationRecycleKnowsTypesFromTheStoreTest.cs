using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>"Is this a NodeType?" is answered by the STORE the type's activation loads from — never by
/// the query read model</b> (#6826).
///
/// <para><b>The defect.</b> <see cref="ActivationRecycle.Request"/> refused a request naming a type it
/// could not find, and it looked the type up with a QUERY (<c>path:{type}</c>). On a portal pod every
/// Doc NodeType — live, compiled, served — was refused as "unknown NodeType(s)" from the type's OWN
/// recycle cascade, on every replica, so none of their live activations was ever recycled. A query's
/// negative is not the store's: the CQRS rule forbids deciding the existence of one known path from
/// it, and a read model that answers a SHORTER snapshot (a provider that caught a read fault and
/// dropped the row, a derived index that does not hold the row) reads exactly like absence.</para>
///
/// <para><b>The instrument.</b> A real mesh over the real <see cref="InMemoryStorageAdapter"/>, whose
/// READ legs can be made to throw for the one type path under test — the production shape of a store
/// read that does not answer. Armed only around the request, so the mesh, the seeding and the
/// request's own write are untouched. With the fault armed the old check read the provider's shortened
/// snapshot as "the type does not exist" and refused the request as UNKNOWN; the check now reads the
/// store, so the same fault surfaces as a FAULT — the honest answer — and never as a refusal that
/// names a live type unknown.</para>
///
/// <para><b>Controls.</b> With nothing armed, a stored NodeType is accepted (the healthy path), while
/// a path with no node and a stored node that is not a NodeType are both still refused as unknown —
/// the side of the check that must not move. Every faulted case asserts the fault was INJECTED.</para>
/// </summary>
public class AnActivationRecycleKnowsTypesFromTheStoreTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "StoreKnownTypes";
    private const string StoredType = $"{Partition}/StoredType";
    private const string MarkdownPage = $"{Partition}/APage";
    private const string AbsentType = $"{Partition}/NoSuchType";

    private readonly FaultingReadStorageAdapter store = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => new StoreBootstrap(store);

    private sealed class StoreBootstrap(IStorageAdapter adapter) : IMeshBootstrap
    {
        public string Name => "monolith-over-a-faultable-store";

        public MeshBuilder Bootstrap(MeshBuilder builder)
            => builder
                .UseMonolithMesh()
                .ConfigureServices(services => services.AddInMemoryPersistence(adapter))
                .RegisterMeshQueryCoreOnMeshHub();
    }

    private IObservable<ActivationRecycleTicket> Recycle(string type) =>
        ActivationRecycle.Request(Mesh, new ActivationRecycleRequest
        {
            NodeTypes = ImmutableList.Create(type),
            Reason = "test: #6826 — a live type must never be refused as unknown",
            RequestedBy = nameof(AnActivationRecycleKnowsTypesFromTheStoreTest),
        });

    private async Task Seed(MeshNode node)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => meshService.CreateNode(node))
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"{node.Path} must be stored before the request names it", TestContext.Current.CancellationToken);
    }

    private Task SeedType() => Seed(new MeshNode("StoredType", Partition)
    {
        NodeType = MeshNode.NodeTypePath,
        Name = "Stored type",
        State = MeshNodeState.Active,
        Content = new NodeTypeDefinition { Description = "a stored, served NodeType" },
    });

    /// <summary>
    /// 🚨 THE #6826 REPRO. The type is stored; the read that would find it does not answer. The
    /// request must not claim the type is unknown — that sentence is the one that sent every live
    /// activation of a served type on keeping its old build with nobody looking.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AStoredTypeWhoseReadFaults_IsNeverRefusedAsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedType();

        store.FaultOn = "StoredType";
        try
        {
            var outcome = await Recycle(StoredType)
                .Select(ticket => (Ticket: (ActivationRecycleTicket?)ticket, Error: (Exception?)null))
                .Catch((Exception ex) => Observable.Return((Ticket: (ActivationRecycleTicket?)null, Error: (Exception?)ex)))
                .Take(1)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the request answers — with a ticket or with the read's fault", ct);

            store.Faults.Should().BeGreaterThan(0,
                "the fault must actually have been INJECTED — without it this case exercises the healthy "
                + "path and cannot fail");
            outcome.Ticket?.Refusal.Should().NotContain("unknown NodeType",
                "the type IS stored: a read that did not answer must never be reported as the type's absence");
            outcome.Error.Should().NotBeNull(
                "a store read that faulted is surfaced as the fault — the caller logs that nothing was "
                + "recycled because the type could not be READ, which is what happened");
        }
        finally
        {
            store.FaultOn = null;
        }
    }

    /// <summary>The healthy path: a stored NodeType is known, and the request is written.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AStoredType_IsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedType();

        var ticket = await Recycle(StoredType).Should().Within(TestTimeouts.Convergence)
            .Emit("the request answers", ct);

        ticket.Refusal.Should().BeNull("the type is stored and is a NodeType");
        ticket.Path.Should().StartWith(ActivationRecycleRequest.Namespace + "/");
        store.Faults.Should().Be(0, "nothing was armed in the healthy case");
    }

    /// <summary>
    /// 🚨 THE SIDE THAT MUST NOT MOVE. A path nothing stores, and a stored node that is not a
    /// NodeType, are still refused as unknown — reading the store did not turn the check into
    /// "anything goes".
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnAbsentPath_AndANodeThatIsNotAType_AreStillRefusedAsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(new MeshNode("APage", Partition) { NodeType = "Markdown", Name = "A page", State = MeshNodeState.Active });

        var absent = await Recycle(AbsentType).Should().Within(TestTimeouts.Convergence)
            .Emit("the request answers", ct);
        absent.Path.Should().BeNull("nothing was written for a type that does not exist");
        absent.Refusal.Should().Contain("unknown NodeType").And.Contain(AbsentType);

        var page = await Recycle(MarkdownPage).Should().Within(TestTimeouts.Convergence)
            .Emit("the request answers", ct);
        page.Path.Should().BeNull("a Markdown page is a node, not a NodeType");
        page.Refusal.Should().Contain("unknown NodeType").And.Contain(MarkdownPage);
    }

    /// <summary>
    /// The real <see cref="InMemoryStorageAdapter"/> with its READ legs made to throw for paths
    /// containing <see cref="FaultOn"/> — null (the default) injects nothing and forwards every member.
    /// </summary>
    private sealed class FaultingReadStorageAdapter(InMemoryStorageAdapter inner) : IStorageAdapter
    {
        private int faults;
        private volatile string? faultOn;

        /// <summary>The path fragment whose reads throw; null arms nothing.</summary>
        public string? FaultOn { get => faultOn; set => faultOn = value; }

        /// <summary>How many reads were faulted — asserted, so the faulted case cannot go vacuous.</summary>
        public int Faults => Volatile.Read(ref faults);

        private bool ShouldFault(string path) =>
            faultOn is { } fragment && path.Contains(fragment, StringComparison.OrdinalIgnoreCase);

        private IObservable<T> Fault<T>() => Observable.Defer(() =>
        {
            Interlocked.Increment(ref faults);
            return Observable.Throw<T>(new TimeoutException("The operation has timed out."));
        });

        private IStorageAdapter Inner => inner;

        public IObservable<DataChangeNotification> Changes => Inner.Changes;
        public IObservable<ChangeFeedGap> ChangeFeedGaps => Inner.ChangeFeedGaps;

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => ShouldFault(path) ? Fault<MeshNode?>() : Inner.Read(path, options);

        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => paths.Any(ShouldFault) ? Fault<MeshNode>() : Inner.ReadMany(paths, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options) => Inner.Write(node, options);

        public IObservable<IReadOnlyList<MeshNode>> WriteMany(IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
            => Inner.WriteMany(nodes, options);

        public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
            => Inner.WriteIfVersion(node, expectedVersion, options);

        public IObservable<string> Delete(string path) => Inner.Delete(path);
        public IObservable<bool> DeleteIfExists(string path) => Inner.DeleteIfExists(path);
        public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths) => Inner.DeleteMany(paths);
        public IObservable<string?> FindDeleteBlockingProvider(string path) => Inner.FindDeleteBlockingProvider(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
            => Inner.ListChildPaths(parentPath);

        public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath) => Inner.ListDescendantPaths(rootPath);
        public IObservable<bool> Exists(string path) => Inner.Exists(path);
        public IObservable<bool> ExistsInWritableStorage(string path) => Inner.ExistsInWritableStorage(path);

        public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(string fullPath, JsonSerializerOptions options)
            => Inner.FindBestPrefixMatch(fullPath, options);

        public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(string fullPath, JsonSerializerOptions options)
            => Inner.ResolvePath(fullPath, options);

        public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath) => Inner.ListPartitionSubPaths(nodePath);

        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => Inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<Unit> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => Inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => Inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => Inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }
}
