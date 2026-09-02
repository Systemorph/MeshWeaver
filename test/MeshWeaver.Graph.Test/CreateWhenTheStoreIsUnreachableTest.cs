using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A CREATE WHOSE STORE IS UNREACHABLE WAS NOT REFUSED — IT WAS NEVER ATTEMPTED</b>
/// (MeshWeaver#3050 / MeshWeaver#3051).
///
/// <para>In production the PostgreSQL host became briefly unreachable and every storage call site
/// timed out while OPENING a connector (<c>PoolingDataSource.OpenNewConnector →
/// NpgsqlConnector.RawOpen → TimeoutException</c>) — before any SQL ran. The create handler's chain
/// opens with <c>persistence.Read(node.Path, …)</c>, so that timeout arrived at its terminal error
/// arm, fell into the catch-all, and was reported twice over as something it was not:</para>
///
/// <list type="bullet">
///   <item>to the OPERATOR as <c>fail: MeshWeaver.Mesh.CreateNode "Unexpected error during node
///     creation at …"</c> — naming the create as the thing that failed, when the create was fine and
///     a database was down. Same wording defect MeshWeaver#2876 fixed one layer up for a layout
///     area's render;</item>
///   <item>to the CALLER as <see cref="NodeCreationRejectionReason.Unknown"/> — indistinguishable
///     from a verdict, when the two demand OPPOSITE responses. A refused create must not be retried;
///     a create that was never attempted must be, <b>with the same node id</b>. A caller that reads
///     "refused" and mints a fresh id on its next attempt writes a DUPLICATE, which is
///     MeshWeaver#2229's shape.</item>
/// </list>
///
/// <para><b>What is deliberately NOT tested here, because it must not exist:</b> a retry. The bounded
/// one already ran upstream (<c>TransientStorageFaults.RetryTransientConnect</c>, MeshWeaver#2521 —
/// 250 → 500 → 1000 ms, then the last error surfaces). A fault reaching the handler's terminal arm is
/// one whose budget is honestly spent, so a retry here would aim a second one at the resource that is
/// already the bottleneck. These tests assert an ANSWER, never a recovery.</para>
///
/// <para>The fault is injected at the storage adapter — the seam the incident's own stack traces name
/// — rather than at a validator, so the chain that faults is the production chain. The driver
/// exception is a <see cref="DbException"/> stand-in: core never references Npgsql, and it does not
/// need to (see <c>StorageFaults</c>).</para>
/// </summary>
public class CreateWhenTheStoreIsUnreachableTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Any path carrying this segment marker faults with a transient CONNECT timeout.</summary>
    private const string Unreachable = "unreachable-";

    /// <summary>
    /// Any path carrying this marker faults with a real QUERY error (<c>42P01 undefined_table</c>).
    /// This is the falsifying case: it travels the identical code path and must still be reported as
    /// an unexpected failure. Without it, "answer Unavailable for every exception" would pass every
    /// other assertion in this file.
    /// </summary>
    private const string BrokenQuery = "brokenquery-";

    // ——— the harness ————————————————————————————————————————————————————————————————————

    /// <summary>
    /// A driver exception core can construct: every ADO.NET provider derives its faults from
    /// <see cref="DbException"/>, which carries <see cref="DbException.SqlState"/> — the surface
    /// <c>StorageFaults</c> classifies on.
    /// </summary>
    private sealed class FakeDbException(string message, Exception? inner = null, string? sqlState = null)
        : DbException(message, inner)
    {
        public override string? SqlState { get; } = sqlState;
    }

    /// <summary>The verbatim incident shape: "Failed to connect …" wrapping a connect timeout.</summary>
    private static DbException ConnectTimeout() =>
        new FakeDbException("Failed to connect to 10.42.18.4:5432",
            new TimeoutException("Timeout during connection attempt"));

    /// <summary>A genuine query error — a defect, not an outage, and it must keep reading as one.</summary>
    private static DbException UndefinedTable() =>
        new FakeDbException("relation \"mesh_nodes\" does not exist", sqlState: "42P01");

    private static Exception? FaultFor(string path)
        => path.Contains(Unreachable, StringComparison.Ordinal) ? ConnectTimeout()
            : path.Contains(BrokenQuery, StringComparison.Ordinal) ? UndefinedTable()
            : null;

    /// <summary>
    /// Wraps the production adapter chain and makes the READ/WRITE surfaces fault for the marked
    /// paths only — everything else forwards untouched, so the mesh boots, the partition exists, and
    /// a create on a healthy path still lands (the positive control below depends on that).
    ///
    /// <para>Forwarding is exhaustive on purpose: <c>IStorageAdapter</c>'s doc-comments require a
    /// decorator to forward <c>Changes</c>, <c>DeleteIfExists</c>, <c>WriteIfVersion</c>,
    /// <c>ResolvePath</c> and <c>ListDescendantPaths</c>, or the behaviour they carry is silently
    /// lost at the outermost decorator that falls back to the interface default.</para>
    /// </summary>
    private sealed class PathFaultingStorageAdapter(IStorageAdapter inner, Func<string, Exception?> faultFor)
        : IStorageAdapter
    {
        private static IObservable<T> Fault<T>(Exception ex) => Observable.Throw<T>(ex);

        public IObservable<DataChangeNotification> Changes => inner.Changes;

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => faultFor(path) is { } ex ? Fault<MeshNode?>(ex) : inner.Read(path, options);

        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => paths.Select(faultFor).FirstOrDefault(e => e is not null) is { } ex
                ? Fault<MeshNode>(ex)
                : inner.ReadMany(paths, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => faultFor(node.Path) is { } ex ? Fault<MeshNode?>(ex) : inner.Write(node, options);

        public IObservable<IReadOnlyList<MeshNode>> WriteMany(
            IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
            => nodes.Select(n => faultFor(n.Path)).FirstOrDefault(e => e is not null) is { } ex
                ? Fault<IReadOnlyList<MeshNode>>(ex)
                : inner.WriteMany(nodes, options);

        public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
            => faultFor(node.Path) is { } ex ? Fault<bool?>(ex) : inner.WriteIfVersion(node, expectedVersion, options);

        public IObservable<bool> Exists(string path)
            => faultFor(path) is { } ex ? Fault<bool>(ex) : inner.Exists(path);

        public IObservable<string> Delete(string path)
            => faultFor(path) is { } ex ? Fault<string>(ex) : inner.Delete(path);

        public IObservable<bool> DeleteIfExists(string path)
            => faultFor(path) is { } ex ? Fault<bool>(ex) : inner.DeleteIfExists(path);

        public IObservable<string?> FindDeleteBlockingProvider(string path)
            => inner.FindDeleteBlockingProvider(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath) => inner.ListChildPaths(parentPath);

        public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
            => inner.ListDescendantPaths(rootPath);

        public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
            string fullPath, JsonSerializerOptions options) => inner.FindBestPrefixMatch(fullPath, options);

        public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
            string fullPath, JsonSerializerOptions options) => inner.ResolvePath(fullPath, options);

        public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
            => inner.ListPartitionSubPaths(nodePath);

        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<System.Reactive.Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services =>
        {
            // Wrap the LAST non-keyed IStorageAdapter registration — the production decorator chain's
            // outermost layer (MonotonicWriteGuard → VersionWriting → the in-memory store) — so the
            // create handler's own `persistence.Read` is the call that faults, exactly as in prod.
            // The keyed "inner" registration DecorateStorageAdapterWithVersionWriting adds is skipped
            // deliberately: reading ImplementationFactory off a keyed descriptor throws.
            var registered = services.Last(d => d.ServiceType == typeof(IStorageAdapter) && !d.IsKeyedService);
            services.Remove(registered);
            return services.AddSingleton<IStorageAdapter>(sp =>
                new PathFaultingStorageAdapter(Materialise(registered, sp), FaultFor));
        });

    private static IStorageAdapter Materialise(ServiceDescriptor descriptor, IServiceProvider sp)
        => descriptor.ImplementationFactory is { } factory
            ? (IStorageAdapter)factory(sp)
            : descriptor.ImplementationInstance as IStorageAdapter
              ?? throw new InvalidOperationException(
                  "The IStorageAdapter registration is neither a factory nor an instance, so this test "
                  + "cannot wrap it. If the persistence registration lane changed, update this hook — "
                  + "silently falling back to an unwrapped store would make every assertion below "
                  + "vacuous.");

    // ——— issuing the verbs ——————————————————————————————————————————————————————————————

    private static MeshNode Page(string id) => new(id, TestPartition)
    {
        Name = id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}\n\npage" },
    };

    private static string PathOf(string id) => $"{TestPartition}/{id}";

    private static string NewId(string marker) => marker + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Issues the singular create the way production does — from a client hub, aimed at the mesh's
    /// node-operations hub — and returns the RESPONSE rather than an exception, because the rejection
    /// reason is the thing under test and <c>IMeshService.CreateNode</c> throws it away.
    /// </summary>
    private async Task<CreateNodeResponse> Create(MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(node)))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(90.Seconds()).Await();
        Output.WriteLine(
            $"create {node.Path} success={response.Success} reason={response.RejectionReason} "
            + $"error={response.Error}");
        return response;
    }

    /// <summary>The bulk sibling — the verb every installer and static-repo import travels.</summary>
    private async Task<CreateNodesResponse> CreateMany(params MeshNode[] nodes)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodesRequest([.. nodes])))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(90.Seconds()).Await();
        Output.WriteLine(
            $"createMany {nodes.Length} success={response.Success} reason={response.RejectionReason} "
            + $"error={response.Error}");
        return response;
    }

    // ——— the guards ————————————————————————————————————————————————————————————————————

    /// <summary>
    /// 🚨 <b>THE GUARD.</b> A create whose store could not be reached is answered
    /// <see cref="NodeCreationRejectionReason.Unavailable"/> — the value that already means "not
    /// evaluated, retry is meaningful" (MeshWeaver#1446) — and never
    /// <see cref="NodeCreationRejectionReason.Unknown"/>, which every consumer maps to the same
    /// "creation failed" verdict as a genuine refusal.
    ///
    /// <para>On the failing code this assertion reads <c>Unknown</c>: the transient connect timeout
    /// fell straight into the catch-all, one branch below where it belongs.</para>
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ACreateWhoseStoreIsUnreachable_IsAnsweredUnavailable_NotUnknown()
    {
        var response = await Create(Page(NewId(Unreachable)));

        response.Success.Should().BeFalse("the store was unreachable, so nothing was created");
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable,
            "a store that could not be REACHED means the create was never ATTEMPTED — reporting it as "
            + "Unknown makes an availability failure indistinguishable from a verdict, and the two "
            + "demand opposite responses from the caller (do not retry vs. retry with the same id)");
    }

    /// <summary>
    /// The answer has to SAY the two things a caller acts on, because the reason enum alone does not
    /// travel to a human, an MCP client or an activity log. Both clauses are load-bearing: "nothing
    /// was written" is what stops a caller from cleaning up a row that does not exist, and "the same
    /// id" is what stops it from minting a fresh one on its next attempt (MeshWeaver#2229).
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task TheAnswerSaysNothingWasWritten_AndThatTheSameIdMayBeRetried()
    {
        var response = await Create(Page(NewId(Unreachable)));

        response.Error.Should().NotBeNull();
        response.Error!.Should().Contain("unreachable",
            "the sentence must name the STORE as the thing that failed — 'Unexpected error during "
            + "node creation' sent readers hunting for a defect in a create path that was fine");
        response.Error.Should().Contain("Nothing was written");
        response.Error.Should().Contain("same node id",
            "a caller that retries with a FRESH id after an availability failure mints a duplicate");
        response.Error.Should().NotContain("Unexpected error");
    }

    /// <summary>
    /// 🚨 <b>THE FALSIFYING CASE.</b> A real query error travels the identical path and must STILL be
    /// reported as an unexpected failure. Excusing a defect as an outage is the mirror image of the
    /// bug being fixed here, and it is the one that hides: an operator told "temporarily unavailable"
    /// about a missing table waits for it to come back.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ARealQueryError_IsStillReportedAsAnUnexpectedFailure()
    {
        var response = await Create(Page(NewId(BrokenQuery)));

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unknown,
            "42P01 undefined_table is a DEFECT, not an outage — classifying it as Unavailable would "
            + "tell an operator to wait for a table that is never coming back");
        response.Error.Should().Contain("Unexpected error");
    }

    /// <summary>
    /// 🚨 <b>THE POSITIVE CONTROL.</b> Without it every assertion above could pass on a harness that
    /// simply broke all creates, and the file would prove nothing about the classification.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ACreateOnAReachablePath_StillLands()
    {
        var id = "healthy" + Guid.NewGuid().ToString("N")[..8];

        var response = await Create(Page(id));

        response.Success.Should().BeTrue(
            $"the faulting adapter only fails the marked paths — {response.Error}");
        (await ReadNode(PathOf(id)).FirstAsync().Timeout(60.Seconds()).Await())
            .Should().NotBeNull("the create landed and must be readable back");
    }

    /// <summary>
    /// 🚨 <b>THE SIBLING VERB.</b> A guard on one create verb and not the other is a guard on neither:
    /// every installer and static-repo import writes through <c>CreateNodesRequest</c>, and a whole
    /// batch answered "unexpected error" reads to the caller as a refusal it must not retry. The bulk
    /// handler's terminal arm carries the same catch-all and needed the same wire.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ABulkCreateWhoseStoreIsUnreachable_IsAlsoAnsweredUnavailable()
    {
        var response = await CreateMany(
            Page("bulkhealthy" + Guid.NewGuid().ToString("N")[..8]),
            Page(NewId(Unreachable)));

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable,
            "the bulk verb runs the identical pipeline and its terminal arm had the identical hole — "
            + "a batch that was never attempted is not a batch that was refused");
        response.Error.Should().NotBeNull();
        response.Error!.Should().Contain("Nothing was written");
    }
}
