using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Issue #1446 — <b>a permission check that could not be ESTABLISHED is not a decision, and a
/// create must not sit on it.</b>
///
/// <para><b>What happens.</b> Creating a node runs the security layer's grant and policy reads —
/// <c>namespace:{scope}/_Access nodeType:AccessAssignment</c> and
/// <c>namespace:{scope} id:_Policy nodeType:PartitionAccessPolicy</c> — over the target's scope and
/// every ancestor scope. <c>PermissionEvaluator</c> folds them with
/// <see cref="System.Reactive.Linq.Observable.CombineLatest{T1,T2,TResult}(IObservable{T1},IObservable{T2},Func{T1,T2,TResult})"/>,
/// which emits only once EVERY leg has emitted. A leg that starves — the cross-silo case, where the
/// owning activation lives on a peer silo that never answers — never emits, never completes and
/// never errors: <c>SyncedQueryMeshNodes</c> gates on <c>SeenInitial</c> over a merge containing a
/// Subject that is never completed, so the leg can only STALL. The fold therefore has no terminal at
/// all, the <c>.Take(1)</c> in <c>RlsNodeValidator</c> bounds the number of emissions rather than the
/// wait, and the validator <c>Concat()</c> in <c>RunCreationValidatorsObs</c> blocks on validator
/// #1. Observed in the hub snapshot at teardown:</para>
///
/// <code>
/// Hub portal/nodeops-… RunLevel=Started Disposal=Pending
///   Queue(buffer=2, …) Executing(CreateNodeRequest, 33014ms)
/// </code>
///
/// <para><b>Why the existing recovery does not cover it.</b> The faulted-query eviction (#1367) fixes
/// the CACHE — a <c>Replay(1)</c> that latched a terminal is dropped so the next <c>GetQuery</c>
/// opens a fresh upstream. It cannot help here twice over: a starved read has no terminal to latch,
/// and the caller already blocked on the fold is not waiting on the cache entry.</para>
///
/// <para><b>The shape of the fix</b> is the one #1218 introduced for source discovery: an
/// availability failure is reported AS an availability failure —
/// <c>SourceDiscoveryUnavailableException</c> / <c>CompilationStatus.Unavailable</c> there,
/// <see cref="NodeCreationRejectionReason.Unavailable"/> here — never mistaken for a verdict about
/// the thing being checked. A create whose permission check could not be established answers
/// "could not establish", which is neither a grant (unsafe) nor a denial (a lie that sends a
/// correctly-entitled user to ask for permissions they already hold).</para>
///
/// <para>🚨 <b>Why <c>ConfigureMeshBase</c> and not <c>ConfigureMesh</c>.</b> The default test mesh
/// adds a STATIC root grant for <c>Public</c> (<c>TestUsers.PublicAdminAccess</c>), and a static
/// grant seeds <c>PermissionEvaluator</c>'s synchronous fast path so the synced legs are never
/// awaited at all. Production has no such thing — <c>Public</c>'s grants are durable rows — and
/// every user's fold <c>Zip</c>s against the <c>Public</c> evaluation of the same path, so in a real
/// portal the starving legs gate EVERY caller's answer, statically-granted admins included. Opting
/// out of the seed is what makes this suite model production rather than the harness.</para>
/// </summary>
public class StarvedPermissionReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "StarvedPermissionTest";

    /// <summary>The scope whose grant/policy reads never answer — the create target's parent.</summary>
    internal const string StarvedScope = $"{Partition}/Starved";

    /// <summary>A sibling scope whose reads are healthy — the control.</summary>
    private const string HealthyScope = $"{Partition}/Healthy";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Partition) { Name = "Starved Permission Test", NodeType = "Markdown" })
            .ConfigureServices(services => services
                // Mesh-scoped singleton (no static state) — it dies with the mesh.
                .AddSingleton<StarvedSecurityQueryProvider>()
                .AddSingleton<IMeshQueryProvider>(sp =>
                    sp.GetRequiredService<StarvedSecurityQueryProvider>())
                // Short budget so the test spends seconds, not the production default, proving the
                // property. Same device as #1218's SourceSnapshotTimeout — and note there is only
                // ONE number to set: the establishment budget is the deepest rung of the ladder
                // derived from it (#1198), so a test can no longer configure an inner bound that
                // its own enclosing bound would beat.
                .AddSingleton(new MeshOperationOptions { Timeout = TimeSpan.FromSeconds(20) }));

    /// <summary>
    /// 🚨 THE PIN. Two creates, identical in every way except one: one target's scope has security
    /// reads that never answer, the other's are healthy.
    ///
    /// <para><b>Before the fix</b> the starved create never answers at all — it sits Executing until
    /// the caller's <c>RequestTimeout</c> gives up, which is a hang reported as the caller's fault
    /// rather than the read's.</para>
    ///
    /// <para><b>After the fix</b> it answers <see cref="NodeCreationRejectionReason.Unavailable"/>
    /// naming the check that could not be established. The healthy create is untouched.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task StarvedPermissionRead_AnswersUnavailable_WhileAHealthyScopeStillCreates()
    {
        // The two scopes exist before either is starved — their own creates are checked against
        // the PARTITION scope, which is never starved.
        await NodeFactory.CreateNode(new MeshNode("Starved", Partition)
        {
            Name = "Starved scope", NodeType = "Markdown"
        }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("Healthy", Partition)
        {
            Name = "Healthy scope", NodeType = "Markdown"
        }).Should().Within(30.Seconds()).Emit();

        // ── The control: a healthy scope still creates, so the starve is what makes the
        //    difference and not the fixture. ────────────────────────────────────────────────────
        var healthy = await AwaitResponseAsync(
            new CreateNodeRequest(new MeshNode("leaf", HealthyScope)
            {
                Name = "Healthy leaf", NodeType = "Markdown"
            }));
        healthy.Message.Success.Should().Be(true,
            $"a healthy scope must be unaffected — got {healthy.Message.RejectionReason}: {healthy.Message.Error}");

        // ── The pin: the starved scope ANSWERS, and says what it could not do. ─────────────────
        var started = Stopwatch.StartNew();
        var starved = await AwaitResponseAsync(
            new CreateNodeRequest(new MeshNode("leaf", StarvedScope)
            {
                Name = "Starved leaf", NodeType = "Markdown"
            }));
        started.Stop();
        Output.WriteLine(
            $"=== STARVED CREATE → {starved.Message.RejectionReason}: {starved.Message.Error} "
            + $"(after {started.Elapsed.TotalSeconds:0.0}s)");

        starved.Message.Success.Should().Be(false);

        // 1️⃣ An unestablished permission check is an AVAILABILITY failure, never a verdict.
        starved.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable,
            "the check never reached a decision, so reporting ValidationFailed/Unauthorized would "
            + "send a correctly-entitled caller to request permissions they already hold — the same "
            + "mistake #1218 fixed for a starved source read reported as a compile error");
        starved.Message.Error.Should().Contain("could not be established",
            "the message must name the true defect — the security read that did not answer");

        // 2️⃣ …and it answered rather than sat. The bound is the ESTABLISHMENT budget configured
        //    above; before the fix nothing bounded this and the caller's RequestTimeout was the
        //    only thing that ever ended the wait.
        (started.Elapsed < TimeSpan.FromSeconds(45)).Should().Be(true,
            $"the create must answer on its own budget, not be killed by its caller — took {started.Elapsed}");
    }
}

/// <summary>
/// The cross-silo starvation stand-in, scoped to the SECURITY reads of exactly one node scope.
/// Every other query gets an immediate empty <c>Initial</c> (well-behaved: exactly one Initial,
/// never completes) so the rest of the mesh — node creation into the healthy scope included — keeps
/// working, which is what makes the starve the only variable.
///
/// <para>🚨 It goes SILENT rather than throwing. A throwing query has a terminal, and a terminal
/// propagates: the create fails fast today. Real cross-silo starvation is the silent shape — the
/// owning activation lives on a peer silo and simply never answers — and that is the one with no
/// terminal anywhere in the fold.</para>
/// </summary>
public sealed class StarvedSecurityQueryProvider : IMeshQueryProvider
{
    /// <inheritdoc />
    public string Name => nameof(StarvedSecurityQueryProvider);

    /// <inheritdoc />
    public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

    /// <summary>
    /// The grant and policy reads of <see cref="StarvedPermissionReadTest.StarvedScope"/> —
    /// <c>namespace:{scope}/_Access nodeType:AccessAssignment</c> and
    /// <c>namespace:{scope} id:_Policy nodeType:PartitionAccessPolicy</c>. Matched on the scope AND
    /// a security node type, so ordinary reads and writes under the same subtree keep working;
    /// ancestor scopes are NOT matched, so only the deepest leg of the scope walk starves — which is
    /// enough, because <c>CombineLatest</c> holds the whole chain on it.
    /// </summary>
    private static bool IsStarvedSecurityRead(string? query) =>
        query is not null
        && query.Contains(StarvedPermissionReadTest.StarvedScope, StringComparison.OrdinalIgnoreCase)
        && (query.Contains("nodeType:AccessAssignment", StringComparison.OrdinalIgnoreCase)
            || query.Contains("nodeType:PartitionAccessPolicy", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(
        MeshQueryRequest request, JsonSerializerOptions options)
        => Observable.Create<QueryResultChange<T>>(observer =>
        {
            if (request.EffectiveQueries.Any(IsStarvedSecurityRead))
                return Disposable.Empty;   // subscribed, and never heard from again

            observer.OnNext(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Items = Array.Empty<T>(),
                Timestamp = DateTimeOffset.UtcNow,
            });
            return Disposable.Empty;
        });

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
        int limit = 10,
        string? contextPath = null,
        string? context = null)
        => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

    /// <inheritdoc />
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
        => Observable.Return(default(T?));
}
