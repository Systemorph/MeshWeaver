#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// #1732 — the install must not race its OWN teardown.
///
/// <para><b>The defect.</b> <c>PackageInstaller.InstallNodeRepoCore</c> launched a compile of
/// EVERY installed NodeType fire-and-forget (<c>SeedThenRequestReleases</c>, a <c>void</c> method)
/// and then, a few <c>SelectMany</c>s later in the SAME continuation chain, posted a
/// <c>DisposeRequest</c> to the retyped package ROOT (<c>SettleRetypedRoot</c>). Every one of
/// those compiles reads that root — a <c>shared=</c> consumer runs the cell-surface single-home
/// gate, <c>ValidateCellSurfaceSingleHome</c> → <c>ReadCompileSourceNode(owner)</c> →
/// <c>GetMeshNode('&lt;packageRoot&gt;')</c> — so the installer deliberately tore down the hub its
/// own just-started work was reading. In the field the faulting set was always exactly the
/// module's <c>shared=</c> consumers: 3 of 3 for Claims, 33 for Ifrs17, never a non-<c>shared=</c>
/// type. #1726 made those reads PATIENT (they re-probe across a recycle and degrade to
/// <c>CompilationStatus.Unavailable</c> rather than a code verdict); it did NOT make the ordering
/// right, and a read with a tighter budget — or a teardown that outlasts the budget (#1701) —
/// brings the mystery compile failures straight back.
///
/// <para><b>What is pinned.</b> The release trigger each NodeType carries
/// (<c>NodeTypeDefinition.RequestedReleaseAt</c>, stamped by <c>ObserveNodeTypeRelease</c> at flip
/// time) versus the moment the root's hub received the recycle's <c>DisposeRequest</c>. The
/// contract is a SPLIT, not a blanket "release last":</para>
/// <list type="bullet">
///   <item>the root's OWN in-package type is released BEFORE the recycle — the recycle's
///     <c>MayPublishIntoRoot</c> gate is a wait for precisely that type's rebuild, so deferring it
///     would wait 90 s for a compile nobody asked for;</item>
///   <item>every OTHER installed type is released AFTER the root has been recycled and answered
///     again — those are the compiles that read the root.</item>
/// </list>
/// The test therefore fails in BOTH directions: on the old fire-and-forget shape the deferred
/// type's trigger predates the DisposeRequest, and on a naive "defer everything" fix the install
/// blows its budget on the root type's settle timeout.
/// </summary>
public class InstallReleaseOrderingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PackageId = "Kit";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(120);

    // One real Roslyn compile (the root type's rebuild, which the recycle waits for) plus the
    // install's own barriers.
    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(150);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(280);

    /// <summary>
    /// Every recycle the package root's hub received, in order. Captured on the hub itself so the
    /// ordering is asserted against the PRODUCTION teardown, not a test-side approximation — and
    /// as a LIST because an install legitimately produces more than one: the framework's
    /// <c>NodeTypeRebindWatcher</c> recycles the root when the change feed reports the retype, and
    /// <c>SettleRetypedRoot</c> recycles it again once the in-package type has rebuilt.
    /// </summary>
    private readonly ConcurrentQueue<DateTimeOffset> _rootRecycles = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
            .ConfigureDefaultNodeHub(config =>
            {
                if (config.Address.ToString() != PackageId)
                    return config;
                // Passive observer: returns the delivery UNPROCESSED so the framework's own
                // HandleDispose still runs (config handlers are registered last and therefore run
                // FIRST — MessageHub.Register uses AddFirst).
                return config.WithHandler<DisposeRequest>((_, delivery) =>
                {
                    _rootRecycles.Enqueue(DateTimeOffset.UtcNow);
                    return delivery;
                });
            });

    // ── The fixture: a SELF-TYPED root (the Store shape — the only shape that reaches
    //    SettleRetypedRoot, because only it runs the placeholder dance) PLUS a second in-package
    //    NodeType that the root does not use. That second type is the whole point: it is the one
    //    whose compile used to be launched into a hub the installer was about to dispose. ──

    private static readonly IReadOnlyList<RepoFile> Repo = new List<RepoFile>
    {
        new("Kit/index.json",
            """{"$type":"MeshNode","id":"Kit","namespace":"","path":"Kit","mainNode":"Kit","name":"Kit","nodeType":"Kit/Front","state":"Active","content":{"$type":"FrontContent","intro":"hello"}}"""),
        // The ROOT's type — wave one.
        new("Kit/Front.json",
            """{"$type":"MeshNode","id":"Front","namespace":"Kit","path":"Kit/Front","mainNode":"Kit/Front","name":"Front","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"The kit front.","configuration":"config => config.WithContentType<FrontContent>()","includeGlobalTypes":true}}"""),
        new("Kit/Front/Source/FrontContent.cs",
            "public record FrontContent { public string? Intro { get; init; } }"),
        // A SECOND type, used by nothing in this package — wave two.
        new("Kit/Widget.json",
            """{"$type":"MeshNode","id":"Widget","namespace":"Kit","path":"Kit/Widget","mainNode":"Kit/Widget","name":"Widget","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A widget.","configuration":"config => config.WithContentType<WidgetContent>()","includeGlobalTypes":true}}"""),
        new("Kit/Widget/Source/WidgetContent.cs",
            "public record WidgetContent { public string? Label { get; init; } }"),
    };

    [Fact(Timeout = 240_000)]
    public async Task DeferredNodeTypeReleases_AreRequestedAfterTheRootRecycle()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-kit", Repo));
        var source = new NodeRepoPackageSource(fetch, "https://github.com/acme/kit");
        var manifest = new PackageManifest
        {
            Id = PackageId,
            Name = PackageId,
            Kind = PackageKind.NodeRepo,
            TargetPartition = PackageId,
            SourceFolder = PackageId,
            Version = "commit-kit",
        };
        var files = await source.FetchPackageFiles(manifest, "HEAD").FirstAsync().ToTask();

        var result = await PackageInstaller.Install(Mesh, manifest, files, "HEAD")
            .FirstAsync().Timeout(StepTimeout).ToTask();
        result.Written.Should().Be(Repo.Count);

        // PRECONDITION, asserted so its failure reads as itself: SettleRetypedRoot only recycles a
        // root whose in-package type has a build an instance could load. If Kit/Front did not
        // compile there is no recycle to order around and every assertion below would fail for a
        // reason that has nothing to do with ordering.
        var frontDef = await Mesh.GetWorkspace().GetMeshNodeStream($"{PackageId}/Front")
            // ContentAs, never `is NodeTypeDefinition`: a cross-hub mirror snapshot is routinely
            // un-materialized JSON, and a CLR type test blinds this to an in-flight compile — the
            // same reason MayPublishIntoRoot reads it this way.
            .Select(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(def => def?.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error)
            .FirstAsync().Timeout(StepTimeout).ToTask();
        // Print the ORDER before asserting anything about it. An ordering test that fails without
        // naming the sequence it saw costs a full investigation per sighting (#1811), and the
        // precondition below is exactly where the reader is left with nothing: it fires BEFORE the
        // stamps are read, so without this the trx shows a compile error and no ordering evidence
        // at all — which is how this test's one recorded failure got filed as an ordering race.
        var recycles = _rootRecycles.ToArray();
        Output.WriteLine(recycles.Length == 0
            ? "root recycles observed: NONE"
            : $"root recycles observed: {string.Join(", ", recycles.Select(r => r.ToString("O")))}");

        frontDef!.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "the root's own type must compile or there is no recycle to order around — and THIS "
            + "assertion is about the mesh's compiler, not about ordering: "
            + $"{ClassifyCompileFailure(frontDef.CompilationError)}");

        recycles.Should().NotBeEmpty(
            "the retyped root must be recycled — SettleRetypedRoot is what this ordering is about");

        var front = await ReleaseTrigger($"{PackageId}/Front");
        var widget = await ReleaseTrigger($"{PackageId}/Widget");
        Output.WriteLine($"{PackageId}/Front  requestedReleaseAt={front:O}");
        Output.WriteLine($"{PackageId}/Widget requestedReleaseAt={widget:O}");

        // Wave one before wave two. Necessary but not sufficient — the old fire-and-forget shape
        // satisfies this too, by microseconds, from inside one `foreach`.
        front.Should().BeBefore(widget,
            "the root's own in-package NodeType goes in the first wave, everything else in the second");

        // THE pin: a recycle of the root sits BETWEEN the two waves. That is the whole contract —
        // the root's own type is released before the recycle (its rebuild is what
        // SettleRetypedRoot waits for; deferring it would stall on RootTypeSettleTimeout, 90 s,
        // which the install budget above also catches), and every other type is released only
        // once that recycle has settled.
        //
        // On the fire-and-forget shape both stamps land in the same turn, microseconds apart and
        // strictly BEFORE any recycle, so no recycle can fall between them and this fails.
        var sandwiched = recycles.Where(r => r > front && r < widget).ToArray();
        sandwiched.Should().NotBeEmpty(
            "a NodeType compile reads the package root (ValidateCellSurfaceSingleHome → "
            + "GetMeshNode('<packageRoot>')), so the deferred types' releases must not be issued "
            + "until the installer's own recycle of that root has settled — but no recycle was "
            + $"observed between {front:O} and {widget:O} (#1732)");
    }

    /// <summary>
    /// Turns the failed compile's recorded error into a sentence that says WHICH defect this is,
    /// so the reader does not have to re-derive it from a Roslyn stack.
    ///
    /// <para>The precondition above fails for two completely different reasons and they need
    /// completely different owners. A Roslyn <b>diagnostic</b> means this fixture's C# stopped
    /// compiling — the test's own problem. An emit-phase <b>fault</b> (Roslyn THREW; the compile
    /// pipeline stamps its two-leg canary verdict onto the message) means the mesh could not emit
    /// at all, which is <b>#890</b> — a process-level condition this test merely observed, and
    /// nothing about install-vs-recycle ordering. The one recorded failure of this test
    /// (run 32069247537, shard 4, on a docs-only branch) was the second kind and was filed as the
    /// first, which is the entire cost this line exists to remove.</para>
    /// </summary>
    private static string ClassifyCompileFailure(string? compilationError)
    {
        if (string.IsNullOrWhiteSpace(compilationError))
            return "the compile reported no error text at all";
        // The canary verdict is stamped only on an emit-phase THROW (EmitPipeline's catch around
        // compilation.Emit), so its presence IS the discriminator — no string-matching on
        // exception names.
        return compilationError.Contains("canary=", StringComparison.Ordinal)
            ? "Roslyn THREW while emitting rather than reporting diagnostics — this is #890 "
              + "(a process-level emit fault), NOT an ordering race, and this test only observed "
              + $"it. Verdict + error: {compilationError}"
            : $"the fixture's own C# failed to compile: {compilationError}";
    }

    /// <summary>
    /// The release trigger the installer stamped on <paramref name="typePath"/>. The install
    /// completes only once every flip has LANDED, so this read never waits on the compile that
    /// follows it — the value is already there.
    /// </summary>
    private async Task<DateTimeOffset> ReleaseTrigger(string typePath)
    {
        // ContentAs, never a CLR type test — see the note at the precondition read above.
        var def = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Select(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d?.RequestedReleaseAt is not null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask();
        return def!.RequestedReleaseAt!.Value;
    }
}
