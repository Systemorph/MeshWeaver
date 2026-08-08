using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.NuGet;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Frameworks;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Issue #576, remaining scope 1 — the IN-ACTIVATION compile wedge.
///
/// <para>The concurrent-trigger race is already closed on main: <c>CompilationStatus</c> IS the
/// single-flight lock (the watcher fires only on a transition INTO Pending; the Pending→Compiling
/// flip inside the owner's serialized Update elects exactly one dispatcher). That fix has a
/// corollary this test pins: because a fresh trigger against a <c>Compiling</c> type is now
/// correctly ABSORBED, a compile leg that never completes is no longer merely slow — it strands
/// the NodeType at Compiling for the life of the activation, with nothing able to recover it.</para>
///
/// <para>Two legs still had no wall clock around them, and both are exercised here through the
/// code's OWN injection seams — no reflection, no test hooks in production code:</para>
/// <list type="bullet">
///   <item><b>roslyn-compile</b> (<c>INuGetAssemblyResolver</c>): a <c>#r "nuget:…"</c> against an
///     unreachable feed parks inside <c>CompileAsync</c>. Bounded ⇒ TERMINAL Error naming the leg,
///     and the bound CANCELS the leaf so the abandoned restore stops and the single-flight entry
///     evicts — which is what makes the retry in step 4 a real compile rather than a replay of the
///     same hung task.</item>
///   <item><b>assembly-store-upload</b> (<c>IAssemblyStore</c>): a wedged blob endpoint parks AFTER
///     a perfectly good emit. Bounded ⇒ the compile still settles <b>Ok</b> — an upload failure has
///     never failed a compile and the bound must not change that contract — with a warning naming
///     the leg on the compile's ActivityLog, so a silently un-published assembly is diagnosable.</item>
/// </list>
///
/// <para>Sibling of <see cref="CompileSourceSnapshotWedgeTest"/>, which pins the same
/// "every dispatched compile reaches exactly one terminal status" contract for the source-snapshot
/// leg.</para>
/// </summary>
public class CompileLegBoundWedgeTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly string _cacheDir = Path.Combine(
        Path.GetTempPath(), $"MeshWeaverLegBoundTest-{Guid.NewGuid():N}");

    /// <summary>Short enough that the terminal write lands well inside the test budget, far above
    /// anything a healthy leg takes (both are milliseconds here).</summary>
    private static readonly TimeSpan LegBound = TimeSpan.FromSeconds(8);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_cacheDir);
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                // Both doubles are MESH-scoped singletons (no static state) and each hangs only
                // for its OWN target — the resolver for one package id, the store for one node
                // path — so the two tests never interfere even on a shared mesh.
                .AddSingleton<HangingNuGetAssemblyResolver>()
                .AddSingleton<INuGetAssemblyResolver>(sp =>
                    sp.GetRequiredService<HangingNuGetAssemblyResolver>())
                .AddSingleton<HangingAssemblyStore>()
                .AddSingleton<IAssemblyStore>(sp => sp.GetRequiredService<HangingAssemblyStore>())
                .Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = _cacheDir;
                    o.EnableCompilationCache = true;
                    o.EnableDiskCache = true;
                    o.RoslynCompileTimeout = LegBound;
                    o.AssemblyStoreUploadTimeout = LegBound;
                }));
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_cacheDir))
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private const string RoslynLegType = "RoslynLegWedgeType";
    private const string UploadLegType = "UploadLegWedgeType";

    private static string SourceFor(string className, string marker) => $$"""
        using MeshWeaver.Layout.Composition;
        public static class {{className}}
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>{{marker}}</div>");
        }
        """;

    private static string ConfigurationFor(string className) =>
        "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", "
        + className + ".Overview))";

    [Fact(Timeout = 150000)]
    public async Task HungRoslynLeg_SettlesTerminalError_NamingTheLeg_ThenRecovers()
    {
        var resolver = Mesh.ServiceProvider.GetRequiredService<HangingNuGetAssemblyResolver>();
        var typePath = $"{TestPartition}/{RoslynLegType}";

        // 1. A NodeType whose source declares a package from a feed that never answers. The
        //    first-build kickoff dispatches the compile; the compile parks inside CompileAsync's
        //    NuGet restore — the realistic non-completion (an unreachable feed has no timeout of
        //    its own), reached through the production DI seam.
        await NodeFactory.CreateNode(new MeshNode(RoslynLegType, TestPartition)
        {
            Name = "Roslyn Leg Wedge Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Repro for the unbounded Roslyn/NuGet compile leg (#576).",
                Configuration = ConfigurationFor("RoslynLegLayoutAreas"),
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $"#r \"nuget:{HangingNuGetAssemblyResolver.NeverAnswersPackageId}, 1.0.0\"\n"
                    + SourceFor("RoslynLegLayoutAreas", "ROSLYN_LEG_OK"),
                Language = "csharp"
            }
        }).Should().Within(30.Seconds()).Emit();

        // 2. 🚨 THE WEDGE ASSERTION. Unbounded, the type sits at Compiling for the life of the
        //    activation and every later trigger is absorbed by the (correct) status lock — this
        //    wait times out. Bounded, the compile fails TERMINALLY and the error NAMES the leg,
        //    so the operator learns which stage hung rather than "compile is taking a while".
        var failed = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(60.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error);
        var failedDef = (NodeTypeDefinition)failed.Content!;
        Output.WriteLine($"=== Terminal error landed: {failedDef.CompilationError} ===");
        failedDef.CompilationError.Should().Contain("roslyn-compile",
            "the terminal error must name the LEG that hung — that is the whole diagnostic value "
            + "of the bound");
        failedDef.CompilationError.Should().Contain("did not complete within");
        failedDef.LatestReleasePath.Should().BeNullOrEmpty(
            "a compile that never produced an assembly must not mint a release");
        resolver.HangAttempts.Should().BeGreaterThan(0,
            "the compile must genuinely have entered the NuGet leg — otherwise this test proves nothing");

        // 3. 🚨 The bound CANCELS the leg (CancellationDisposable), so the parked restore actually
        //    stops. That matters beyond tidiness: the per-node single-flight entry in
        //    MeshNodeCompilationService evicts only when its task settles, so without cancellation
        //    every retry would receive the SAME hung task and re-fail identically forever.
        await resolver.Cancelled.Should().Within(30.Seconds()).Emit();
        Output.WriteLine("=== hung NuGet leg was cancelled by the bound ===");

        // 4. The feed heals; a fresh explicit trigger (the Compile button / MCP compile seam) must
        //    now run a REAL compile that settles Ok.
        resolver.Heal();
        await Mesh.GetWorkspace().GetMeshNodeStream(typePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with
            {
                Content = def with
                {
                    RequestedReleaseAt = DateTimeOffset.UtcNow,
                    RequestedReleaseForce = true,
                }
            };
        }).Should().Within(30.Seconds()).Emit();

        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(60.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath));
        Output.WriteLine("=== recovery compile settled Ok ===");
    }

    [Fact(Timeout = 150000)]
    public async Task HungAssemblyStoreUpload_StillSettlesOk_AndNamesTheLegOnTheActivityLog()
    {
        var store = Mesh.ServiceProvider.GetRequiredService<HangingAssemblyStore>();
        var typePath = $"{TestPartition}/{UploadLegType}";
        store.HangFor(typePath);

        // 1. A perfectly healthy NodeType — Roslyn succeeds, and the compile then parks in the
        //    LAST leg of the pipeline: pushing the bytes to the assembly store.
        await NodeFactory.CreateNode(new MeshNode(UploadLegType, TestPartition)
        {
            Name = "Upload Leg Wedge Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Repro for the unbounded assembly-store upload leg (#576).",
                Configuration = ConfigurationFor("UploadLegLayoutAreas"),
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = SourceFor("UploadLegLayoutAreas", "UPLOAD_LEG_OK"),
                Language = "csharp"
            }
        }).Should().Within(30.Seconds()).Emit();

        // 2. 🚨 Unbounded, the type sits at Compiling forever with the assembly already on disk.
        //    Bounded, it SETTLES — and settles Ok, because an upload failure has never failed a
        //    compile (the assembly is usable in the producing silo). The bound closes the wedge
        //    without silently promoting a store outage into a compile outage.
        var settled = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);
        var settledDef = (NodeTypeDefinition)settled.Content!;
        Output.WriteLine($"=== settled {settledDef.CompilationStatus}: {settledDef.CompilationError} ===");
        settledDef.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "an assembly-store upload failure must NOT fail the compile — the bound only makes it "
            + "answer, it does not change the leg's contract");
        store.UploadAttempts.Should().BeGreaterThan(0,
            "the compile must genuinely have entered the upload leg");

        // 3. …and it must not be SILENT: a compile whose assembly never reached the store leaves
        //    cross-silo activation unable to find it, so the leg is named on the ActivityLog — the
        //    official compile-diagnosis surface the terminal write copies into.
        var activityPath = settledDef.LastCompilationActivityPath;
        activityPath.Should().NotBeNullOrEmpty("the compile activity is the diagnosis surface");
        var activity = await Mesh.GetWorkspace().GetMeshNodeStream(activityPath!)
            .Should().Within(30.Seconds())
            .Match(n => n.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions) is { } log
                && log.Messages.Any(m => m.Message.Contains("assembly-store-upload", StringComparison.Ordinal)));
        var warning = activity.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions)!.Messages
            .First(m => m.Message.Contains("assembly-store-upload", StringComparison.Ordinal));
        Output.WriteLine($"=== activity log names the leg: {warning.Message} ===");
        warning.Message.Should().Contain("did not complete within");
    }
}

/// <summary>
/// Test-only <see cref="INuGetAssemblyResolver"/> that NEVER answers for one well-known package id
/// — the deterministic stand-in for an unreachable/hanging NuGet feed, which is the realistic way
/// the compile's Roslyn leg fails to complete (the restore has no timeout of its own). Every other
/// package resolves to the empty set immediately, so the double is inert for any other NodeType
/// sharing the mesh. <see cref="Heal"/> models the feed coming back.
/// </summary>
public sealed class HangingNuGetAssemblyResolver : INuGetAssemblyResolver
{
    /// <summary>The package id this resolver refuses to answer for.</summary>
    public const string NeverAnswersPackageId = "MeshWeaver.Test.NeverAnswers";

    private readonly AsyncSubject<Unit> _cancelled = new();
    private int _hangAttempts;
    private volatile bool _healed;

    /// <summary>How many times a resolve entered the hanging path.</summary>
    public int HangAttempts => Volatile.Read(ref _hangAttempts);

    /// <summary>Completes once a hung resolve observed its cancellation — the proof that the
    /// bound does not merely abandon the leaf but actually stops it (which is what evicts the
    /// compile's single-flight entry and makes a retry a real compile).</summary>
    public IObservable<Unit> Cancelled => _cancelled;

    /// <summary>Stops hanging — the feed is reachable again.</summary>
    public void Heal() => _healed = true;

    /// <inheritdoc />
    public async Task<ResolvedPackageSet> ResolveAsync(
        IReadOnlyCollection<NuGetPackageReference> requested,
        NuGetFramework? targetFramework = null,
        CancellationToken ct = default)
    {
        var targeted = requested.Any(r =>
            string.Equals(r.Id, NeverAnswersPackageId, StringComparison.OrdinalIgnoreCase));
        if (_healed || !targeted)
            return ResolvedPackageSet.Empty;

        Interlocked.Increment(ref _hangAttempts);
        try
        {
            // Park until cancelled — exactly what a request to an unreachable feed does.
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            _cancelled.OnNext(Unit.Default);
            _cancelled.OnCompleted();
            throw;
        }
        return ResolvedPackageSet.Empty;
    }
}

/// <summary>
/// Test-only <see cref="IAssemblyStore"/> whose upload NEVER completes for one armed node path —
/// the stand-in for a wedged blob endpoint. Behaves like <c>NullAssemblyStore</c> for every other
/// path so it is inert for the rest of the mesh.
/// </summary>
public sealed class HangingAssemblyStore : IAssemblyStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _hangPaths =
        new(StringComparer.Ordinal);
    private int _uploadAttempts;

    /// <summary>How many uploads entered the hanging path.</summary>
    public int UploadAttempts => Volatile.Read(ref _uploadAttempts);

    /// <summary>Arms the store to never answer for <paramref name="nodeTypePath"/>.</summary>
    public void HangFor(string nodeTypePath) => _hangPaths[nodeTypePath] = 0;

    /// <inheritdoc />
    public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version)
        => Observable.Return<string?>(null);

    /// <inheritdoc />
    public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
        => PutWithLocation(nodeTypePath, version, assemblyBytes, pdbBytes).Select(l => l.LocalPath);

    /// <inheritdoc />
    public IObservable<AssemblyStoreLocation> PutWithLocation(
        string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
    {
        if (!_hangPaths.ContainsKey(nodeTypePath))
            return Observable.Return(new AssemblyStoreLocation(string.Empty, string.Empty, string.Empty));
        Interlocked.Increment(ref _uploadAttempts);
        // Never emits, never completes, never errors — the wedged endpoint.
        return Observable.Never<AssemblyStoreLocation>();
    }
}
