#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// #1209 — A STATIC node must not silently shadow durable content at the same path.
///
/// <para>The incident: a host calling bare <c>.AddAI()</c> registers the built-in Agent/Skill
/// catalogs as static node providers serving the paths <c>Agent</c> and <c>Skill</c>. Installing the
/// DURABLE <c>Agent</c>/<c>Skill</c> plugin packages — which write real rows at exactly those paths —
/// collided: the root's create was answered "node already exists" BY THE STATIC ENTRY (persistence
/// held nothing), the installer fell back to an UPDATE, that update activated the per-node hub on the
/// STATIC node — a hub seeded from a node that is by design never persisted, which emits one Full
/// snapshot at v0 and never again — and the install's post-write confirmation
/// (<c>RootRetypeReconciled</c>) waited out its 30 s and threw a bare <c>TimeoutException</c> with
/// 0 nodes imported and nothing naming the cause.</para>
///
/// <para>Both halves are pinned here, because the fix must be surgical: the collision fails LOUDLY
/// and IMMEDIATELY (naming the path, the claimant and the <c>serveFromPartition</c> cure), while
/// static-ONLY serving — the legitimate configuration on every host that serves
/// <c>Doc</c>/<c>Agent</c>/<c>Harness</c>/<c>Skill</c> from memory and installs no durable package
/// there — keeps working untouched.</para>
/// </summary>
public class StaticShadowedInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// A static NodeType definition at the TOP-LEVEL path <c>Widget</c> — the exact shape of
    /// <c>AgentNodeType.CreateMeshNode()</c> on a bare <c>.AddAI()</c> host: an
    /// <c>AddMeshNodes</c> seed node (surfaced through <c>StaticMeshNodeListProvider</c>, an
    /// <see cref="IStaticNodeProvider"/>) that is NOT definition-only, so it wins every serve seam
    /// at that path while having no persistence backing.
    /// </summary>
    private static MeshNode WidgetTypeDefinition() => new("Widget")
    {
        Name = "Widget",
        IsSatelliteType = false,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<WidgetContent>())
    };

    public record WidgetContent
    {
        public string? Intro { get; init; }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
            .AddMeshNodes(WidgetTypeDefinition());

    /// <summary>
    /// The colliding package: a node-repo plugin whose ROOT sits at <c>Widget</c> — the very path the
    /// static definition above serves — and whose root type ships in the package (the self-typed-root
    /// shape, which is what puts <c>RootRetypeReconciled</c>'s 30 s wait in the path).
    /// </summary>
    private static readonly IReadOnlyList<RepoFile> CollidingRepo = new List<RepoFile>
    {
        new("Widget/index.json",
            """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget","nodeType":"Widget/Front","state":"Active","content":{"$type":"FrontContent","intro":"hello"}}"""),
        new("Widget/Front.json",
            """{"$type":"MeshNode","id":"Front","namespace":"Widget","path":"Widget/Front","mainNode":"Widget/Front","name":"Front","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"The widget front.","configuration":"config => config.WithContentType<FrontContent>()","includeGlobalTypes":true}}"""),
        new("Widget/Front/Source/FrontContent.cs",
            "public record FrontContent { public string? Intro { get; init; } }"),
    };

    /// <summary>The same package shape at a path NOTHING serves statically — the control.</summary>
    private static readonly IReadOnlyList<RepoFile> CleanRepo = new List<RepoFile>
    {
        new("Gadget/index.json",
            """{"$type":"MeshNode","id":"Gadget","namespace":"","path":"Gadget","mainNode":"Gadget","name":"Gadget","nodeType":"Gadget/Front","state":"Active","content":{"$type":"GadgetContent","intro":"hello"}}"""),
        new("Gadget/Front.json",
            """{"$type":"MeshNode","id":"Front","namespace":"Gadget","path":"Gadget/Front","mainNode":"Gadget/Front","name":"Front","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"The gadget front.","configuration":"config => config.WithContentType<GadgetContent>()","includeGlobalTypes":true}}"""),
        new("Gadget/Front/Source/GadgetContent.cs",
            "public record GadgetContent { public string? Intro { get; init; } }"),
    };

    private static NodeRepoPackageSource Source(IReadOnlyList<RepoFile> repo)
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-1209", repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/widgets");
    }

    private static PackageManifest Manifest(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = PackageKind.NodeRepo,
        TargetPartition = id,
        SourceFolder = id,
        Version = "commit-1209",
    };

    /// <summary>
    /// 🚨 THE REGRESSION. Installing durable content at a statically-served path must fail LOUDLY and
    /// FAST — naming the contested path, the static claimant, and the <c>serveFromPartition</c> cure —
    /// instead of writing into a hub with no persistence backing and hanging out a 30 s timeout.
    ///
    /// <para>Before the fix this test failed on BOTH counts: the install ran the full placeholder
    /// dance, then <c>RootRetypeReconciled</c> followed a stream that could never carry the durable
    /// type and threw <see cref="TimeoutException"/> after 30 s — so the 20 s budget below tripped
    /// first and the exception was neither an <see cref="InvalidOperationException"/> nor did its
    /// message mention the collision.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task InstallIntoStaticallyServedPath_FailsFast_NamingBothClaimantsAndTheFix()
    {
        // The precondition the whole issue is about: this host SERVES `Widget` statically.
        Mesh.ServiceProvider.FindServedStaticNode("Widget").Should().NotBeNull(
            "the fixture must reproduce the bare-AddAI shape: a static provider claiming the path");

        var manifest = Manifest("Widget");
        var files = await Source(CollidingRepo).FetchPackageFiles(manifest, "HEAD")
            .FirstAsync().ToTask();

        var stopwatch = Stopwatch.StartNew();
        var install = () => PackageInstaller.Install(Mesh, manifest, files, "HEAD")
            .FirstAsync()
            // 20 s is the discriminator: the fix refuses before the first write (sub-second), the
            // unfixed code cannot answer before RootRetypeReconciled's 30 s wait elapses.
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask();

        var error = (await install.Should().ThrowAsync<InvalidOperationException>(
            "a static/durable path collision must be refused up front, not surfaced as a downstream "
            + "timeout")).Which;
        stopwatch.Stop();

        error.Message.Should().Contain("Widget", "the refusal must name the contested path");
        error.Message.Should().Contain("static node provider",
            "the refusal must name WHAT is claiming the path");
        error.Message.Should().Contain("MeshBuilder.AddMeshNodes",
            "the refusal must name WHICH provider claims it");
        error.Message.Should().Contain("serveFromPartition",
            "the refusal must name the per-host configuration that cures it");

        // Tighter than the 20 s budget above on purpose: that one only proves the answer beat
        // RootRetypeReconciled's 30 s wait, this one proves the refusal is the zero-I/O pre-flight
        // it claims to be. Observed ≈0.5 s; 10 s is 20× headroom for a loaded CI runner.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "the collision is decidable with zero I/O — it must never cost a stream round-trip");

        // And nothing was written: the refusal is a pre-flight, not a partial install. Asked of the
        // STORAGE ADAPTER, which answers "no row" instead of routing to a hub that does not exist.
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        (await persistence.Exists($"{PackageInstaller.InstalledPartition}/Widget")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask())
            .Should().BeFalse("a refused install must leave no install record behind");
        (await persistence.Exists("Widget/Front").FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask())
            .Should().BeFalse("a refused install must write none of the package's nodes");
    }

    /// <summary>
    /// The other half of the contract: a host that serves a path statically and installs NOTHING
    /// durable there is completely unaffected. The static node still resolves and is still served by
    /// its per-node hub, and an ordinary install at an unclaimed path proceeds exactly as before.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task StaticOnlyServing_IsUnchanged_AndAnUnclaimedPathStillInstalls()
    {
        // (a) The statically-served node is still SERVED — read through the same per-node hub the
        //     GUI and every reader use.
        var served = await Mesh.GetWorkspace().GetMeshNodeStream("Widget")
            .Where(n => n is not null).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        served!.Name.Should().Be("Widget",
            "static-only serving must keep working — every host that serves Doc/Agent/Harness/Skill "
            + "from memory depends on it");

        // (b) An install at a path NOTHING claims statically is untouched by the new pre-flight.
        var manifest = Manifest("Gadget");
        var files = await Source(CleanRepo).FetchPackageFiles(manifest, "HEAD")
            .FirstAsync().ToTask();

        var result = await PackageInstaller.Install(Mesh, manifest, files, "HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).ToTask();
        result.Written.Should().Be(3, "an uncontested install must be unaffected by the collision guard");

        var root = await Mesh.GetWorkspace().GetMeshNodeStream("Gadget")
            .Where(n => n is not null).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        root!.NodeType.Should().Be("Gadget/Front");
    }
}
