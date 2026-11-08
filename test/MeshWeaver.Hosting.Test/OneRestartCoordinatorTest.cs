using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>ONE restart coordinator for every retirement</b> (MeshWeaver.Plugins#5055, PR C). A package
/// uninstall that disabled a module some process still holds no longer restarts the instance on its
/// own: it states facts on <see cref="PackageRestartCoordinator.Path"/>, and that node's own hub
/// dispatches ONE restart for the batch, records the structured outcome, retries a faulted dispatch
/// without repeating phase 1 and never repeats an accepted one. Real mesh, the real uninstall
/// executor, a real landing service on a temporary module root, real landed modules, the real
/// coordinator; only the restart lane (the self-updater) is scripted, and it counts.
/// </summary>
public class OneRestartCoordinatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ModuleA = "MeshWeaver.RestartProbeA";
    private const string ModuleB = "MeshWeaver.RestartProbeB";

    private readonly string moduleRoot = Path.Combine(Path.GetTempPath(), "mw-pc-" + Guid.NewGuid().ToString("N")[..8]);
    private ModuleLandingService? landing;

    /// <summary>Runs inside a removal, after its tombstone — a test parks one there.</summary>
    private Action<string>? beforeProjecting;

    /// <summary>How the restart lane answers its n-th call (1-based); null: it takes the restart.</summary>
    private Func<int, ModuleRestartOutcome>? script;

    /// <summary>How many times the restart lane was asked.</summary>
    private int restarts;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddPluginCatalog()
        .ConfigureServices(services => services
            .AddSingleton(_ => landing ??= new ModuleLandingService(null, EnsureDirectory(moduleRoot), null,
                beforeProjecting: name => beforeProjecting?.Invoke(name)))
            // Both probe modules are loaded in this process and there is no live loader: unloading
            // them takes a restart.
            .AddSingleton(_ => new ModuleReloadAgent
            {
                LoadedNames = () => ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, ModuleA, ModuleB),
            })
            .AddSingleton<IModuleActivationRestart>(_ => new CountingRestart(this)));

    private sealed class CountingRestart(OneRestartCoordinatorTest test) : IModuleActivationRestart
    {
        public IObservable<ModuleRestartOutcome> RequestRestart(string reason) => Observable.Defer(() =>
        {
            var call = Interlocked.Increment(ref test.restarts);
            test.log.WriteLine($"restart lane asked (call {call}): {reason}");
            return Observable.Return(test.script?.Invoke(call)
                                     ?? new ModuleRestartOutcome(ModuleRestartKinds.Restarted, "rolled its own workloads (test)"));
        });
    }

    private readonly ITestOutputHelper log = output;

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private ModuleLandingService Landing => Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private async Task Installed(string id, string module, CancellationToken ct)
    {
        await Landing.LandModule(module, [(module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))], version: "1.0.0")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var record = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{id}") with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = id,
            State = MeshNodeState.Active,
            Content = new PackageManifest { Id = id, Name = id, Source = "Plugins", Version = "1.0.0", TargetPartition = id, Module = module },
        };
        await InstallRecordGate.Write(Mesh, record, PackageRecordWriteIntent.CreateAllowed, NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(
                MeshNode.FromPath(id) with { NodeType = "Markdown", Name = id, State = MeshNodeState.Active }))
            .Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private async Task<string> Request(string id, CancellationToken ct)
    {
        var ticket = await PackageUninstall.Request(Mesh, new PackageUninstallRequest { Package = id, Reason = "test", RequestedBy = "someone" })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.NotNull(ticket.Path);
        return ticket.Path!;
    }

    private Task<PackageUninstallRequest> PhaseOneDone(string path, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path))
            .Select(n => n.ContentAs<PackageUninstallRequest>(Mesh.JsonSerializerOptions))
            .OfType<PackageUninstallRequest>()
            .Where(r => r.Status == PackageUninstallStatus.AwaitingConfirmation || PackageUninstallStatus.IsTerminal(r.Status))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>The coordinator once it satisfies <paramref name="until"/>. It may not exist yet, so
    /// its existence is re-read from storage first — never a point read of an absent node.</summary>
    private async Task<PackageRestartCoordinatorState> Coordinator(Func<PackageRestartCoordinatorState, bool> until, CancellationToken ct)
    {
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.Read(PackageRestartCoordinator.Path, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null))
            .Where(n => n is not null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        return await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(PackageRestartCoordinator.Path))
            .Select(n => n.ContentAs<PackageRestartCoordinatorState>(Mesh.JsonSerializerOptions))
            .OfType<PackageRestartCoordinatorState>()
            .Where(until)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>
    /// Two uninstalls running AT ONCE produce ONE restart, covering both. Deterministic: the first
    /// one's module removal is parked until the second has told the coordinator it is pending, so
    /// the first is ready while the second still retires. Negative control (measured): with the
    /// coordinator not waiting for a pending member, the first dispatches alone and the second
    /// dispatches again — two restarts.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TwoConcurrentUninstalls_DispatchOneRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed("RestartPkgA", ModuleA, ct);
        await Installed("RestartPkgB", ModuleB, ct);
        var release = 0;
        beforeProjecting = name =>
        {
            if (string.Equals(name, ModuleA, StringComparison.OrdinalIgnoreCase))
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
        };
        try
        {
            var requests = await Task.WhenAll(Request("RestartPkgA", ct), Request("RestartPkgB", ct));
            var b = requests[1];
            // B is in its phase 1 and has said so — then A's removal may finish.
            await Coordinator(s => s.Members?.ContainsKey(b) == true, ct);
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        var paths = await Task.WhenAll(
            (await Coordinator(s => s.Members?.Count >= 2 && s.Members.Values.All(m => m.State == PackageRestartMemberState.Covered), ct))
                .Members!.Keys.Select(p => PhaseOneDone(p, ct)));

        Assert.All(paths, r => Assert.True(r.Status == PackageUninstallStatus.AwaitingConfirmation, r.Failure ?? r.Status));
        Assert.All(paths, r => Assert.Contains("one restart requested", r.ModuleOutcome ?? ""));
        var state = await Coordinator(s => s.Dispatches?.Values.All(d => d.State == PackageRestartDispatchState.Accepted) == true, ct);
        var dispatch = Assert.Single(state.Dispatches!.Values);
        Assert.Equal(2, dispatch.Covers.Count);
        Assert.Equal(1, Volatile.Read(ref restarts));
    }

    /// <summary>
    /// A FAULTED dispatch is recorded retryable and retried by the reconcile pass — once its backoff is
    /// due, never before — and then succeeds; phase 1 is NOT repeated (one "uninstalling" line, the
    /// preview's instant unchanged). In-test control: a retry pass whose backoff is not due retries
    /// nothing. Negative control (measured): with the owner ignoring the retry, the dispatch stays
    /// faulted.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AFaultedDispatch_IsRetriedOnce_AndSucceeds_WithoutRepeatingPhaseOne()
    {
        var ct = TestContext.Current.CancellationToken;
        script = call => call == 1
            ? new ModuleRestartOutcome(ModuleRestartKinds.Faulted, "the hand-over to the control lane was refused (test)")
            : new ModuleRestartOutcome(ModuleRestartKinds.Restarted, "rolled its own workloads (test)");
        await Installed("RestartPkgFault", ModuleA, ct);

        var path = await Request("RestartPkgFault", ct);
        var preview = await PhaseOneDone(path, ct);

        Assert.True(preview.Status == PackageUninstallStatus.AwaitingConfirmation, preview.Failure ?? preview.Status);
        Assert.Contains("faulted", preview.ModuleOutcome ?? "");
        var faulted = Assert.Single((await Coordinator(s => s.Dispatches?.Count == 1, ct)).Dispatches!.Values);
        Assert.Equal(PackageRestartDispatchState.Faulted, faulted.State);
        Assert.Equal(1, Volatile.Read(ref restarts));

        // In-test control: not due yet ⇒ nothing retried.
        Assert.Empty(await PackageRestartCoordinator.RetryFaulted(Mesh, TimeSpan.FromHours(1), DateTimeOffset.UtcNow)
            .Timeout(TestTimeouts.Convergence).Await(ct));
        Assert.Equal(1, Volatile.Read(ref restarts));

        var retried = await PackageRestartCoordinator.RetryFaulted(Mesh, TimeSpan.Zero, DateTimeOffset.UtcNow)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Equal(faulted.Id, Assert.Single(retried));
        var accepted = Assert.Single((await Coordinator(
            s => s.Dispatches?.Values.Any(d => d.State == PackageRestartDispatchState.Accepted) == true, ct)).Dispatches!.Values);
        Assert.Equal(2, accepted.Attempt);
        Assert.Equal(2, Volatile.Read(ref restarts));

        var after = await PhaseOneDone(path, ct);
        Assert.Equal(PackageUninstallStatus.AwaitingConfirmation, after.Status);
        Assert.Equal(preview.AwaitingConfirmationAt, after.AwaitingConfirmationAt);
        Assert.Single(after.Log, l => l.Contains("uninstalling 'RestartPkgFault'"));
    }

    /// <summary>
    /// An ACCEPTED dispatch is never asked again — not after the coordinator's own hub is recycled and
    /// re-activated, and not when the same request states it is ready again (a resumed phase 1).
    /// Negative control (measured): with a covered member treated as ready again, the restart lane is
    /// asked a second time.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnAcceptedDispatch_IsNeverRepeated_EvenAfterTheOwnerReactivates()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed("RestartPkgOnce", ModuleA, ct);
        var path = await Request("RestartPkgOnce", ct);
        Assert.Equal(PackageUninstallStatus.AwaitingConfirmation, (await PhaseOneDone(path, ct)).Status);
        var first = Assert.Single((await Coordinator(s => s.Dispatches?.Count == 1, ct)).Dispatches!.Values);
        Assert.Equal(PackageRestartDispatchState.Accepted, first.State);
        Assert.Equal(1, Volatile.Read(ref restarts));

        await GetClient().RecycleNode(PackageRestartCoordinator.Path,
                reason: "OneRestartCoordinatorTest: re-activate the coordinator after its accepted dispatch")
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        var again = await PackageRestartCoordinator.Ready(Mesh, path, ModuleA, "a resumed phase 1", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        Assert.NotNull(again);
        Assert.Equal(first.Id, again!.Id);
        Assert.Equal(PackageRestartDispatchState.Accepted, again.State);
        var state = await Coordinator(s => s.Facts is null, ct);
        Assert.Single(state.Dispatches!.Values);
        Assert.Equal(1, Volatile.Read(ref restarts));
    }

    /// <summary>
    /// A dispatch another activation left in flight — the lane may have taken it and rolled that
    /// process before its answer was written — is recorded Interrupted and never asked again.
    /// Negative control (measured): with a foreign in-flight dispatch adopted as this activation's
    /// own, the restart lane is asked.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ADispatchInterruptedInFlight_IsNeverAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        const string request = PackageUninstallRequest.Namespace + "/interrupted-probe";
        // Creates the coordinator (as System), stating nothing that dispatches.
        await PackageRestartCoordinator.Withdraw(Mesh, request, NullLogger.Instance).Timeout(TestTimeouts.Convergence).Await(ct);
        // The state a dead activation left: its dispatch persisted, the lane's answer never recorded.
        var issuer = Mesh.NodeOperationExecutionHub() ?? Mesh;
        await Access.RunAsSystem(() => issuer.GetMeshNodeStream(PackageRestartCoordinator.Path)
                .Update<PackageRestartCoordinatorState>(s => s with
                {
                    DispatchCount = 7,
                    Members = ImmutableDictionary<string, PackageRestartMember>.Empty.Add(request, new PackageRestartMember
                    {
                        State = PackageRestartMemberState.Covered, Module = ModuleA, CoveredBy = "restart-7", At = DateTimeOffset.UtcNow,
                    }),
                    Dispatches = ImmutableDictionary<string, PackageRestartDispatch>.Empty.Add("restart-7", new PackageRestartDispatch
                    {
                        Id = "restart-7",
                        Covers = [request],
                        Modules = [ModuleA],
                        State = PackageRestartDispatchState.Dispatching,
                        Activation = "an-activation-that-died",
                        RequestedAt = DateTimeOffset.UtcNow,
                    }),
                }))
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);

        var answer = await PackageRestartCoordinator.Ready(Mesh, request, ModuleA, "a resumed phase 1", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        Assert.NotNull(answer);
        Assert.Equal(PackageRestartDispatchState.Interrupted, answer!.State);
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    /// <summary>
    /// A person-requested uninstall still restarts EXACTLY once, and says so in its module outcome.
    /// Negative control (measured): with a pending member's ready fact not replacing its pending
    /// state, the request holds its own restart back and the lane is never asked.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APersonRequestedUninstall_RestartsExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed("RestartPkgPerson", ModuleB, ct);

        var request = await PhaseOneDone(await Request("RestartPkgPerson", ct), ct);

        Assert.True(request.Status == PackageUninstallStatus.AwaitingConfirmation, request.Failure ?? request.Status);
        Assert.Equal("someone", request.RequestedBy);
        Assert.Contains("one restart requested (Restarted", request.ModuleOutcome ?? "");
        Assert.Equal(1, Volatile.Read(ref restarts));
    }

    /// <summary>
    /// A request persisted BEFORE the coordinator existed, that already stamped its own
    /// <c>RestartRequestedAt</c>, is never restarted again when its phase 1 resumes on the new code —
    /// the in-flight compatibility rule. Negative control (measured): without the legacy check, the
    /// resumed phase 1 asks the coordinator and the lane is asked once more.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ALegacyRequestThatAlreadyAskedItsRestart_IsNotRestartedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed("RestartPkgLegacy", ModuleA, ct);
#pragma warning disable CS0618 // the legacy field, as a request persisted before the coordinator carries it
        var legacy = new PackageUninstallRequest
        {
            Package = "RestartPkgLegacy", Reason = "test", RequestedBy = "someone", RestartRequestedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
#pragma warning restore CS0618
        var ticket = await PackageUninstall.Request(Mesh, legacy).Timeout(TestTimeouts.Convergence).Await(ct);

        var request = await PhaseOneDone(ticket.Path!, ct);

        Assert.True(request.Status == PackageUninstallStatus.AwaitingConfirmation, request.Failure ?? request.Status);
        Assert.Contains("already asked its one restart", request.ModuleOutcome ?? "");
        Assert.Equal(0, Volatile.Read(ref restarts));
    }
}

/// <summary>
/// 🚨 <b>Only the platform states restart facts</b> (#8008 review). A <c>Ready</c> on the coordinator
/// drives an instance restart, and a <c>Pending</c> or <c>Withdraw</c> naming another request could
/// hold its restart back. So an ordinary authenticated user, who holds no grant in the Admin
/// partition, must not be able to state one. This class stands the mesh up WITHOUT the test base's
/// public-admin grant (<see cref="MonolithMeshTestBase.ConfigureMeshBase"/>), so the identity writing
/// is judged by the real row-level security. In-test control: the same kind of fact stated through
/// the production writer, which runs as System, IS folded — the barrier read detects an accepted fact.
/// </summary>
public class ARestartFactFromAnOrdinaryUserIsRefusedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Intruder = "ordinary-user-no-admin-grant";
    private readonly ITestOutputHelper log = output;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder).AddPluginCatalog();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IMessageHub Issuer => Mesh.NodeOperationExecutionHub() ?? Mesh;

    private Task<PackageRestartCoordinatorState> Coordinator(Func<PackageRestartCoordinatorState, bool> until, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(PackageRestartCoordinator.Path))
            .Select(n => n.ContentAs<PackageRestartCoordinatorState>(Mesh.JsonSerializerOptions))
            .OfType<PackageRestartCoordinatorState>()
            .Where(until)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    [Fact(Timeout = 180_000)]
    public async Task AFactWrittenByAnOrdinaryUser_IsRefused_AndHasNoEffect()
    {
        var ct = TestContext.Current.CancellationToken;
        const string victim = PackageUninstallRequest.Namespace + "/victim-request";
        const string forged = PackageUninstallRequest.Namespace + "/forged-request";
        const string barrier = PackageUninstallRequest.Namespace + "/barrier-request";
        // The coordinator exists, created by System through the production writer.
        await PackageRestartCoordinator.Pending(Mesh, victim, "MeshWeaver.Victim", "a real uninstall", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        await Coordinator(s => s.Members?.ContainsKey(victim) == true, ct);

        // The intruder states a Ready for a request of its own (it would drive a restart) and a
        // Withdraw for the victim (it would drop the victim's pending place), straight onto the node.
        var write = await Access.RunAs(new AccessContext { ObjectId = Intruder, Name = "Ordinary User" },
                () => Issuer.GetMeshNodeStream(PackageRestartCoordinator.Path)
                    .Update<PackageRestartCoordinatorState>(s => s with
                    {
                        Facts = (s.Facts ?? ImmutableDictionary<string, PackageRestartFact>.Empty)
                            .SetItem("ready-forged", new PackageRestartFact
                            {
                                Kind = PackageRestartFactKind.Ready, Request = forged, Module = "MeshWeaver.Forged", At = DateTimeOffset.UtcNow,
                            })
                            .SetItem("withdraw-forged", new PackageRestartFact
                            {
                                Kind = PackageRestartFactKind.Withdraw, Request = victim, At = DateTimeOffset.UtcNow,
                            }),
                    }))
            .Take(1)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        log.WriteLine($"the intruder's write answered: {(write is null ? "SUCCESS" : $"{write.GetType().Name}: {write.Message}")}");

        // Barrier: a later fact through the production writer. Once it is folded, any fact that had
        // reached the node before it was folded too.
        await PackageRestartCoordinator.Pending(Mesh, barrier, "MeshWeaver.Barrier", "barrier", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var state = await Coordinator(s => s.Members?.ContainsKey(barrier) == true && s.Facts is null, ct);

        Assert.NotNull(write);
        Assert.False(state.Members!.ContainsKey(forged), "an ordinary user's Ready never enrols a request");
        Assert.True(state.Members.ContainsKey(victim), "an ordinary user's Withdraw never drops another request");
        Assert.Null(state.Dispatches);
    }
}
