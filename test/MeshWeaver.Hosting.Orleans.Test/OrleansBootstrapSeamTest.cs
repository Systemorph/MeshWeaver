using System;
using System.IO;
using System.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The seam that replaced the second Orleans base class: a suite DECLARES which mesh it wants
/// (<c>MeshBootstrap.Orleans(…)</c>) instead of picking a base class that hard-codes one.
///
/// <para>🚨 Every case here is about a failure that is otherwise SILENT or late. The applicator is a
/// static hook armed by a module initializer — if that arming is ever removed, nothing fails to
/// compile and nothing fails to boot; a suite simply starts throwing "No Orleans applicator is
/// registered" at run time, in whichever test happens to touch it first. And a pool that hands a
/// suite a cluster built by someone ELSE's silo configurator produces a missing-registration error
/// two files from the cause.</para>
/// </summary>
public class OrleansBootstrapSeamTest(ITestOutputHelper output)
{
    /// <summary>
    /// The module initializer in this assembly's rig arms <c>OrleansBootstrap.Applicator</c>. It is
    /// the ONLY thing that makes <c>MeshBootstrap.Orleans(…)</c> mean anything, and it is invisible
    /// to every compiler check — hence a test.
    /// </summary>
    [Fact]
    public void TheOrleansApplicator_IsArmedByLoadingThisAssembly()
    {
        OrleansBootstrap.Applicator.Should().NotBeNull(
            "MeshWeaver.Hosting.Orleans.TestBase arms it from a [ModuleInitializer] — that hook is "
            + "what keeps the Monolith test base free of any Orleans reference while still letting "
            + "a suite ask for an Orleans mesh through the same IMeshBootstrap seam");
    }

    /// <summary>The supported shape APPLIES, rather than throwing the not-registered message.</summary>
    [Fact]
    public void TheDefaultCluster_AppliesToAMeshBuilder()
    {
        var services = new ServiceCollection();
        var builder = new MeshBuilder(c => c.Invoke(services), new Address("mesh", "seam"));

        var applied = MeshBootstrap.Orleans().Bootstrap(builder);

        applied.Should().NotBeNull();
        // The mesh-level half of an Orleans bootstrap is the partitioned in-memory persistence the
        // silo configurators install; the CLUSTER half is topology and has no MeshBuilder to land on.
        services.Should().NotBeEmpty("applying the bootstrap must actually register something");
    }

    /// <summary>
    /// A cluster this rig cannot stand up is refused HERE, naming the provider — not minutes later
    /// inside a silo, with a message about a connection string and nothing about the test.
    /// </summary>
    [Theory]
    [InlineData(ClusterProvider.AdoNet, StorageProvider.Memory)]
    [InlineData(ClusterProvider.Redis, StorageProvider.Memory)]
    [InlineData(ClusterProvider.Localhost, StorageProvider.AdoNet)]
    [InlineData(ClusterProvider.Localhost, StorageProvider.Redis)]
    public void AnExternalBackend_IsRefusedByName(ClusterProvider clustering, StorageProvider storage)
    {
        var bootstrap = MeshBootstrap.Orleans(o => o
            .WithClustering(clustering, clustering == ClusterProvider.Localhost ? null : "Server=nowhere")
            .WithGrainStorage(storage, storage == StorageProvider.Memory ? null : "localhost:6379"));

        var services = new ServiceCollection();
        var builder = new MeshBuilder(c => c.Invoke(services), new Address("mesh", "seam"));

        var ex = Assert.Throws<NotSupportedException>(() => bootstrap.Bootstrap(builder));
        output.WriteLine(ex.Message);
        ex.Message.Should().Contain(clustering.ToString().ToLowerInvariant())
            .And.Contain(storage.ToString().ToLowerInvariant(),
                "the refusal must name the shape that was asked for, or it explains nothing");
    }

    /// <summary>
    /// 🚨 The regression that motivated <see cref="OrleansClusterShape"/>. The pool used to key on
    /// the fixture TYPE, which was sound only while "a different cluster" and "a different fixture
    /// subclass" were the same statement. Once a suite can DESCRIBE its cluster, two suites share a
    /// fixture type and mean different silos — and a lease across them hands one suite a cluster
    /// built by the other's configurator.
    /// </summary>
    [Fact]
    public void TwoClustersThatDifferInAnyWay_AreNotInterchangeable()
    {
        var stock = new OrleansClusterShape(
            typeof(SharedSiloConfigurator), typeof(TestClientConfigurator), 1, true);

        stock.Should().Be(new OrleansClusterShape(
            typeof(SharedSiloConfigurator), typeof(TestClientConfigurator), 1, true));

        stock.Should().NotBe(stock with { SiloConfigurator = typeof(TestSiloConfigurator) });
        stock.Should().NotBe(stock with { Silos = 2 });
        stock.Should().NotBe(stock with { ShareSiloStore = false });
        stock.Should().NotBe(stock with { FixtureType = typeof(TwoSiloCacheUpdateFixture) });
    }

    /// <summary>
    /// A suite on the Orleans base whose <c>Bootstrap</c> is a MONOLITH one is a statement that
    /// cannot be honoured. It is refused by name, at the point the shape is read — the alternative
    /// is an <c>InvalidCastException</c> with no mention of either class.
    /// </summary>
    [Fact]
    public void AMonolithBootstrapOnTheOrleansBase_IsRefusedByName()
    {
        var suite = new WronglyBootstrappedSuite(output);

        var ex = Assert.Throws<InvalidOperationException>(() => suite.ReadShape());
        output.WriteLine(ex.Message);
        ex.Message.Should().Contain(nameof(WronglyBootstrappedSuite))
            .And.Contain("MeshBootstrap.Orleans")
            .And.Contain(nameof(MonolithMeshTestBase));
    }

    /// <summary>
    /// 🚨 The ratchet on the bridge. <c>OrleansTestBaseCompat.cs</c> exists ONLY so
    /// MeshWeaver.Plugins keeps building until its own suites convert; the moment a suite in THIS
    /// repo derives from one of the three retired names again, the bridge stops being deletable and
    /// the second base class quietly comes back. Nothing tells you that — the code compiles and the
    /// tests pass — which is exactly the shape a guard catches and review does not.
    /// </summary>
    [Fact]
    public void NoSuiteInThisRepo_DerivesFromTheRetiredBases()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MeshWeaver.slnx")))
            root = root.Parent;
        root.Should().NotBeNull("the guard must find the repo root, or it checks nothing");

        var bridge = Path.Combine(
            root!.FullName, "test", "MeshWeaver.Hosting.Orleans.TestBase", "OrleansTestBaseCompat.cs");
        File.Exists(bridge).Should().BeTrue(
            "this guard is about that file; if it is gone the guard is stale and must go with it");

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root.FullName, "test"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // The bridge itself DECLARES them, and this guard NAMES them in its own message. Two
            // files, both listed explicitly — never a substring rule, which is how a scanner starts
            // excusing the very code it exists to catch.
            .Where(f => !string.Equals(f, bridge, StringComparison.Ordinal)
                        && Path.GetFileName(f) != $"{nameof(OrleansBootstrapSeamTest)}.cs")
            .Where(f => File.ReadAllText(f) is var text
                        && (text.Contains(": OrleansTestBase", StringComparison.Ordinal)
                            || text.Contains(": OrleansSharedTestBase", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        output.WriteLine($"bridge derivations in this repo: {offenders.Length}");
        offenders.Should().BeEmpty(
            "OrleansMeshTestBase is the one Orleans base; OrleansTestBaseCompat.cs is a bridge kept "
            + "only until MeshWeaver.Plugins' suites convert, and a new derivation here makes it "
            + "undeletable. Derive from OrleansMeshTestBase and set SiloConfiguratorType instead. "
            + "Offending file(s): " + string.Join(", ", offenders));
    }

    /// <summary>Never initialized — it exists only so the refusal above has something to refuse.</summary>
    private sealed class WronglyBootstrappedSuite(ITestOutputHelper output) : OrleansMeshTestBase(output)
    {
        protected override IMeshBootstrap Bootstrap => MeshBootstrap.Monolith();

        public OrleansClusterShape ReadShape() => ClusterShape;
    }
}
