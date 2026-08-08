#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Plugin DEPENDENCY TRACKING (#636): a package declares what it needs
/// (<see cref="PackageManifest.Requires"/>), and every install path derives an order from that
/// declaration so a dependent is never imported before the package whose NodeTypes it uses.
///
/// <para>The failure this prevents is not subtle — it is an outright refusal. The installer
/// validates that every type an incoming node claims already exists, so importing the dependent
/// first dies with "NodeType(s) not registered: …", naming a path that appears nowhere in the
/// package the user asked for. <see cref="DependentAlone_IsRefused_BecauseItsTypeIsNotThereYet"/>
/// pins that, and the tests around it pin that the declared order removes it.</para>
/// </summary>
public class PluginDependencyOrderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    /// <summary>
    /// Two node-repo packages in the real shape: <c>Base</c> ships a NodeType (plus its source), and
    /// <c>Dependent</c> ships an INSTANCE of that type while declaring <c>Base@^1.0.0</c> on its
    /// root content. Nothing but the declaration says the two are related.
    /// </summary>
    private static readonly IReadOnlyList<RepoFile> Repo = new List<RepoFile>
    {
        new("Base/index.json",
            """{"$type":"MeshNode","id":"Base","namespace":"","path":"Base","mainNode":"Base","name":"Base","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"ships the shared type"}}"""),
        new("Base/Widget.json",
            """{"$type":"MeshNode","id":"Widget","namespace":"Base","path":"Base/Widget","mainNode":"Base/Widget","name":"Widget","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"the shared type"}}"""),
        new("Base/Widget/Source/Widget.cs",
            "public record Widget { public string Name { get; init; } = string.Empty; }"),

        new("Dependent/index.json",
            """{"$type":"MeshNode","id":"Dependent","namespace":"","path":"Dependent","mainNode":"Dependent","name":"Dependent","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"uses the shared type","requires":["Base@^1.0.0"]}}"""),
        new("Dependent/Item.json",
            """{"$type":"MeshNode","id":"Item","namespace":"Dependent","path":"Dependent/Item","mainNode":"Dependent/Item","name":"Item","nodeType":"Base/Widget","state":"Active"}"""),
    };

    private static NodeRepoPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-deps", Repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");
    }

    private static async Task<IReadOnlyList<PackageManifest>> Catalog() =>
        await Source().ListPackages("HEAD").FirstAsync().ToTask();

    private async Task<InstallResult> Install(PackageManifest pkg)
    {
        var files = await Source().FetchPackageFiles(pkg, "HEAD").FirstAsync().ToTask();
        return await PackageInstaller.Install(Mesh, pkg, files, "commit-deps")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(90)).ToTask();
    }

    /// <summary>
    /// The declaration survives the whole path from the repo file to the catalog entry the
    /// installers order by. Without this the graph has nothing to sort and every later assertion
    /// would pass vacuously.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RootContent_Requires_ReachesTheCatalogEntry()
    {
        var catalog = await Catalog();

        catalog.Select(p => p.Id).Should().Equal(["Base", "Dependent"]);
        catalog.Single(p => p.Id == "Dependent").Requires
            .Should().Equal(["Base@^1.0.0"],
                "the root's declared requires is what every install path orders by");
        catalog.Single(p => p.Id == "Base").Requires.Should().BeEmpty();
    }

    /// <summary>
    /// Proof that the ordering is not cosmetic: importing the dependent on its own is REFUSED,
    /// because the type its instance claims does not exist yet.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task DependentAlone_IsRefused_BecauseItsTypeIsNotThereYet()
    {
        var catalog = await Catalog();
        var dependent = catalog.Single(p => p.Id == "Dependent");

        var act = () => Install(dependent);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Base/Widget",
                "the refusal names the missing type — which is exactly why the order has to be "
                + "derived rather than left to whoever clicks first");
    }

    /// <summary>
    /// The closure a single Install click resolves: the dependency first, the clicked package last.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task InstallClosure_PutsTheDependencyFirst()
    {
        var catalog = await Catalog();
        var dependent = catalog.Single(p => p.Id == "Dependent");

        var closure = PackageDependencyGraph.InstallClosure(
            dependent, catalog, ImmutableHashSet<string>.Empty, NullLogger.Instance);

        closure.Select(p => p.Id).Should().Equal(["Base", "Dependent"]);
    }

    /// <summary>
    /// End to end: installing the closure IN ORDER lands both packages, and the dependent's
    /// instance carries the type its dependency shipped. This is the same order the boot pass and
    /// the Install click both derive.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task InstallingTheClosureInOrder_LandsTheDependentTyped()
    {
        var catalog = await Catalog();
        var dependent = catalog.Single(p => p.Id == "Dependent");
        var closure = PackageDependencyGraph.InstallClosure(
            dependent, catalog, ImmutableHashSet<string>.Empty, NullLogger.Instance);

        foreach (var pkg in closure)
            (await Install(pkg)).Written.Should().BeGreaterThan(0, $"{pkg.Id} must land");

        var item = await Mesh.GetWorkspace().GetMeshNodeStream("Dependent/Item")
            .Where(n => n is not null).Select(n => n!)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        item.NodeType.Should().Be("Base/Widget",
            "the instance is typed by the NodeType its DEPENDENCY shipped — the whole point of "
            + "installing Base first");
    }

    /// <summary>
    /// An already-installed dependency is not re-installed: the closure narrows to the package the
    /// user actually clicked.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnInstalledDependency_IsNotReinstalled()
    {
        var catalog = await Catalog();
        var dependent = catalog.Single(p => p.Id == "Dependent");

        var closure = PackageDependencyGraph.InstallClosure(
            dependent, catalog, ImmutableHashSet.Create("Base"), NullLogger.Instance);

        closure.Select(p => p.Id).Should().Equal(["Dependent"]);
    }

    /// <summary>
    /// A dependency the catalog does not offer does not block the install — the instance may simply
    /// not be granted it, and the installer's own refusal is the accurate error if it truly matters.
    /// </summary>
    [Fact]
    public void AnUnofferedDependency_DoesNotBlockTheInstall()
    {
        var target = new PackageManifest { Id = "Solo", Requires = ["NotInThisCatalog@^2.0.0"] };

        var closure = PackageDependencyGraph.InstallClosure(
            target, [target], ImmutableHashSet<string>.Empty, NullLogger.Instance);

        closure.Select(p => p.Id).Should().Equal(["Solo"]);
    }

    /// <summary>
    /// A cycle is REFUSED with the loop spelled out — never silently installed in an arbitrary
    /// order (which fails later naming a NodeType from neither package) and never walked forever.
    ///
    /// <para>Deliberately the opposite policy from the unattended boot pass, which warns and
    /// installs every package anyway
    /// (<c>DefaultPackageInstallTest.DependencyCycle_StillInstallsEveryPackageOnce</c>): there is
    /// nobody at boot to tell, and one malformed package must not strand an instance.</para>
    /// </summary>
    [Fact]
    public void ADependencyCycle_IsRefusedWithTheLoopNamed()
    {
        var a = new PackageManifest { Id = "A", Requires = ["B@^1.0.0"] };
        var b = new PackageManifest { Id = "B", Requires = ["A@^1.0.0"] };

        Action act = () => _ = PackageDependencyGraph.InstallClosure(
            a, [a, b], ImmutableHashSet<string>.Empty, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("A → B → A");
    }

    /// <summary>
    /// What the tolerant sort ACTUALLY guarantees inside a cycle — pinned because the comments used
    /// to promise "degrades to catalog order", which the depth-first walk never did (for
    /// <c>A→B, B→A</c> it yields <c>[B, A]</c>, the reverse of catalog order).
    ///
    /// <para>The real guarantee is better than the one that was promised: only the BACK EDGE
    /// closing the loop is dropped, so a dependency that both cycle members need is still installed
    /// before either of them. A literal catalog-order fallback would have violated exactly that —
    /// here it would put <c>Lib</c> last, after the two packages that require it.</para>
    /// </summary>
    [Fact]
    public void ACycle_DropsOnlyTheBackEdge_AndKeepsEveryOtherConstraint()
    {
        // A ↔ B is a cycle; BOTH also depend on Lib, which is outside it. Catalog order is
        // [A, B, Lib] — the order a "fall back to catalog order" policy would have produced.
        var a = new PackageManifest { Id = "A", Requires = ["B@^1.0.0", "Lib@^1.0.0"] };
        var b = new PackageManifest { Id = "B", Requires = ["A@^1.0.0", "Lib@^1.0.0"] };
        var lib = new PackageManifest { Id = "Lib" };

        var ordered = PackageDependencyGraph
            .InDependencyOrder([a, b, lib], NullLogger.Instance)
            .Select(p => p.Id).ToList();

        ordered.Should().HaveCount(3, "a cycle must never drop or duplicate a package");
        ordered.IndexOf("Lib").Should().BeLessThan(ordered.IndexOf("A"),
            "the satisfiable edge out of the cycle still has to be honoured");
        ordered.IndexOf("Lib").Should().BeLessThan(ordered.IndexOf("B"),
            "the satisfiable edge out of the cycle still has to be honoured");
    }

    /// <summary>
    /// A cycle among packages the click does NOT touch is irrelevant — resolution only looks at the
    /// reachable set, so one malformed pair elsewhere in the catalog cannot block every install.
    /// </summary>
    [Fact]
    public void ACycleElsewhereInTheCatalog_DoesNotBlockAnUnrelatedInstall()
    {
        var solo = new PackageManifest { Id = "Solo" };
        var a = new PackageManifest { Id = "A", Requires = ["B"] };
        var b = new PackageManifest { Id = "B", Requires = ["A"] };

        var closure = PackageDependencyGraph.InstallClosure(
            solo, [solo, a, b], ImmutableHashSet<string>.Empty, NullLogger.Instance);

        closure.Select(p => p.Id).Should().Equal(["Solo"]);
    }

    /// <summary>
    /// Declaring dependencies does not cost idempotence: re-installing the same ref writes NOTHING.
    ///
    /// <para>Worth pinning because the root carrying <c>requires</c> is the one node whose content
    /// the installer compares as TYPED-stored-vs-incoming. The comparison stays symmetric — the
    /// incoming root deserializes into the same registered <see cref="PluginManifest"/> the stored
    /// one did, so the member the record does not declare is dropped from BOTH sides — and an
    /// unchanged package is therefore never rewritten or recompiled. (The corollary, deliberately
    /// NOT fixed here: the installed root does not carry its declaration, so a dependency-status
    /// surface has to read the catalog listing rather than the node — issue #636's health-surface
    /// item.)</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ReinstallingADependencyDeclaringPackage_WritesNothing()
    {
        var catalog = await Catalog();
        foreach (var pkg in PackageDependencyGraph.InstallClosure(
                     catalog.Single(p => p.Id == "Dependent"), catalog,
                     ImmutableHashSet<string>.Empty, NullLogger.Instance))
            await Install(pkg);

        var again = await Install(catalog.Single(p => p.Id == "Dependent"));
        again.Written.Should().Be(0,
            "nothing changed, so nothing may be rewritten — a rewrite here would recompile the "
            + "package's NodeTypes on every single sync");
    }

    /// <summary>
    /// Two packages that share TYPES, not just node types: <c>Lib</c> ships a record, and
    /// <c>App</c>'s own source references it by pulling <c>Lib</c>'s sources in with
    /// <c>shared=@Lib/Common/Source</c>.
    ///
    /// <para>🚨 This is what "loaded and compiled before dependents" actually reduces to on this
    /// mesh. Cross-NodeType type sharing is SOURCE-TEXT inclusion — a sharer compiles the shared
    /// files into its OWN assembly — so a dependent never needs its dependency's assembly to be
    /// RELEASED; it needs the dependency's <c>Code</c> NODES to be present when its own compile
    /// runs. That is why this change adds no wait-for-release step: there is no assembly-level edge
    /// for one to guard.</para>
    ///
    /// <para>🚨 Scope of this test, stated honestly: it pins that the cross-package sharing path
    /// WORKS end to end, NOT that the install order is what makes it work. Measured (installing the
    /// two packages in the reverse order still compiles Ok): the release trigger is fire-and-forget
    /// and the sharer's sources are resolved when the compile actually RUNS, by which time the
    /// other package has usually landed too. So the order closes a RACE rather than a certainty —
    /// but the race is worth closing, because losing it is not a retryable miss: the source queries
    /// match zero nodes, Roslyn emits CS0246, and the type is PARKED at
    /// <c>CompilationStatus.Error</c> — a park only cleared by the type's OWN source snapshot
    /// changing or an explicit re-release, never by the dependency showing up afterwards.</para>
    /// </summary>
    private static readonly IReadOnlyList<RepoFile> SharingRepo = new List<RepoFile>
    {
        new("Lib/index.json",
            """{"$type":"MeshNode","id":"Lib","namespace":"","path":"Lib","mainNode":"Lib","name":"Lib","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"ships a shared record"}}"""),
        new("Lib/Common.json",
            """{"$type":"MeshNode","id":"Common","namespace":"Lib","path":"Lib/Common","mainNode":"Lib/Common","name":"Common","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"the shared library type","configuration":"config => config.WithContentType<Money>()"}}"""),
        new("Lib/Common/Source/Money.cs",
            "public record Money { public decimal Amount { get; init; } public string Currency { get; init; } = \"CHF\"; }"),

        new("App/index.json",
            """{"$type":"MeshNode","id":"App","namespace":"","path":"App","mainNode":"App","name":"App","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"uses the shared record","requires":["Lib@^1.0.0"]}}"""),
        // Its own Source subtree PLUS Lib's — the `shared=` group is what makes Money resolvable.
        new("App/Invoice.json",
            """{"$type":"MeshNode","id":"Invoice","namespace":"App","path":"App/Invoice","mainNode":"App/Invoice","name":"Invoice","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"references Lib's record","configuration":"config => config.WithContentType<Invoice>()","sources":["namespace:Source scope:subtree","shared=@Lib/Common/Source"]}}"""),
        new("App/Invoice/Source/Invoice.cs",
            "public record Invoice { public Money Total { get; init; } = new(); }"),
    };

    [Fact(Timeout = 300_000)]
    public async Task ADependentsSource_CompilesAgainstTheTypeItsDependencyShips()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-sharing", SharingRepo));
        var source = new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");
        var catalog = await source.ListPackages("HEAD").FirstAsync().ToTask();

        var app = catalog.Single(p => p.Id == "App");
        app.Requires.Should().Equal(["Lib@^1.0.0"]);

        var closure = PackageDependencyGraph.InstallClosure(
            app, catalog, ImmutableHashSet<string>.Empty, NullLogger.Instance);
        closure.Select(p => p.Id).Should().Equal(["Lib", "App"],
            "the shared source should be on the mesh before the sharer's compile is even triggered");

        foreach (var pkg in closure)
        {
            var files = await source.FetchPackageFiles(pkg, "HEAD").FirstAsync().ToTask();
            await PackageInstaller.Install(Mesh, pkg, files, "commit-sharing")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();
        }

        // The dependent's own record has a member of the DEPENDENCY's type. Ok here means Roslyn
        // resolved Money — i.e. Lib's Code nodes were on the mesh when App/Invoice compiled.
        var invoice = await Mesh.GetMeshNodeStream("App/Invoice")
            .Should().Within(240.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);

        var def = (NodeTypeDefinition)invoice.Content!;
        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the dependent must compile against its dependency's shared source; error: {def.CompilationError}");
    }
}
