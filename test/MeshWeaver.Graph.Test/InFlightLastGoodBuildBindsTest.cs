using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 TOLERANCE: a page of an instance whose type has a LAST-GOOD compiled assembly must render on
/// that assembly while a recompile is in flight (maintainer directive, 2026-09-12: <i>"it should all
/// be tolerant"</i>). Waiting for the build to "settle" — and painting the compile-progress overlay
/// after the grace, then the 30 s "did not settle" fallback — is the defect whenever the record
/// still names bytes this process can load.
///
/// <para><b>Measured.</b> memex 2026-09-12, during a rolling update: two platform generations served
/// one mesh for the 30-minute termination grace, each compiling <c>Store/Plugin</c> under its own
/// framework identity (ten compiles in four minutes), and every instance of the type on the new
/// generation — DeepSign, Edu, RolePlay, Chess, ClaudeCode, Voice, WebSearch, … — sat on
/// <c>NodeType 'Store/Plugin' build did not settle within 30s</c> while the assembly store held a
/// build for the running identity the whole time.</para>
///
/// <para>Asserted on the production predicates the slow path composes
/// (<see cref="NodeTypeEnrichmentHelpers.IsBindableOrSettled"/>,
/// <see cref="NodeTypeEnrichmentHelpers.RoutesToInFlightOverlay"/>) and on
/// <see cref="NodeTypeEnrichmentHelpers.WaitForCompileSettled"/> under virtual time — the same
/// seams <c>CompileSettlePredicateTest</c> and <c>CompileWaitDoesNotTimeoutTest</c> pin — so the
/// verdict is about the decision, not about a compiler's timing.</para>
/// </summary>
public class InFlightLastGoodBuildBindsTest
{
    private const string NodeTypePath = "type/LastGoodProbe";
    private const string ForeignFramework = "s0000000000000000000000000000000f";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Default);
    // Virtual time (TestScheduler): the no-progress budget the wait is armed with. Any value works;
    // it is deliberately NOT the copied 30 s convention the timeout-literal ratchet counts.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>A dynamic type mid-rebuild (<paramref name="status"/>) that still names the build
    /// it published before — for THIS framework identity unless <paramref name="framework"/> says
    /// otherwise.</summary>
    private static MeshNode Node(CompilationStatus? status, bool lastGood, string? framework = null) =>
        new(NodeTypePath)
        {
            Version = 7,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = status,
                LatestAssemblyCollection = lastGood ? "local" : null,
                LatestAssemblyPath = lastGood ? "Type_LastGood/v7-live.dll" : null,
                CompiledFrameworkVersion = lastGood
                    ? framework ?? NodeTypeCompilationHelpers.FrameworkVersion
                    : null,
            },
        };

    // ── the predicates ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    public void AnInFlightTypeWithALastGoodBuild_IsBindable(CompilationStatus inFlight)
        => Assert.True(NodeTypeEnrichmentHelpers.IsBindableWhileCompiling(
            Node(inFlight, lastGood: true), Options, guards: null));

    [Theory]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    public void AnInFlightTypeWithNoBuild_IsNotBindable(CompilationStatus inFlight)
        => Assert.False(NodeTypeEnrichmentHelpers.IsBindableWhileCompiling(
            Node(inFlight, lastGood: false), Options, guards: null));

    [Fact]
    public void AnInFlightTypeWhoseBuildIsForAnotherFramework_IsNotBindable()
        => Assert.False(NodeTypeEnrichmentHelpers.IsBindableWhileCompiling(
                Node(CompilationStatus.Pending, lastGood: true, ForeignFramework), Options, guards: null),
            "bytes compiled for another framework identity are not loadable here — the progress "
            + "overlay stays the honest answer for those");

    [Fact]
    public void ASettledType_IsNotBindableWhileCompiling_ItIsSimplySettled()
    {
        var ok = Node(CompilationStatus.Ok, lastGood: true);
        Assert.False(NodeTypeEnrichmentHelpers.IsBindableWhileCompiling(ok, Options, guards: null));
        Assert.True(NodeTypeEnrichmentHelpers.IsBindableOrSettled(ok, Options, guards: null));
    }

    [Fact]
    public void RoutingToTheProgressOverlay_IsOnlyForAnInFlightTypeWithNothingToBind()
    {
        Assert.False(NodeTypeEnrichmentHelpers.RoutesToInFlightOverlay(
                Node(CompilationStatus.Pending, lastGood: true), Options, guards: null),
            "a loadable last-good build is bound, never overlaid");
        Assert.True(NodeTypeEnrichmentHelpers.RoutesToInFlightOverlay(
            Node(CompilationStatus.Compiling, lastGood: false), Options, guards: null));
        Assert.False(NodeTypeEnrichmentHelpers.RoutesToInFlightOverlay(
            Node(CompilationStatus.Ok, lastGood: true), Options, guards: null));
    }

    // ── the wait, under virtual time ──────────────────────────────────────────────────────────

    private static IObservable<MeshNode> Settled(IObservable<MeshNode> typeStream)
        => typeStream
            .Where(n => NodeTypeEnrichmentHelpers.IsBindableOrSettled(n, Options, guards: null))
            .Take(1);

    /// <summary>
    /// THE CONTRACT: compile in flight + last-good assembly present ⇒ the activation binds NOW —
    /// at virtual t = 0, before any grace elapses and without waiting for the terminal write.
    /// </summary>
    [Fact]
    public void CompileInFlightWithALastGoodBuild_EmitsImmediately_NotAfterTheGrace()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();
        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler, Options,
                inFlightGrace: Grace)
            .Subscribe(n => emitted = n, ex => error = ex);

        var rebuilding = Node(CompilationStatus.Compiling, lastGood: true);
        typeStream.OnNext(rebuilding);

        Assert.Null(error);
        Assert.Same(rebuilding, emitted);
        Assert.False(NodeTypeEnrichmentHelpers.RoutesToInFlightOverlay(emitted!, Options, guards: null),
            "the slow path binds this emission through ApplyStreamResult's HasUsableBuild branch "
            + "(which precedes every status branch) and arms the stale-assembly watcher for the "
            + "rebuild — it does not route it to the compile-progress overlay");
    }

    /// <summary>
    /// The control arm: an in-flight type with NOTHING loadable keeps today's contract exactly —
    /// silence at t = 0, the grace emission after <see cref="Grace"/>, and that emission routes to
    /// the overlay.
    /// </summary>
    [Fact]
    public void CompileInFlightWithNoBuild_StillWaitsOutTheGrace_ThenOverlays()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();
        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler, Options,
                inFlightGrace: Grace)
            .Subscribe(n => emitted = n, ex => error = ex);

        var firstBuild = Node(CompilationStatus.Compiling, lastGood: false);
        typeStream.OnNext(firstBuild);
        Assert.Null(emitted);

        scheduler.AdvanceBy(Grace.Ticks);
        Assert.Null(error);
        Assert.Same(firstBuild, emitted);
        Assert.True(NodeTypeEnrichmentHelpers.RoutesToInFlightOverlay(emitted!, Options, guards: null));
    }
}

/// <summary>
/// 🚨 THE WEDGE ON THE ACTIVATION PATH, reproduced against a real mesh: the NodeType node an
/// activation reads off the mesh-hub MIRROR says the build is for ANOTHER framework identity, while
/// STORAGE holds a build for the live one. Before this fix the framework-stale branch trusted the
/// mirror, flipped the type Pending and waited for a "usable" build; with the writer being another
/// process generation that wait could not be satisfied and every activation timed out onto the
/// "did not settle" overlay (memex 2026-09-12, every <c>Store/Plugin</c> instance). Now the branch
/// confirms against storage first and binds the build that is actually there — no Pending flip, no
/// recompile, no wait.
///
/// <para>Discriminating by construction: the pre-fix path WRITES (the Ok→Pending flip advances the
/// persisted version and dispatches a compile); the fixed path reads once and binds. The persisted
/// record's version and status are therefore the assertion, not the shape of the configuration.</para>
/// </summary>
public class ForeignFrameworkStampOnTheMirrorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypeName = "ForeignStampProbe";
    private const string ForeignFramework = "s0000000000000000000000000000000f";
    private static readonly TimeSpan VerdictBudget = TimeSpan.FromSeconds(20);

    private static MeshConfiguration EmptyMeshConfiguration() => new(Array.Empty<MeshNode>());

    private IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    [Fact(Timeout = 90_000)]
    public async Task AForeignStampOnTheMirror_BindsTheStorageBuild_WithoutARecompile()
    {
        var typePath = $"{TestPartition}/{TypeName}";
        var store = Mesh.ServiceProvider.GetService<IAssemblyStore>();
        Assert.NotNull(store);

        // Real bytes under the live framework tag — the build THIS process can load.
        const long storeVersion = 1;
        var bytes = await File.ReadAllBytesAsync(typeof(ModuleVersionCompatibility).Assembly.Location);
        var location = await store!.PutWithLocation(typePath, storeVersion, bytes, null)
            .FirstAsync().Timeout(VerdictBudget).Await();

        var usable = new NodeTypeDefinition
        {
            Configuration = "config => config",
            CompilationStatus = CompilationStatus.Ok,
            LastCompiledVersion = storeVersion,
            LatestAssemblyCollection = location.Collection,
            LatestAssemblyPath = location.ContentPath,
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        };
        var persisted = await CreateAsSystem(new MeshNode(TypeName, TestPartition)
        {
            NodeType = MeshNode.NodeTypePath,
            Content = usable,
        });

        // What the mirror replays: the same record, stamped by another generation's compile.
        var mirror = persisted with
        {
            Content = usable with { CompiledFrameworkVersion = ForeignFramework },
        };
        var instance = new MeshNode("foreign-stamp-instance", TestPartition) { NodeType = typePath };

        var verdict = await NodeTypeEnrichmentHelpers
            .ApplyStreamResult(
                mirror, instance, typePath, EmptyMeshConfiguration(), Compiler, Mesh, logger: null)
            .Take(1)
            .Should().Within(VerdictBudget).Emit("the activation must reach a verdict");

        // Not an overlay: every overlay installs an UnhandledMessageNack; a bound build does not.
        if (verdict.HubConfiguration is not null)
        {
            var applied = verdict.HubConfiguration(
                new MessageHubConfiguration(null, new Address("probe", TypeName)));
            (applied.Get<UnhandledMessageNack>()).Should().BeNull(
                "storage holds a build for the live framework, so the instance binds it — it is "
                + "neither the compile-progress overlay nor the framework-stale prompt");
        }

        // No recompile was requested: the persisted record is exactly as written.
        var after = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n is not null)
            .Take(1)
            .Should().Within(VerdictBudget).Emit("the type node is readable after the verdict");
        var afterDef = after!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions);
        afterDef.Should().NotBeNull();
        afterDef!.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "the framework-stale branch must not flip a type Pending when storage already holds a "
            + "loadable build — that flip is the wait every instance sat out on memex 2026-09-12");
        after.Version.Should().Be(persisted.Version,
            "a bind is a READ; the pre-fix path's Ok→Pending flip advanced the version");
    }

    private Task<MeshNode> CreateAsSystem(MeshNode node)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => meshService.CreateNode(node))
            .FirstAsync().Timeout(VerdictBudget).Await();
    }
}
