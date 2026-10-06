using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #6056 — <b>a release re-cut cut short because THIS PROCESS IS LEAVING is not a release
/// fault</b>, and must not be reported as one.
///
/// <para><b>The incident, as measured.</b> Both production bursts of <c>[ReleasePostCondition] … AND
/// the release could not be re-cut: … Node creation at '…' was cancelled before it completed</c>
/// (memex, 2026-10-03 23:15:58Z on pod <c>57d6d7f9cc-tslft</c>; 2026-10-05 11:25:57Z on
/// <c>5dcbbd8f4b-jqqxx</c>) fall in the same millisecond as that pod's own
/// <c>Host is shutting down, cannot route</c> lines (governed Logs actions
/// <c>Ops/Actions/logs-6056-{tslft,jqqxx}-shutdown-20261006</c>). The pod was being replaced by a
/// roll; its mesh was draining its I/O pools, and CreateNode's cooperative-cancellation arm answered
/// the release create <c>Unavailable</c>. Nothing was left advertising an unreleased build: the
/// version history of every named NodeType has NO write at that instant (the dying process's
/// terminal stamp never landed), and the replacing replica re-compiled and cut the release 16–41 s
/// later. The ERROR — and the sev:H incident filed from it — reported a pod being replaced.</para>
///
/// <para><b>The control, on a real mesh.</b> A real <see cref="INodeValidator"/> in the mesh's own
/// service collection fails the Release create for this test's type with the cancellation a drained
/// pool raises, so the re-cut runs the PRODUCTION create path to the production answer ("Node
/// creation at '…' was cancelled before it completed."). The process's leaving is the host's real
/// <see cref="IHostApplicationLifetime"/>, stopped through <see cref="IHostApplicationLifetime.StopApplication"/>
/// exactly as SIGTERM stops it. Leaving ⇒ the verdict is a keyed WARNING naming the shutdown and no
/// ERROR is logged; the negative control runs the identical failure on a process that is NOT leaving
/// and must still be an ERROR — so the test cannot pass by having stopped reporting failures.</para>
/// </summary>
public class AReleaseRecutCutShortByShutdownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Path infix that opts a Release create INTO the cancellation; nothing else is touched.</summary>
    private const string CutShortMarker = "ReleaseCutShortByShutdown";

    /// <summary>The exact sentence CreateNode answers a cooperatively cancelled create with.</summary>
    private const string CancelledAnswer = "was cancelled before it completed";

    /// <summary>
    /// Fails a Release create under <see cref="CutShortMarker"/> with the cancellation a draining
    /// I/O pool raises — so CreateNode's own cooperative-cancellation arm produces the answer.
    /// </summary>
    private sealed class DrainedPoolReleaseValidator : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => context.Node is { Path: { } path } node
               && path.Contains(CutShortMarker, StringComparison.Ordinal)
               && string.Equals(node.NodeType, GraphNodeTypeNames.Release, StringComparison.Ordinal)
                ? Observable.Throw<NodeValidationResult>(new OperationCanceledException(
                    "The I/O pool was drained (mesh or silo teardown) — this leaf was cancelled before it produced a result."))
                : Observable.Return(NodeValidationResult.Valid());
    }

    /// <summary>Records every line the post-condition logs, with its level. Instance state only.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((logLevel, formatter(state, exception)));
    }

    // Field initializers run before the base constructor calls ConfigureMesh, so these instances are
    // the ones registered below. A real host purely for its real ApplicationLifetime — the concrete
    // type the runtime cancels on SIGTERM; it is never started, so stopping it stops nothing else.
    private readonly IHostApplicationLifetime lifetime =
        new HostBuilder().Build().Services.GetRequiredService<IHostApplicationLifetime>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<INodeValidator>(new DrainedPoolReleaseValidator())
                .AddSingleton(lifetime));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static ImmutableDictionary<string, long> Sources(string typePath, long version) =>
        ImmutableDictionary<string, long>.Empty.Add($"{typePath}/Source/SignatureModel", version);

    /// <summary>The compile that just succeeded — store version 3925, the incident's build.</summary>
    private static NodeCompilationResult Built(string typePath) => new(
        AssemblyLocation: $"/cache/{typePath.Replace('/', '_')}/SignatureRequest.dll",
        NodeTypeConfigurations: [],
        CompiledSources: Sources(typePath, 3925),
        Collection: "local",
        ContentPath: $"{typePath.Replace('/', '_')}/v3925-c003e001-838a58440794.dll",
        Version: 3925);

    /// <summary>The definition as the compile watcher observed it at dispatch: a CONSUMED request
    /// and a release cut for the PREVIOUS build (3922) — the incident's <c>Signature/SignatureRequest</c>.</summary>
    private static NodeTypeDefinition Consumed(string typePath) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Compiling,
        RequestedReleaseAt = new DateTimeOffset(2026, 10, 4, 0, 22, 9, TimeSpan.Zero),
        LastReleaseRequestHandledAt = new DateTimeOffset(2026, 10, 4, 0, 22, 9, TimeSpan.Zero),
        LastCompiledVersion = 3922,
        LatestAssemblyCollection = "local",
        LatestAssemblyPath = $"{typePath.Replace('/', '_')}/v3922-c003e001-838a58440794.dll",
        LatestReleasePath = $"{typePath}/Release/20261005074547-g0RDQ68Z",
        CompiledSources = Sources(typePath, 3922),
    };

    private async Task<MeshNode> SeedTypeAsync(string typePath)
    {
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typePath,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = Consumed(typePath),
        };
        await MeshService.CreateNode(typeNode)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the NodeType whose release is re-cut must exist",
                cancellationToken: TestContext.Current.CancellationToken);
        return typeNode;
    }

    /// <summary>Runs the production remedy over the incident's shape: the settle's own create was
    /// cut short, and the re-cut meets the same cancellation at the owner.</summary>
    private async Task<(ReleasePostCondition.Settle Settle, RecordingLogger Log, string Attempted)> RestoreAsync(
        string discriminator, System.Threading.CancellationToken cancellationToken)
    {
        var typePath = $"{TestPartition}/{CutShortMarker}{discriminator}{Guid.NewGuid().ToString("N")[..8]}";
        var typeNode = await SeedTypeAsync(typePath);
        var result = Built(typePath);
        var attempted = $"{typePath}/{GraphNodeTypeNames.ReleaseSegment}/{DateTime.UtcNow:yyyyMMddHHmmss}-"
                        + NodeTypeBuildState.ContentHashOf(result);
        var firstAttempt = NodeTypeBuildState.ReleaseCreateOutcome.Failed(
            $"the create at '{attempted}' failed: InvalidOperationException: Node creation at '{attempted}' {CancelledAnswer}.",
            attempted);
        var log = new RecordingLogger();

        var settle = await ReleasePostCondition
            .Restore(Mesh, typePath, result, typeNode, activityPath: null, firstAttempt, log)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("Restore always answers exactly once", cancellationToken: cancellationToken);
        return (settle, log, attempted);
    }

    /// <summary>
    /// 🚨 THE CASE: the process is leaving, the re-cut is cut short at the owner — a keyed Warning
    /// naming the shutdown, no Error line, and the stamp still names the build that has no release.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARecutCutShortWhileTheProcessIsLeaving_IsAWarningNamingTheShutdown_NotAReleaseFault()
    {
        lifetime.StopApplication();
        Mesh.IsLeaving().Should().BeTrue("the host lifetime is stopping — the SIGTERM window of a roll");

        var (settle, log, attempted) = await RestoreAsync("Leaving", TestContext.Current.CancellationToken);

        settle.ReleasePath.Should().BeNull("the re-cut met the cancellation; no release exists");
        settle.Diagnosis.Should().NotBeNull();
        settle.Diagnosis!.Message.Should().Contain(CancelledAnswer,
            "the re-cut ran the production create path to the production answer — the reason still travels");
        settle.Diagnosis.MessageKey.Should().Be(ReleasePostCondition.AbandonedKey);
        settle.Diagnosis.LogLevel.Should().Be(LogLevel.Warning,
            "a pod being replaced has not failed to release anything");
        settle.UnreleasedBuildPath.Should().Be(attempted, "this build still has no release, and says where to look");
        settle.UnreleasedBuildReason.Should().Contain("leaving");
        log.Lines.Where(l => l.Level >= LogLevel.Error).Should().BeEmpty(
            "a re-cut cut short by the process's own shutdown must not reach the Error-level incident pipeline");
        log.Lines.Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains("THIS PROCESS IS LEAVING"));
    }

    /// <summary>
    /// The NEGATIVE CONTROL: the identical failure on a process that is NOT leaving stays the Error
    /// it has always been — the leaving branch is what changed the verdict, nothing else.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARecutCutShortOnALiveProcess_StaysAnErrorAndAViolation()
    {
        Mesh.IsLeaving().Should().BeFalse("the control process is not stopping");

        var (settle, log, attempted) = await RestoreAsync("Live", TestContext.Current.CancellationToken);

        settle.ReleasePath.Should().BeNull();
        settle.Diagnosis.Should().NotBeNull();
        settle.Diagnosis!.Message.Should().Contain(CancelledAnswer);
        settle.Diagnosis.MessageKey.Should().Be(ReleasePostCondition.ViolatedKey);
        settle.Diagnosis.LogLevel.Should().Be(LogLevel.Error);
        settle.UnreleasedBuildPath.Should().Be(attempted);
        log.Lines.Should().Contain(l => l.Level == LogLevel.Error && l.Message.Contains("could not be re-cut"));
    }
}
