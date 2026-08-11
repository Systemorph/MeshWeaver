using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Deterministic repro of the memex-cloud 2026-07-20 <c>Store/Plugin</c> compile wedge.
///
/// <para><b>Incident shape:</b> a GitSync burst updated many NodeType sources at once. A compile
/// was dispatched (Pending → Compiling, activity created: "Compile started" / "Invoking
/// compiler…"), but its ONE-SHOT source-snapshot read — <c>ResolveSources(...).Take(1)</c> over
/// the shared <c>NodeSources.GetSources</c> synced query — never received the query's Initial
/// (a subscription that raced the burst). With no bound and no error path, the compile parked
/// FOREVER at <c>CompilationStatus=Compiling</c>. The wedge is ABSORBING: the compile watcher
/// needs a Pending TRANSITION, <c>InstallReleaseRequestWatcher</c> gates on a SETTLED status,
/// and the recovery kickoff is activation-one-shot — so no later trigger could ever start a
/// compile again ("no compile ever executed after the flip"; the explicit trigger no-oped).</para>
///
/// <para><b>This test</b> pins the wedge with a query provider that withholds the Initial for
/// exactly the NodeType's source query (the deterministic stand-in for the raced/lost
/// synced-query Initial — <c>MeshQuery.MergeProviderObservables</c> gates its merged Initial on
/// EVERY provider by design). Before the fix the NodeType wedges at Compiling forever and the
/// first assertion times out. After the fix (<c>MeshNodeCompilationService.SnapshotSources</c>
/// bounds the one-shot read; <c>RunCompile</c> guarantees a terminal outcome), the compile FAILS
/// TERMINALLY with a loud error naming the dead source query — and a fresh explicit trigger,
/// once the query is healthy again, runs a REAL compile that settles Ok. Wedges-to-zero: every
/// dispatched compile reaches exactly one terminal status.</para>
///
/// <para>🚨 The terminal status is <see cref="CompilationStatus.Unavailable"/>, not
/// <see cref="CompilationStatus.Error"/> (issue #1218). A source set that could not be
/// ESTABLISHED is not a verdict about the code: Roslyn never ran, so calling it an Error told
/// every reader — the instance overlay, and the bake readiness gate — that the author's code was
/// broken. The wedge contract this test exists for is unchanged and is what still matters: the
/// compile SETTLES, terminally, naming the dead query, and a fresh trigger recovers.</para>
/// </summary>
public class CompileSourceSnapshotWedgeTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output), IDisposable
{
    private readonly string _cacheDir = Path.Combine(
        Path.GetTempPath(), $"MeshWeaverSnapshotWedgeTest-{Guid.NewGuid():N}");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_cacheDir);
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                // The stalling provider is a MESH-scoped singleton (no static state); the test
                // resolves the same instance from the mesh to release the stall.
                .AddSingleton<StallingSourceQueryProvider>()
                .AddSingleton<IMeshQueryProvider>(sp =>
                    sp.GetRequiredService<StallingSourceQueryProvider>())
                .Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = _cacheDir;
                    o.EnableCompilationCache = true;
                    o.EnableDiskCache = true;
                    // Short snapshot bound so the terminal Error lands quickly; the healthy-path
                    // snapshot is instant (Replay(1) cache), so this never trips a good compile.
                    o.SourceSnapshotTimeout = TimeSpan.FromSeconds(5);
                }));
    }

    public new void Dispose()
    {
        base.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_cacheDir))
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private const string TypeName = "SnapshotWedgeType";
    private static readonly string TypePath = $"{TestPartition}/{TypeName}";

    private const string SourceCode = """
        using MeshWeaver.Layout.Composition;
        public static class WedgeLayoutAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>WEDGE_OK</div>");
        }
        """;

    [Fact(Timeout = 120000)]
    public async Task HungSourceSnapshot_SettlesTerminalError_ThenFreshTriggerRecovers()
    {
        var provider = Mesh.ServiceProvider.GetRequiredService<StallingSourceQueryProvider>();

        // 1. Create the NodeType + its source. The per-NodeType hub activates, the first-build
        //    kickoff flips CompilationStatus null → Pending, the compile watcher dispatches, the
        //    Pending → Compiling transition lands — and the compile's source snapshot parks on
        //    the withheld synced-query Initial (the raced-burst stand-in).
        await NodeFactory.CreateNode(new MeshNode(TypeName, TestPartition)
        {
            Name = "Snapshot Wedge Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Repro for the 2026-07-20 hung-source-snapshot compile wedge.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", WedgeLayoutAreas.Overview))",
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("code", $"{TypePath}/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = SourceCode, Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();

        // 2. 🚨 THE WEDGE ASSERTION. The dispatched compile's snapshot never receives an
        //    Initial. BEFORE the fix: the NodeType sticks at Compiling forever — no terminal
        //    write, no error, and no later trigger can recover it (the absorbing wedge) — this
        //    wait times out and the test is RED. AFTER the fix: the bounded snapshot settles
        //    UNESTABLISHED and the compile terminates at Unavailable, naming the dead source
        //    query — a terminal state that is honest about WHAT failed (#1218).
        var failed = await Mesh.GetWorkspace().GetMeshNodeStream(TypePath)
            .Should().Within(TimeSpan.FromSeconds(40))
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Error or CompilationStatus.Unavailable);
        var failedDef = (NodeTypeDefinition)failed.Content!;
        Output.WriteLine($"=== Terminal state landed: {failedDef.CompilationStatus} / {failedDef.CompilationError} ===");
        failedDef.CompilationStatus.Should().Be(CompilationStatus.Unavailable,
            "Roslyn never ran — the source set could not be established — so this is an "
            + "AVAILABILITY failure, never a verdict on the author's code (#1218). Recording it "
            + "as Error is what made every instance page say 'please correct the code' and made "
            + "the bake gate read an unreadable mesh as an image regression");
        failedDef.CompilationError.Should().Contain("Source discovery",
            "the terminal error must name the true defect — the source query that never emitted — "
            + "not a generic compile failure");
        failedDef.LatestReleasePath.Should().BeNullOrEmpty(
            "a compile that never saw its sources must not mint a release");

        // 3. The source query heals (the burst subsides): release the withheld Initial on the
        //    LIVE upstream subscription the cached synced query already holds.
        provider.Release();

        // 4. A fresh EXPLICIT trigger (the Compile button / MCP compile seam —
        //    RequestedReleaseAt) must now run a REAL compile that settles Ok. This is exactly
        //    what the absorbing wedge made impossible: with the status stuck at Compiling the
        //    release watcher's settled-gate swallowed every trigger. Terminal Error is settled,
        //    so the trigger dispatches and Roslyn runs against the now-emitting source set.
        await Mesh.GetWorkspace().GetMeshNodeStream(TypePath).Update(curr =>
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

        await Mesh.GetWorkspace().GetMeshNodeStream(TypePath)
            .Should().Within(TimeSpan.FromSeconds(60))
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath));
        Output.WriteLine("=== Recovery compile settled Ok ===");
    }
}

/// <summary>
/// Test-only <see cref="IMeshQueryProvider"/> that WITHHOLDS the Initial for any query that
/// targets the wedge NodeType's source namespace — the deterministic stand-in for a synced-query
/// subscription whose Initial was lost racing a source-update burst.
/// <c>MeshQuery.MergeProviderObservables</c> gates its merged Initial on EVERY registered
/// provider (by design), so one withheld Initial keeps the whole
/// <c>NodeSources.GetSources</c> synced query from ever emitting. Every other query gets an
/// immediate empty Initial (well-behaved: exactly one Initial, never completes).
/// <see cref="Release"/> flushes the withheld Initials on the live subscriptions, modelling the
/// upstream healing after the burst.
/// </summary>
public sealed class StallingSourceQueryProvider : IMeshQueryProvider
{
    private readonly object _gate = new();
    private ImmutableList<Action> _pendingInitials = ImmutableList<Action>.Empty;
    private bool _released;

    private const string StallMarker = "SnapshotWedgeType/";

    /// <inheritdoc />
    public string Name => nameof(StallingSourceQueryProvider);

    /// <inheritdoc />
    public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

    /// <summary>Flushes every withheld Initial and stops stalling new subscriptions.</summary>
    public void Release()
    {
        ImmutableList<Action> toFlush;
        lock (_gate)
        {
            _released = true;
            toFlush = _pendingInitials;
            _pendingInitials = ImmutableList<Action>.Empty;
        }
        foreach (var emit in toFlush)
            emit();
    }

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        => Observable.Create<QueryResultChange<T>>(observer =>
        {
            void EmitInitial() => observer.OnNext(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Items = Array.Empty<T>(),
                Timestamp = DateTimeOffset.UtcNow,
            });

            var stall = request.EffectiveQueries
                .Any(q => q?.Contains(StallMarker, StringComparison.Ordinal) == true);
            if (stall)
            {
                lock (_gate)
                {
                    if (!_released)
                    {
                        // Withhold the Initial: the observer stays subscribed (never completes,
                        // never errors) until Release() flushes it — the lost-Initial shape.
                        _pendingInitials = _pendingInitials.Add(EmitInitial);
                        return Disposable.Empty;
                    }
                }
            }
            EmitInitial();
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
