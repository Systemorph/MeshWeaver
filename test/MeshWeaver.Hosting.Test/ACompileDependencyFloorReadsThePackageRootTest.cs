using System;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Systemorph/MeshWeaver#6195 — the PRODUCTION reading behind the compile half of the
/// dependency floor (<see cref="NodeTypeDependencyFloorReader"/>): the requirements the package
/// root declares, judged against what this process LOADED.
///
/// <para>The measured case: on memex-cloud 2026-10-28 the package root <c>Hosting</c> declared
/// <c>requires: ["Store@^1.35.0", "AI@^1.40.0", …]</c> while the outgoing replicas ran AI 1.39.8.
/// The reading must name that requirement and both versions; with a satisfying AI loaded, with a
/// root that declares nothing, and with no loaded version, it must abstain. One seam is
/// substituted, as in <c>ADependencyFloorHoldsTheImportTest</c>: the loaded-module reading,
/// because a test process cannot load two builds of one module.</para>
/// </summary>
public class ACompileDependencyFloorReadsThePackageRootTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "FloorPkg";
    private const string Bare = "FloorBare";

    private readonly FakeLoadedModules loaded = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILoadedPackageModules>(loaded));

    private NodeTypeDependencyFloorReader Reader => new(Mesh, loaded);

    private static JsonElement PluginRootContent(params string[] requires) =>
        JsonSerializer.SerializeToElement(new { version = "1.73", requires });

    /// <summary>The package root as the import writes it: its <c>index.json</c> content, carrying the
    /// requirement list (untyped here; typed as the Store's plugin content in production —
    /// <see cref="NodeTypeDependencyFloorReader.RequiresOf"/> reads both, see below).</summary>
    private static MeshNode PackageRoot(params string[] requires) =>
        new(Package) { NodeType = "Store/Plugin", Content = PluginRootContent(requires) };

    private static Task<string?> Settled(IObservable<string?> reading, CancellationToken ct) =>
        reading.Should().Within(TestTimeouts.Convergence).Emit("the reading emits exactly once", ct);

    private static ImmutableDictionary<string, LoadedPackageModule> Loaded(string? aiVersion) =>
        aiVersion is null
            ? ImmutableDictionary<string, LoadedPackageModule>.Empty
            : ImmutableDictionary<string, LoadedPackageModule>.Empty
                .Add("AI", new LoadedPackageModule("MeshWeaver.AI", aiVersion));

    private string? Judge(MeshNode? root, string? aiVersion) =>
        NodeTypeDependencyFloorReader.Judge(Package, root, Loaded(aiVersion), Mesh.JsonSerializerOptions);

    [Fact]
    public void TheMeasuredCase_NamesTheRequirementAndBothVersions_AndASatisfyingLoadClearsIt()
    {
        var root = PackageRoot("Store@^1.35.0", "AI@^1.40.0", "Essentials@^1.13.0");

        var unmet = Judge(root, "1.39.8");
        unmet.Should().NotBeNull("AI 1.39.8 does not satisfy ^1.40.0 — the replica must not compile");
        unmet!.Should().Contain($"package '{Package}'").And.Contain("AI@^1.40.0")
            .And.Contain("MeshWeaver.AI").And.Contain("1.39.8");

        Judge(root, "1.40.2").Should().BeNull(
            "a replica that runs a satisfying AI compiles — the positive arm of the same root");
    }

    [Fact]
    public void ItAbstains_WhereThereIsNothingToJudge()
    {
        Judge(PackageRoot(), "1.39.8").Should().BeNull("a root that declares no requirement");
        Judge(null, "1.39.8").Should().BeNull("a root that could not be read is not judged");
        Judge(PackageRoot("AI@^1.40.0"), aiVersion: null).Should().BeNull(
            "a dependency with no loaded version here is NOT JUDGED — absent is never 'unmet'");
    }

    /// <summary>
    /// The read half, through the real mesh: a partition root that declares nothing (a Space) is
    /// read and abstains, and a path that names no child of a root is not judged at all.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheReadThroughTheMesh_AbstainsOnARootThatDeclaresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(new MeshNode(Bare)
        {
            NodeType = "Space",
            Name = "Partition without requirements",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        loaded.Set("AI", "MeshWeaver.AI", "1.39.8");

        (await Settled(Reader.Unmet($"{Bare}/Deployment"), ct)).Should().BeNull();
        (await Settled(Reader.Unmet(Bare), ct)).Should().BeNull("a root itself is no NodeType inside a package");
    }

    /// <summary>
    /// 🚨 The read never ACTIVATES the package root. It sits on the activation path — hydrating or
    /// compiling a NodeType is what an instance's activation waits for — and a package root is
    /// commonly an instance of a type its own package defines (<c>Store</c> is a
    /// <c>Store/Catalog</c>). Read through the root's own hub, activating a cold self-typed root
    /// waited on itself until the read's budget ran out, and the floor was then silently not
    /// judged. So the root is read as persisted, and its address stays cold. POSITIVE ARM in the
    /// same test: the requirement is still read and judged from that cold root.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheRootIsJudged_WithoutActivatingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = $"FloorCold{Guid.NewGuid().ToString("N")[..8]}";
        var address = new Address(root);

        // The activation signal itself, connected before anything is read (the subject is hot and a
        // hub is built on the router's own turn) — see ADisposeRequestNeverInstantiatesAHubTest.
        var built = Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>()
            .HubAdded
            .Where(h => (h.Address with { Host = null }).Equals(address))
            .Replay(1);
        using var watching = built.Connect();

        await NodeFactory.CreateNode(new MeshNode(root)
        {
            // A partition root (only a partition-owning type may sit at the top level), carrying
            // the package's requirement list as the import writes it.
            NodeType = "Space",
            Name = "A package root nobody has opened",
            State = MeshNodeState.Active,
            // `description` is a member Space declares, so the content binds; `requires` rides beside
            // it exactly as on a package root.
            Content = JsonSerializer.SerializeToElement(new
            {
                description = "a package root",
                requires = new[] { "AI@^1.40.0" },
            }),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        Mesh.GetHostedHub(address, HostedHubCreation.Never).Should().BeNull(
            "the precondition: creating the root does not activate its hub");
        loaded.Set("AI", "MeshWeaver.AI", "1.39.8");

        var unmet = await Settled(Reader.Unmet($"{root}/Kind"), ct);
        unmet.Should().NotBeNull("the requirement is read from the persisted root and judged");
        unmet!.Should().Contain("AI@^1.40.0").And.Contain("1.39.8");

        await built.Should().NotEmit(TestTimeouts.Quick / 4,
            "the floor read must not activate the root: for a self-typed root that activation is the "
            + "very one waiting for this read",
            ct);
        Mesh.GetHostedHub(address, HostedHubCreation.Never).Should().BeNull(
            "…and the registry the signal is fed from agrees");
    }

    /// <summary>
    /// 🚨 The floor's module reading is taken off the landing service's maintained snapshot, so
    /// repeated readings share ONE derivation of the activation list. It sits on every hydration,
    /// and the authoritative walk of the volume measured 7–9 s per call on the fleet's share — paid
    /// per activation, and N times over for N concurrent ones.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheFloorsModuleReading_SharesOneDerivationOfTheActivationList()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "mw-floor-served-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var derivations = 0;
        try
        {
            using var landing = new ModuleLandingService(baseDirectory: directory)
            {
                BeforeServedPublish = _ => Interlocked.Increment(ref derivations),
            };
            var reading = new ServedLoadedPackageModuleReader(landing);
            for (var i = 0; i < 3; i++)
                await reading.Read().Should().Within(TestTimeouts.Convergence)
                    .Emit($"reading {i + 1} of the loaded modules answers", ct);

            Volatile.Read(ref derivations).Should().Be(1,
                "three readings inside the freshness bound share one derivation — a walk of the volume per "
                + "reading is what put seconds in front of every hydration");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Hosting/Deployment", "Hosting")]
    [InlineData("/Hosting/Deployment/", "Hosting")]
    [InlineData("Hosting", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ThePackageRoot_IsTheFirstSegment(string? path, string? root)
        => NodeTypeDependencyFloorReader.PackageRootOf(path).Should().Be(root);

    [Fact]
    public void Requires_IsReadFromTypedAndUntypedContent()
    {
        var options = Mesh.JsonSerializerOptions;
        NodeTypeDependencyFloorReader.RequiresOf(
                new MeshNode(Package) { Content = PluginRootContent(" AI@^1.40.0 ", "") }, options)
            .Should().Equal(["AI@^1.40.0"]);
        NodeTypeDependencyFloorReader.RequiresOf(
                new MeshNode(Package) { Content = new TypedRoot(["Store@^1.35.0"]) }, options)
            .Should().Equal(["Store@^1.35.0"]);
        NodeTypeDependencyFloorReader.RequiresOf(new MeshNode(Package), options).Should().BeEmpty();
        NodeTypeDependencyFloorReader.RequiresOf(null, options).Should().BeEmpty();
    }

    private sealed record TypedRoot(ImmutableList<string> Requires);

    /// <summary>The loaded-module reading, set by the test.</summary>
    private sealed class FakeLoadedModules : ILoadedPackageModules
    {
        private readonly BehaviorSubject<ImmutableDictionary<string, LoadedPackageModule>> current =
            new(ImmutableDictionary<string, LoadedPackageModule>.Empty);

        public void Set(string package, string module, string version)
            => current.OnNext(current.Value.SetItem(package, new LoadedPackageModule(module, version)));

        public void Clear() => current.OnNext(ImmutableDictionary<string, LoadedPackageModule>.Empty);

        public IObservable<ImmutableDictionary<string, LoadedPackageModule>> Read() => current.Take(1);
    }
}
