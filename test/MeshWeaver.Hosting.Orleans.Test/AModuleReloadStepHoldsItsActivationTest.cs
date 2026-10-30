using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// #7324: a module reload's step HOLDS the request's activation, and a step the hub is torn down
/// under is reported once, at Warning, with who tore the hub down and why.
///
/// <para><b>The shape.</b> A reload request is written and read by System only, so its activation
/// retires the moment its last subscriber goes (#6623) — the read <c>ModuleReload.Request</c> makes
/// to activate the executor leaves a cache mirror, and the cache's idle sweep releases it ten minutes
/// later. An all-modules landing reloads module after module and writes nothing to its node in
/// between, so nothing it does counts as a subscriber: unheld, the activation was retired MID-landing,
/// the executor's pipeline (which lives on the hub's disposal) went with it, and the next activation
/// started the landing over.</para>
///
/// <para><b>The reproduction.</b> The executor is the production one; only its landing is parked on a
/// test-owned gate (<see cref="ParkedLandings"/>), because a real landing is long only against a real
/// registry. The mirror's release is <see cref="IMeshNodeStreamCache.ReleaseIfUnwatched"/> — the event
/// the idle sweep ends in, minus its ten-minute wait — exactly as
/// <see cref="AnInfrastructureActivationEndsWithItsLastSubscriberTest"/> drives it.</para>
/// </summary>
public class AModuleReloadStepHoldsItsActivationTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(ParkedReloadSiloConfigurator);

    private IMessageHub SiloMesh =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services.GetRequiredService<IMessageHub>();

    /// <summary>
    /// 🚨 THE PIN. The request's last subscriber goes while its landing is in flight: the activation
    /// must NOT be retired under the landing, and once the landing ends the hold is released, so the
    /// retirement that fell due during it is taken then. On unfixed code the hub is disposed within
    /// moments of the release — mid-landing — and the first assertion fails.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARetirementDueDuringTheLanding_WaitsForIt_AndIsTakenWhenTheLandingEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, hub) = await ARequestParkedInItsLanding("hold");
        var landings = SiloServices().GetRequiredService<ParkedLandings>();
        var warnings = SiloServices().GetRequiredService<ReloadWarnings>();

        // The last subscriber goes WHILE the landing runs — the cache's idle sweep, without its wait.
        await ReleaseWhenUnwatched(SiloServices().GetRequiredService<IMeshNodeStreamCache>(), path);

        await hub.DisposalCompleted.Take(1).Should().NotEmit(TimeSpan.FromSeconds(3),
            $"{path} is landing modules: the step holds its activation, so the retirement that fell due "
            + "when its last subscriber left must wait for it — an unheld activation was retired "
            + "MID-landing and the next activation started the landing over, 13 times on one request (#7324)", ct);

        landings.Release(path);

        await hub.DisposalCompleted.Take(1).Should().Within(TestTimeouts.Convergence).Emit(
            "the landing ended, so its hold is released — and the retirement that fell due while it was up "
            + "is taken now; a hold that is never released keeps the activation for good", ct);
        warnings.Lines.Where(l => l.Contains(path, StringComparison.Ordinal) && l.Contains("torn down while", StringComparison.Ordinal)).Should().BeEmpty(
            "negative control: the landing ENDED before the activation was retired, so no step was torn "
            + "down and nothing may say one was");
        landings.Starts(path).Should().Be(1, "one landing, never started over");
    }

    /// <summary>
    /// The hold defers only idle retirement — a recycle still tears the hub down. When it does under
    /// a step in flight, ONE Warning names the step and carries the hub's own disposal attribution,
    /// so the next occurrence of #7324 says what restarted the landing. On unfixed code nothing at
    /// Warning is logged: the step lines and the dispose reason are Information, which the cluster
    /// does not ship.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AHubTornDownUnderALandingStep_SaysOnceWhoTookItDownAndWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, hub) = await ARequestParkedInItsLanding("recycle");
        var warnings = SiloServices().GetRequiredService<ReloadWarnings>();

        var reason = $"test recycle {Guid.NewGuid():N}";
        SiloMesh.NodeOperationIssuingHub().Post(new DisposeRequest { Reason = reason },
            o => o.WithTarget(new Address(path)));
        await hub.DisposalCompleted.Take(1).Should().Within(TestTimeouts.Convergence)
            .Emit("the routed recycle tears the request's hub down", ct);

        var lines = warnings.Lines.Where(l => l.Contains(path, StringComparison.Ordinal) && l.Contains("torn down while", StringComparison.Ordinal)).ToImmutableList();
        lines.Should().ContainSingle("ONE warning per step torn down — never one per hub or per frame")
            .Which.Should().Contain("landing step was in flight").And.Contain(reason,
                "the warning carries MessageHub.DisposalAttribution — WHY the hub was torn down, in the "
                + "poster's own words — which is the reading #7324 could not take");
    }

    private async Task<(string Path, IMessageHub Hub)> ARequestParkedInItsLanding(string tag)
    {
        var ct = TestContext.Current.CancellationToken;
        var access = SiloMesh.ServiceProvider.GetRequiredService<AccessService>();
        var mesh = SiloMesh.ServiceProvider.GetRequiredService<IMeshService>();
        var landings = SiloServices().GetRequiredService<ParkedLandings>();
        var path = $"reloadhold/{tag}-{Guid.NewGuid():N}";

        // Written AS SYSTEM, as ModuleReload.Request writes it — the executor acts on nothing else.
        await access.RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
            {
                Name = "A reload whose landing is parked",
                NodeType = ParkedReloadSiloConfigurator.NodeType,
                State = MeshNodeState.Active,
                Content = new ModuleReloadRequest { Reason = "test: a long all-modules landing" },
            }).Take(1))
            .Should().Within(TestTimeouts.Convergence).Emit($"System writes {path}", ct);

        // …and then reads it once, which is what activates the executor (ModuleReload.Request).
        await access.RunAsSystem(() => SiloMesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null).Take(1))
            .Should().Within(TestTimeouts.Convergence).Emit($"System's read of {path} activates its hub", ct);

        await landings.Entered(path).Should().Within(TestTimeouts.Convergence)
            .Emit("the executor reaches its landing and parks there — the precondition for everything", ct);
        var hub = SiloMesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        hub.Should().NotBeNull("the landing runs on the request's own live hub");
        return (path, hub!);
    }

    private static Task ReleaseWhenUnwatched(IMeshNodeStreamCache cache, string path) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => cache.ReleaseIfUnwatched(path))
            .Where(released => released)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(
                $"the mirror of {path} has no reader left once the activating read's Take(1) completed",
                TestContext.Current.CancellationToken);
}

/// <summary>
/// Landings parked until the test releases them, per request path. Instance state on a singleton
/// the silo's mesh owns — never static — so it dies with the cluster.
/// </summary>
public sealed class ParkedLandings
{
    private readonly ConcurrentDictionary<string, AsyncSubject<Unit>> gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> starts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReplaySubject<string> entered = new();

    /// <summary>Emits once a landing for <paramref name="path"/> has started and parked.</summary>
    public IObservable<string> Entered(string path) =>
        entered.Where(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).Take(1);

    /// <summary>How many landings ever started for <paramref name="path"/>.</summary>
    public int Starts(string path) => starts.TryGetValue(path, out var n) ? n : 0;

    /// <summary>Ends every landing of <paramref name="path"/>: it answers that nothing could be
    /// resolved, so the executor finishes the request.</summary>
    public void Release(string path)
    {
        var gate = gates.GetOrAdd(path, _ => new AsyncSubject<Unit>());
        gate.OnNext(Unit.Default);
        gate.OnCompleted();
    }

    /// <summary>The landing the executor runs — cold: it starts (and counts) on subscribe.</summary>
    internal IObservable<ModuleReloadLanding> Land(string path) =>
        Observable.Defer(() =>
        {
            starts.AddOrUpdate(path, 1, (_, n) => n + 1);
            var gate = gates.GetOrAdd(path, _ => new AsyncSubject<Unit>());
            entered.OnNext(path);
            return gate.Select(_ => new ModuleReloadLanding(ImmutableList<ModuleReloadItem>.Empty,
                "test: the parked landing was released"));
        });
}

/// <summary>Every Warning the module reload executor logs, as rendered text. A logger provider
/// the silo's logger factory picks up from DI.</summary>
public sealed class ReloadWarnings : ILoggerProvider
{
    private readonly ConcurrentQueue<string> lines = new();

    /// <summary>The rendered warnings so far.</summary>
    public IReadOnlyList<string> Lines => lines.ToArray();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        categoryName.EndsWith(nameof(ModuleReloadExecutor), StringComparison.Ordinal)
            ? new Recorder(lines)
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <inheritdoc />
    public void Dispose() { }

    private sealed class Recorder(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                lines.Enqueue(formatter(state, exception));
        }
    }
}

/// <summary>
/// The stock silo, plus the module reload (types and the per-process agent) and a request node
/// type whose hub arms the PRODUCTION executor with its landing parked on <see cref="ParkedLandings"/>.
/// </summary>
public class ParkedReloadSiloConfigurator : SharedSiloConfigurator
{
    /// <summary>The test's request node type.</summary>
    public const string NodeType = "ParkedModuleReload";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureAdditional(MeshBuilder builder) =>
        builder.AddModuleReload()
            .AddMeshNodes(new MeshNode(NodeType)
            {
                Name = "Parked module reload",
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<ModuleReloadRequest>())
                    .WithInitialization(hub => ModuleReloadExecutor.Arm(hub,
                        (h, _) => h.ServiceProvider.GetRequiredService<ParkedLandings>().Land(ModuleReloadExecutor.PathOf(h)))),
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<ParkedLandings>();
                services.AddSingleton<ReloadWarnings>();
                services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<ReloadWarnings>());
                return services;
            });
}
