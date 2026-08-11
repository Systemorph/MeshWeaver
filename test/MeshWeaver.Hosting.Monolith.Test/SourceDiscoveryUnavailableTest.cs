using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// 🚨 Issue #1218 — <b>a compile whose SOURCE SET could not be established is not a verdict about
/// the code.</b>
///
/// <para><b>Incident.</b> memex-cloud, 2026-08-11 (ci.2947), an activation-path bake running during
/// a rollout. The OUTGOING pod still held the per-NodeType hub activations, so the incoming pod's
/// source discovery round-tripped cross-silo into a busy peer and starved. Sampled outcomes of the
/// in-flight bake: <c>TimedOut=16 CompileError=14 Compiled=26</c>, with a p90 inter-type gap of
/// exactly 60.0 s — the <c>SubscribeRequest</c> timeout. The 14 "CompileError"s looked like this:</para>
///
/// <code>
/// DynamicTypePreWarmer: BusinessRules/Scope → CompileError
///   CS0246: The type or namespace name 'ScopeLibrary' could not be found
///   CS0103: The name 'ScopeTestsArea' does not exist in the current context
/// </code>
///
/// <para><c>BusinessRules/Scope</c> and <c>SocialMedia/Post</c> had compiled in ~1 s earlier the
/// SAME DAY on the SAME content. Their source queries simply came back short, the compiler handed
/// the short set to Roslyn, and Roslyn — correctly, given what it was shown — emitted diagnostics
/// about the author's code. The bake readiness gate cannot tell that from a real image regression,
/// so <c>BakePhase.Regressed</c> stuck, readiness was refused, the rollout stalled on healthy
/// content, and the stalled pod kept re-baking — which was itself the cluster load behind the
/// 2026-08-10 wedge. The operator's only lever was DISARMING the gate, i.e. exactly the
/// "we can't tell a real regression from an infrastructure hiccup" situation the gate exists to
/// prevent.</para>
///
/// <para><b>What these tests pin — the three cases must stay distinguishable:</b>
/// <list type="number">
///   <item>Source discovery FAILED (query errored / never answered) ⇒ NOT
///     <see cref="PreWarmStatus.CompileError"/>, and it does NOT gate.</item>
///   <item>A REAL compile error on an established source set ⇒ still
///     <see cref="PreWarmStatus.CompileError"/>, and it still GATES.</item>
///   <item>Genuinely ZERO matching sources with a healthy query ⇒ still
///     <see cref="PreWarmStatus.NoSources"/> (#1204), and it does NOT gate.</item>
/// </list></para>
/// </summary>
public class SourceDiscoveryUnavailableTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output), IDisposable
{
    private readonly string _cacheDir = Path.Combine(
        Path.GetTempPath(), $"MeshWeaverSourceUnavailableTest-{Guid.NewGuid():N}");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_cacheDir);
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                // Mesh-scoped singleton (no static state) — it dies with the mesh.
                .AddSingleton<StarvedSourceQueryProvider>()
                .AddSingleton<IMeshQueryProvider>(sp =>
                    sp.GetRequiredService<StarvedSourceQueryProvider>())
                .Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = _cacheDir;
                    o.EnableCompilationCache = true;
                    o.EnableDiskCache = true;
                    // Short bound: the starved legs error immediately, so the snapshot settles
                    // long before this — it is here only so a genuinely silent leg cannot hold
                    // the test at the 30 s production default.
                    o.SourceSnapshotTimeout = TimeSpan.FromSeconds(5);
                }));
    }

    public new void Dispose()
    {
        base.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_cacheDir))
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private const string Partition = "SourceUnavailableTest";

    /// <summary>The type whose source discovery starves — the incident's shape.</summary>
    private const string StarvedName = "StarvedSourceType";
    private static readonly string StarvedPath = $"{Partition}/{StarvedName}";

    /// <summary>The control: a genuinely broken type whose source discovery is HEALTHY.</summary>
    private const string BrokenName = "GenuinelyBrokenType";
    private static readonly string BrokenPath = $"{Partition}/{BrokenName}";

    /// <summary>Valid source — the point is that this code is FINE; only the READ of it fails.</summary>
    private const string HealthySource = """
        using MeshWeaver.Layout.Composition;
        public static class StarvedLayoutAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>STARVED_OK</div>");
        }
        """;

    /// <summary>Source that Roslyn genuinely rejects — the control case that MUST keep gating.</summary>
    private const string BrokenSource = """
        public static class BrokenLayoutAreas
        {
            public static int Nope( => this is not valid C# at all ((
        }
        """;

    /// <summary>
    /// 🚨 THE PIN. Two NodeTypes, identical treatment, one difference: one type's source QUERIES
    /// die (the cross-silo starvation stand-in), the other type's CODE is broken.
    ///
    /// <para><b>Before the fix</b> both settle <see cref="CompilationStatus.Error"/> and both
    /// classify as <see cref="PreWarmStatus.CompileError"/>, so the gate refuses readiness for
    /// both — the starved type being the false regression that stalled the rollout. The starved
    /// type's compile also actually RAN, against a set it knew to be short, producing a phantom
    /// <c>CS0103: The name 'StarvedLayoutAreas' does not exist in the current context</c> — a
    /// diagnostic indistinguishable from a real one.</para>
    ///
    /// <para><b>After the fix</b> the starved type never reaches Roslyn: it settles
    /// <see cref="CompilationStatus.Unavailable"/> naming the query that died, which
    /// <c>DynamicTypePreWarmer.WarmOne</c> reports as <see cref="PreWarmStatus.TimedOut"/> — a
    /// non-verdict the gate files under "not evaluated". The genuinely broken type is untouched:
    /// <see cref="CompilationStatus.Error"/>, <see cref="PreWarmStatus.CompileError"/>, gates.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task StarvedSourceDiscovery_IsNotACompileVerdict_WhileARealCompileErrorStillIs()
    {
        // ── The starved type: valid Configuration, valid source, DEAD source query ───────────
        await NodeFactory.CreateNode(new MeshNode(StarvedName, Partition)
        {
            Name = "Starved Source Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Its sources are fine; the mesh read of them is not.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", StarvedLayoutAreas.Overview))",
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("code", $"{StarvedPath}/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = HealthySource, Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();

        // ── The control: healthy source query, genuinely broken code ─────────────────────────
        await NodeFactory.CreateNode(new MeshNode(BrokenName, Partition)
        {
            Name = "Genuinely Broken Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Roslyn really does reject this.",
                Configuration = "config => config.AddDefaultLayoutAreas()",
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("code", $"{BrokenPath}/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = BrokenSource, Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();

        // The starved type's SOURCE IS THERE AND IS FINE — read authoritatively, off the query
        // path. This is what makes the eventual verdict provably about the READ, not the code.
        var sourceNode = await Mesh.GetWorkspace()
            .GetMeshNodeStream($"{StarvedPath}/Source/code")
            .Should().Within(30.Seconds()).Match(n => n?.Content is CodeConfiguration);
        ((CodeConfiguration)sourceNode.Content!).Code.Should().Contain("StarvedLayoutAreas",
            "the content is healthy — only its discovery starves");

        // ── Terminal states ──────────────────────────────────────────────────────────────────
        var starved = await Mesh.GetWorkspace().GetMeshNodeStream(StarvedPath)
            .Should().Within(TimeSpan.FromSeconds(90))
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Error
                                       or CompilationStatus.Unavailable);
        var starvedDef = (NodeTypeDefinition)starved.Content!;
        Output.WriteLine($"=== STARVED  → {starvedDef.CompilationStatus}: {starvedDef.CompilationError}");

        var broken = await Mesh.GetWorkspace().GetMeshNodeStream(BrokenPath)
            .Should().Within(TimeSpan.FromSeconds(90))
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Error
                                       or CompilationStatus.Unavailable);
        var brokenDef = (NodeTypeDefinition)broken.Content!;
        Output.WriteLine($"=== BROKEN   → {brokenDef.CompilationStatus}: {brokenDef.CompilationError}");

        // 1️⃣ An unreadable source set is an AVAILABILITY failure, never a compile verdict.
        starvedDef.CompilationStatus.Should().Be(CompilationStatus.Unavailable,
            "Roslyn never ran — the source set could not be ESTABLISHED. Recording that as Error "
            + "is what made the bake gate read a starved cross-silo read as an image regression "
            + "and stall the rollout on 14 of 56 healthy types (#1218)");
        starvedDef.CompilationError.Should().Contain("could not be ESTABLISHED",
            "the terminal message must name the true defect — the source query that did not "
            + "answer — so an operator is not sent to fix code that was never in question");
        starvedDef.CompilationError.Should().NotContain("CS0103",
            "a compile that was never attempted cannot have produced a Roslyn diagnostic; a "
            + "phantom CS0103/CS0246 over a short source set is the entire bug");

        // 2️⃣ A REAL compile error on an ESTABLISHED source set is untouched — it still gates.
        brokenDef.CompilationStatus.Should().Be(CompilationStatus.Error,
            "this fix must not weaken the gate: Roslyn rejected code it could actually see, and "
            + "that is exactly the regression a bake gate exists to catch");

        // 3️⃣ …and therefore the gate treats them differently.
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkOutcome(OutcomeFor(StarvedPath, starvedDef));
        gate.Phase.Should().NotBe(BakePhase.Regressed,
            "an unreadable mesh is not an image regression — gating on it stalls the rollout and "
            + "leaves disarming the gate as the operator's only lever");
        gate.Unevaluated.Keys.Should().Contain(StarvedPath);

        gate.MarkOutcome(OutcomeFor(BrokenPath, brokenDef));
        gate.Phase.Should().Be(BakePhase.Regressed,
            "a genuine compile error on a previously-healthy type is the regression the gate is FOR");
        gate.Regressions.Keys.Should().Contain(BrokenPath);
    }

    /// <summary>
    /// The exact mapping <c>DynamicTypePreWarmer.WarmOne</c> applies to a settled compile — the
    /// terminal status is what carries the distinction into the gate.
    /// </summary>
    private static PreWarmOutcome OutcomeFor(string path, NodeTypeDefinition d) =>
        new(path,
            d.CompilationStatus == CompilationStatus.Unavailable
                ? PreWarmStatus.TimedOut
                : DynamicTypePreWarmer.ClassifyCompileFailure(d),
            d.CompilationError)
        { WasHealthyBeforeBake = true };
}

/// <summary>
/// Test-only <see cref="IMeshQueryProvider"/> that ERRORS any query touching the starved type's
/// source namespace — the deterministic stand-in for a cross-silo source read that starves against
/// a busy peer silo during a rollout (a <c>SubscribeRequest</c> that answers with a timeout /
/// delivery failure rather than with rows). Every other query gets an immediate empty Initial, so
/// the rest of the mesh behaves normally and the real providers supply the actual data.
/// </summary>
public sealed class StarvedSourceQueryProvider : IMeshQueryProvider
{
    /// <summary>
    /// The starved type's expanded SOURCE query, exactly as
    /// <see cref="CodeQueryResolver.ExpandAll"/> produces it
    /// (<c>namespace:{selfPath}/Source scope:subtree nodeType:Code</c>).
    ///
    /// <para>🚨 Matched on the <c>namespace:</c> FORM, not on the bare path, so only source
    /// DISCOVERY starves. Node creation and single-node reads under the same subtree use
    /// <c>path:</c>-shaped queries and must keep working — otherwise the test could not put a
    /// healthy source node in the mesh in the first place, and "the code is fine, the read is
    /// not" would be unprovable. Note the type's <c>/Test</c> query is deliberately NOT starved:
    /// one leg dies while the other answers, which is precisely the PARTIAL source set that used
    /// to reach Roslyn.</para>
    /// </summary>
    private const string StarveMarker = "namespace:SourceUnavailableTest/StarvedSourceType/Source";

    /// <inheritdoc />
    public string Name => nameof(StarvedSourceQueryProvider);

    /// <inheritdoc />
    public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(
        MeshQueryRequest request, JsonSerializerOptions options)
    {
        var starve = request.EffectiveQueries
            .Any(q => q?.Contains(StarveMarker, StringComparison.OrdinalIgnoreCase) == true);
        return starve
            ? Observable.Throw<QueryResultChange<T>>(new TimeoutException(
                "No response received for SubscribeRequest within 00:01:00 — the owning "
                + "activation lives on the outgoing silo (test stand-in for cross-silo starvation)"))
            : Observable.Return(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Items = Array.Empty<T>(),
                Timestamp = DateTimeOffset.UtcNow,
            });
    }

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

/// <summary>
