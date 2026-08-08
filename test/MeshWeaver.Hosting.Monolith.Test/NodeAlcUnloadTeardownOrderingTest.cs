using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Ordered record of the teardown milestones this test pins. Instance state on a mesh-scoped
/// singleton — never static (NoStaticState): it dies with the mesh, so nothing bleeds into the
/// next test class and no <c>Clear()</c> is needed.
/// </summary>
internal sealed class TeardownOrderLog
{
    private readonly object gate = new();
    private ImmutableList<string> marks = ImmutableList<string>.Empty;

    public void Mark(string mark)
    {
        lock (gate)
            marks = marks.Add(mark);
    }

    public ImmutableList<string> Marks
    {
        get
        {
            lock (gate)
                return marks;
        }
    }
}

/// <summary>
/// Records WHEN <see cref="ICompilationCacheService.UnloadNodeContexts"/> is invoked, then forwards
/// to the real implementation. Re-declaring <see cref="ICompilationCacheService"/> in the base list
/// re-maps interface dispatch onto this class, so the explicit implementation below wins for every
/// caller that goes through the interface (which is what <c>MeshDataSource.SubscribeToOwnDeletion</c>
/// does) — no 30-member forwarding decorator needed.
/// </summary>
internal sealed class OrderRecordingCompilationCacheService(
    IOptions<CompilationCacheOptions> options,
    ILogger<CompilationCacheService> logger,
    TeardownOrderLog orderLog)
    : CompilationCacheService(options, logger), ICompilationCacheService
{
    /// <summary>Marker prefix; the suffix is the sanitized node name being unloaded.</summary>
    public const string UnloadMarkPrefix = "unload:";

    void ICompilationCacheService.UnloadNodeContexts(string nodeName)
    {
        orderLog.Mark(UnloadMarkPrefix + nodeName);
        base.UnloadNodeContexts(nodeName);
    }
}

/// <summary>
/// 🚨 Teardown PHASE-ORDERING guard for collectible node <c>AssemblyLoadContext</c>s (issue #613).
///
/// <para>A node's ALC must not be unloaded while a pooled I/O leaf can still be executing that
/// ALC's compiled types. The join that guarantees nobody is — <see cref="IoPoolRegistry.DrainAll"/>
/// — runs AFTER <see cref="IMessageHub.DisposalCompleted"/> in every teardown orchestrator
/// (<c>MeshTeardownExtensions.WaitForDisposalAndIoDrainAsync</c>, <c>MonolithMeshTestBase</c>).
/// A hub-disposal callback runs in <c>MessageHub.DisposeImpl</c>, i.e. strictly BEFORE
/// <c>DisposalCompleted</c> — so unloading from there frees the LoaderAllocator out from under a
/// layout-render leaf still inside <c>IoPool.SubscribeThroughPool</c>. The observed crash was
/// <c>AccessViolationException</c> in <c>StaticsHelpers.GetGCThreadStaticBase</c> under
/// <c>Enumerable.ToArray</c> (Workspace's <c>.Cast&lt;T&gt;().ToArray()</c> over a NodeType-compiled
/// <c>T</c>) → SIGABRT, exit 134.</para>
///
/// <para>This test needs no crash: it records the invocation ORDER directly. The
/// <c>ICompilationCacheService</c> is decorated to stamp each <c>UnloadNodeContexts</c> call, a real
/// pooled leaf stamps from INSIDE <see cref="IoPoolRegistry.DrainAll"/> (its cancellation callback
/// runs on <c>IoPool.Drain</c>'s <c>_poolCts.Cancel()</c>), and the test drives the exact phase
/// sequence <c>MeshTeardownExtensions</c> does. Before the fix the unload lands first and this test
/// is RED; the contract it asserts is the one <see cref="MeshTeardownSignal"/>'s own XML doc states
/// — "everything that must not run before teardown truly ends — disposing the service scope,
/// unloading node ALCs … subscribes here".</para>
/// </summary>
public class NodeAlcUnloadTeardownOrderingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Stamped from inside <c>IoPool.Drain</c> — the pool token's cancellation callback.</summary>
    private const string DrainAllStartedMark = "drain-all:started";

    /// <summary>Stamped on the calling thread the instant <see cref="IoPoolRegistry.DrainAll"/> returns.</summary>
    private const string DrainAllReturnedMark = "drain-all:returned";

    /// <summary>
    /// Dedicated pool for the probe leaf so an infinitely-parked slot can never starve Layout / PG /
    /// Compile. Unknown names get <c>IoPoolOptions.Default</c> (= ProcessorCount) slots.
    /// </summary>
    private const string ProbePoolName = "teardown-order-probe";

    private const string ProbeNodeTypeId = "AlcUnloadOrderProbeStory";

    /// <summary>Separate id so the live-mesh case never shares a cache entry with the teardown case.</summary>
    private const string LiveProbeNodeTypeId = "AlcUnloadLiveProbeStory";

    private static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(30);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            // AFTER AddGraph: MeshBuilder.ConfigureServices applies its delegate immediately to the
            // live IServiceCollection, so this replaces the registration AddGraph just made.
            .ConfigureServices(services =>
            {
                services.AddSingleton<TeardownOrderLog>();
                services.RemoveAll<ICompilationCacheService>();
                services.AddSingleton<ICompilationCacheService>(sp =>
                    new OrderRecordingCompilationCacheService(
                        sp.GetRequiredService<IOptions<CompilationCacheOptions>>(),
                        sp.GetRequiredService<ILogger<CompilationCacheService>>(),
                        sp.GetRequiredService<TeardownOrderLog>()));
                return services;
            });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Compiles a real NodeType so at least one recorded unload frees an actual collectible
    /// <c>DynamicNode_*</c> LoaderAllocator — the thing whose premature release is the crash.
    /// Mirrors <c>NodeTypeAssemblyLeakTest</c>: create the NodeType + its Source node, then observe
    /// the terminal <c>CompilationStatus</c> the per-node hub writes back onto its own MeshNode via
    /// <c>stream.Update</c> (no verb-shaped compile request — that response can simply never arrive).
    /// </summary>
    private async Task CompileProbeNodeTypeAsync(string nodeTypeId)
    {
        var nodeTypePath = $"type/{nodeTypeId}";
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = $"config => config.WithContentType<{nodeTypeId}>()"
            },
            State = MeshNodeState.Active,
        };

        await MeshService.CreateNode(typeNode)
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
            {
                NodeType = "Code",
                Name = "code",
                Content = new CodeConfiguration
                {
                    Code = $"public record {nodeTypeId} {{ public string Id {{ get; init; }} = string.Empty; }}",
                    Language = "csharp",
                },
                State = MeshNodeState.Active,
            }))
            .Should().Within(30.Seconds()).Emit();

        var compiledNode = await Mesh.GetMeshNodeStream(nodeTypePath)
            .Should().Within(60.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition def
                        && def.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);

        var compiledDef = (NodeTypeDefinition)compiledNode.Content!;
        compiledDef.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the probe NodeType must compile; error: {compiledDef.CompilationError}");
    }

    /// <summary>
    /// A genuine pooled I/O leaf that parks until the pool token is cancelled. Registering on that
    /// token means the stamp is written by <c>IoPool.Drain</c>'s own <c>_poolCts.Cancel()</c> — the
    /// mark is produced INSIDE <see cref="IoPoolRegistry.DrainAll"/>, not by test bookkeeping. It
    /// also recreates the hazard's precondition: a leaf in flight across the
    /// <c>DisposalCompleted</c> → <c>DrainAll</c> window. It unwinds on cancellation, so the drain
    /// joins it and the teardown report stays clean.
    /// </summary>
    private static async Task<Unit> ParkUntilDrainedAsync(CancellationToken ct, TeardownOrderLog orderLog)
    {
        await using var registration = ct.Register(() => orderLog.Mark(DrainAllStartedMark));
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected: DrainAll cancelled us. Unwind promptly so the join is real.
        }

        return Unit.Default;
    }

    /// <summary>
    /// The OTHER half of the gate, and the one that must never regress into a leak: when only the
    /// NODE hub disposes and the mesh keeps running (prod steady state — node deleted, hub evicted or
    /// recycled), the ALC must be reclaimed IMMEDIATELY. Deferring it to
    /// <see cref="MeshTeardownSignal"/> there would hold the collectible context for the whole
    /// process lifetime, which is exactly the late-project CI OOM / GC-stall that put this unload
    /// hook in <c>MeshDataSource.SubscribeToOwnDeletion</c> in the first place. No
    /// <c>DrainAll</c> is pending in this case, so there is nothing to order behind.
    /// </summary>
    [Fact]
    public async Task NodeAssemblyContexts_AreUnloaded_Immediately_WhenTheMeshKeepsRunning()
    {
        var orderLog = Mesh.ServiceProvider.GetRequiredService<TeardownOrderLog>();

        await CompileProbeNodeTypeAsync(LiveProbeNodeTypeId);

        var nodeTypePath = $"type/{LiveProbeNodeTypeId}";
        var expectedMark = OrderRecordingCompilationCacheService.UnloadMarkPrefix
                           + Mesh.ServiceProvider.GetRequiredService<ICompilationCacheService>()
                               .SanitizeNodeName(nodeTypePath);

        orderLog.Marks.Should().NotContain(expectedMark,
            "the NodeType's ALC must still be loaded while its hub is alive");

        var nodeHub = Mesh.GetHostedHub(Mesh.GetAddress(nodeTypePath), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("compiling the NodeType activates its per-node hub");

        // Dispose ONLY the node hub. The mesh stays up, so the gate must take the immediate branch.
        nodeHub!.Dispose();

        await Observable.Interval(TimeSpan.FromMilliseconds(20))
            .StartWith(-1L)
            .Where(_ => orderLog.Marks.Contains(expectedMark))
            .FirstAsync()
            .Timeout(TeardownBudget)
            .ToTask();

        Mesh.IsDisposing.Should().BeFalse(
            "the reclaim must happen while the mesh is still running — if it only arrived because " +
            "the mesh started tearing down, the live-mesh branch is not being exercised and a " +
            "long-lived process would leak this ALC (the CI OOM this hook exists to prevent)");

        Output.WriteLine($"[diag] live-mesh marks: {string.Join(" | ", orderLog.Marks)}");
    }

    [Fact]
    public async Task NodeAssemblyContexts_AreUnloaded_OnlyAfterTheIoPoolsAreDrained()
    {
        var orderLog = Mesh.ServiceProvider.GetRequiredService<TeardownOrderLog>();
        var ioPools = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        var asyncDisposeQueue = Mesh.ServiceProvider.GetService<AsyncDisposeQueue>();
        var teardownSignal = Mesh.ServiceProvider.GetRequiredService<MeshTeardownSignal>();

        await CompileProbeNodeTypeAsync(ProbeNodeTypeId);

        // Park a real leaf on its own pool and wait until it actually holds a slot, so DrainAll has
        // something to cancel + join (and something to stamp with).
        var probePool = ioPools.Get(ProbePoolName);
        using var probe = probePool
            .Invoke(ct => ParkUntilDrainedAsync(ct, orderLog))
            .Subscribe(_ => { }, _ => { });

        await Observable.Interval(TimeSpan.FromMilliseconds(20))
            .StartWith(-1L)
            .Where(_ => probePool.CurrentInFlight > 0)
            .FirstAsync()
            .Timeout(10.Seconds())
            .ToTask();

        // The production teardown sequence, inlined from
        // MeshTeardownExtensions.WaitForDisposalAndIoDrainAsync so the DrainAll boundary can be
        // stamped between phases. Nothing is skipped and the report is the genuine one.
        Mesh.Dispose();
        await Mesh.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .Timeout(TeardownBudget)
            .ToTask();

        var leakedIoLeaves = ioPools.DrainAll();
        orderLog.Mark(DrainAllReturnedMark);

        var asyncDisposeClean = asyncDisposeQueue is null
                                || await asyncDisposeQueue.DrainAsync(TeardownBudget);

        teardownSignal.SignalCompleted(new TeardownReport(leakedIoLeaves, asyncDisposeClean));

        var marks = orderLog.Marks;
        Output.WriteLine($"[diag] teardown marks: {string.Join(" | ", marks)}");

        leakedIoLeaves.Should().Be(0,
            "the probe leaf observes its cancellation token, so DrainAll's join must be real");
        marks.Should().Contain(DrainAllStartedMark,
            "the parked probe leaf must have been cancelled from inside IoPoolRegistry.DrainAll");

        var drainAllReturnedAt = marks.IndexOf(DrainAllReturnedMark);
        var unloadMarks = marks
            .Select((mark, index) => (mark, index))
            .Where(m => m.mark.StartsWith(OrderRecordingCompilationCacheService.UnloadMarkPrefix,
                StringComparison.Ordinal))
            .ToImmutableArray();

        unloadMarks.Should().NotBeEmpty(
            "every per-node hub reclaims its collectible ALC on disposal, so teardown must call " +
            "UnloadNodeContexts at least once");

        var premature = unloadMarks.Where(m => m.index < drainAllReturnedAt).Select(m => m.mark).ToArray();
        premature.Should().BeEmpty(
            "a node's collectible AssemblyLoadContext may only be unloaded once IoPoolRegistry.DrainAll() " +
            "has cancelled and JOINED every pooled leaf. Unloading earlier — from the hub-disposal " +
            "callback, which MessageHub.DisposeImpl runs before DisposalCompleted — frees the " +
            "LoaderAllocator under a layout-render leaf still inside IoPool.SubscribeThroughPool, and " +
            "its next access to a NodeType-compiled type's statics is an AccessViolation (issue #613). " +
            $"Unloaded too early: {string.Join(", ", premature)}");
    }
}
