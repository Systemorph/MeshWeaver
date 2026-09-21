using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A policy read that cannot be answered DENIES — and says it could not be established, not
/// that access is refused.</b> The access-control half of policy
/// <c>query-fanin-stall-terminal</c>.
///
/// <para><b>Why this test has to exist at all.</b> The permission fold is a <c>CombineLatest</c>
/// over the grant and policy reads of the target's scope and every ancestor scope, and
/// <c>ObserveScopePolicies</c> is deliberately NOT seeded: <c>PermissionCap</c> and
/// <c>BreaksInheritance</c> are RESTRICTIONS whose absence WIDENS, so an empty seed hands the gate a
/// verdict that ignores every runtime cap (<c>Doc/Architecture/AccessControl</c> → "The convergence
/// contract"). Until the fan-in had a terminal, a policy read whose provider never answered
/// therefore made the fold produce nothing at all — no value, no completion, no error — and the
/// permissive shape available to the fan-in instead (count the provider empty and NAME it) is
/// exactly the hole that seeding rule rules out. Making the stall an ERROR is the only sound
/// terminal, and it is sound only because this test's assertion holds.</para>
///
/// <para><b>The shape.</b> ONE user, ONE role, TWO partitions — and a provider that stalls the
/// <c>_Policy</c> read of one of them. The control partition therefore proves the terminal has not
/// become a blanket denial, on the same mesh, the same fold and the same budget; and the stalled
/// partition proves the direction that matters, with a caller who genuinely IS entitled: the answer
/// is neither <c>granted</c> (a hole) nor <c>denied</c> (a lie that sends a correctly-entitled
/// caller to request permissions they already hold, and files an availability incident as a policy
/// decision — #974) but <c>Undetermined</c> ⇒ <c>IsGranted == false</c>, retryable, attributed to
/// the read.</para>
///
/// <para><b>The provider is a test PROVIDER, not a mock of a core service.</b>
/// <see cref="IMeshQueryProvider"/> is the seam every storage backend plugs into, and "subscribed,
/// and never heard from again" is the observed production shape (the cross-silo case, where the
/// owning activation lives on a peer silo that is busy or has just gone away). Everything else is
/// the real mesh: the real <c>MeshQuery</c> merge, the real <c>IMeshNodeStreamCache</c>, the real
/// <c>PermissionEvaluator</c> fold, the real <c>CheckPermissionOutcome</c> classifier.</para>
/// </summary>
public class StalledPolicyReadFailsClosedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The partition whose <c>_Policy</c> read is subscribed and never answered.</summary>
    private const string StalledSpace = "stalledpolicy";

    /// <summary>The control partition — same user, same grant, a policy read that answers.</summary>
    private const string HealthySpace = "healthypolicy";

    /// <summary>The entitled caller. Holds Admin in BOTH partitions, which is what makes the stalled
    /// case a statement about the READ rather than about this user's rights.</summary>
    private const string Entitled = "entitled-user";

    /// <summary>
    /// Four seconds contracts to 2 s / 1 s / 500 ms, so the fan-in's rung fires in half a second and
    /// still strictly INSIDE the fold's own 1 s establishment budget — the ordering the whole
    /// attribution depends on (#1198). Written as a literal because it is the subject, not a wait.
    /// </summary>
    private static readonly TimeSpan ShortLadder = TimeSpan.FromSeconds(4);

    private readonly StalledPolicyProvider provider = new(StalledSpace);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => Bootstrap.Bootstrap(builder)
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(provider))
            .ConfigureHub(c => c
                .WithMeshOperationTimeout(ShortLadder)
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    [Fact(Timeout = 240_000)]
    public async Task AStalledPolicyRead_IsNotAVerdict_AndTheEntitledCallerIsNotGranted()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        await CreateSpace(meshService, HealthySpace, ct);
        await CreateSpace(meshService, StalledSpace, ct);
        await Grant(meshService, access, HealthySpace, ct);
        await Grant(meshService, access, StalledSpace, ct);

        // ── THE CONTROL, and it runs FIRST on purpose: if a grant cannot produce a definite ALLOW
        //    on this mesh at all, the assertion below would pass for the wrong reason.
        var healthy = await Mesh.CheckPermissionOutcome(HealthySpace, Entitled, Permission.Read)
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("the control partition's fold has every leg answered, so it must reach a verdict", ct);
        Output.WriteLine($"healthy partition: granted={healthy.IsGranted} undetermined={healthy.IsUndetermined} "
                         + $"reason={healthy.UndeterminedReason}");
        healthy.IsUndetermined.Should().BeFalse(
            "every leg of this partition's fold answers, so the fold reaches a verdict");
        healthy.IsGranted.Should().BeTrue(
            "the user holds Admin here — a terminal on a DIFFERENT partition's policy read must not "
            + "turn the fan-in into a blanket denial");

        // ── THE CHANGE. Same user, same role, one partition whose _Policy read never answers.
        // 🚨 This wait IS the negative control. With the stall terminal disarmed the fold produces no
        // outcome at all — measured on this branch: the wait does not complete and the test fails
        // having received nothing, which is the hang this change removes. With it armed the outcome
        // arrives and the three assertions below decide WHICH outcome it is.
        var stalled = await Mesh.CheckPermissionOutcome(StalledSpace, Entitled, Permission.Read)
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("a fold whose policy leg cannot be read must still reach a terminal", ct);
        Output.WriteLine($"stalled partition: granted={stalled.IsGranted} undetermined={stalled.IsUndetermined} "
                         + $"reason={stalled.UndeterminedReason}");

        stalled.IsGranted.Should().BeFalse(
            "FAIL CLOSED. The policy leg carries the permission cap and the BreaksInheritance "
            + "boundary, and its absence WIDENS — so a fold that could not read it must never let "
            + "the operation proceed, however entitled the caller looks from the grant leg alone");
        stalled.IsUndetermined.Should().BeTrue(
            "and it must not be reported as a DENIAL either: no verdict was reached, so the honest "
            + "answer is an availability failure (retryable, ErrorType.Unavailable), never "
            + "'Access denied' — which would send a correctly-entitled caller to request "
            + "permissions they already hold and file an outage as a policy decision (#974)");
        stalled.UndeterminedReason.Should().Contain(nameof(StalledPolicyProvider),
            "the reason has to NAME the provider that starved — that attribution is the only thing "
            + "the fan-in's rung of the ladder exists to produce, and no level above it can");
    }

    private static async Task CreateSpace(IMeshService meshService, string id, CancellationToken ct)
        => await meshService.CreateNode(new MeshNode(id)
        {
            Name = id, NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active, Content = new Space(),
        }).Should().Within(TestTimeouts.CrossSilo).Emit($"the creator may create the Space '{id}'",
            cancellationToken: ct);

    private static async Task Grant(
        IMeshService meshService, AccessService access, string space, CancellationToken ct)
    {
        using (access.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode($"{Entitled}_Access", $"{space}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{Entitled} Access",
                MainNode = space,
                Content = new AccessAssignment
                {
                    AccessObject = Entitled,
                    DisplayName = Entitled,
                    Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }],
                },
            }).Should().Within(TestTimeouts.CrossSilo).Emit(
                $"the grant write for '{space}' must complete", cancellationToken: ct);
        }
    }

    /// <summary>
    /// Subscribed, and never heard from again — for the <c>_Policy</c> read of ONE partition only.
    ///
    /// <para>🚨 <b>Deliberately SILENT rather than throwing, and narrowed to one query shape.</b> A
    /// throwing provider already had a terminal and already propagated; silence is the case with no
    /// representation, which is the whole subject. Narrowing it to
    /// <c>SecurityQueries.PartitionPolicies(StalledSpace)</c>'s shape means every other read in the
    /// mesh — the control partition's policy leg, both grant legs, memberships, the gated-node fold,
    /// and every node operation this test performs — is untouched, so the two assertions differ in
    /// exactly one fact.</para>
    /// </summary>
    private sealed class StalledPolicyProvider(string stalledPartition) : IMeshQueryProvider
    {
        public string Name => nameof(StalledPolicyProvider);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        /// <summary>Test probe: how many times the stalled shape has been subscribed. Read by no
        /// assertion above, written so a failure can say whether the shape was matched at all.</summary>
        public int StalledSubscriptions;

        public IObservable<QueryResultChange<T>> Query<T>(
            MeshQueryRequest request, JsonSerializerOptions options)
        {
            if (IsTheStalledPolicyRead(request))
            {
                System.Threading.Interlocked.Increment(ref StalledSubscriptions);
                return Observable.Never<QueryResultChange<T>>();
            }

            // Everything else: answer EMPTY, promptly, exactly as a provider that owns nothing
            // matching should. An Initial is mandatory — returning Observable.Empty here would make
            // this provider the "completed without an Initial" case and change what is under test.
            return Observable.Return(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Items = Array.Empty<T>(),
                Timestamp = DateTimeOffset.UtcNow,
            });
        }

        private bool IsTheStalledPolicyRead(MeshQueryRequest request)
        {
            foreach (var query in request.EffectiveQueries)
                if (query.Contains($"path:{stalledPartition}", StringComparison.OrdinalIgnoreCase)
                    && query.Contains("id:_Policy", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public IObservable<IReadOnlyCollection<QueryResult>> Query(
            MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }
}
