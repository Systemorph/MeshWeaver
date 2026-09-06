using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Systemorph/MeshWeaver#3429 — <b>install-time prebuilt adoption for a package installed AFTER
/// BOOT, and the silence that made its failure unreadable.</b>
///
/// <para><b>What was measured.</b> MeshWeaver.Education's disposable-mesh e2e mounted 40 bundles at
/// <c>/bundles</c> — including <c>Edu.zip</c>, which the bake had adopted 10/10 under the very
/// framework identity the harness was running. <c>Store</c> installed and adopted 17 assemblies.
/// Three minutes later <c>Edu</c> installed 99 nodes, adopted NOTHING, and its ten NodeTypes went to
/// Roslyn on a mesh that was standing on their bytes. The log carried no adoption line, no decline
/// line, and no reason: a completed seed that covered zero said exactly as much as a deployment with
/// no bundles at all. That is the defect — not "it did not adopt", but that <b>a failed adoption and
/// a successful one were indistinguishable</b>.</para>
///
/// <para><b>Why the boot lane cannot cover this.</b> A package installed after boot is the normal
/// case for every consumer of a registry: the boot sweep enumerates the NodeTypes the mesh HOLDS,
/// and a type that arrives later was not in that snapshot. Install-time adoption
/// (<c>PackageInstaller.SeedPrebuiltAssemblies</c> → <c>IPrebuiltAssemblyConsumer.SeedForTypes</c>)
/// is the only lane that can serve it — so a silent zero there is a whole capability failing
/// invisibly.</para>
///
/// <para><b>The two arms.</b> <see cref="AfterBoot_APackageWhoseBundleIsMounted_AdoptsIt"/> is the
/// positive control: without it the silence arm would pass just as well against a harness in which
/// adoption cannot happen at all. <see cref="AfterBoot_APackageNoBundleCovers_SaysWhy"/> is the
/// defect: the same install, the same mount, a bundle naming somebody else's types — and the log
/// must now name the package, the uncovered type, and the reason.</para>
/// </summary>
public class InstallTimePrebuiltAdoptionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "PrebuiltPkg";
    private const string CoveredType = $"{Package}/Widget";

    /// <summary>Where this test class's bundles are mounted — the harness's <c>/bundles</c>.
    /// Per-instance (each <c>[Fact]</c> builds its own mesh), so the two arms cannot see each
    /// other's bundles.</summary>
    private readonly string _bundleDirectory = Path.Combine(
        Path.GetTempPath(), "mw3429-" + Guid.NewGuid().ToString("N"));

    /// <summary>Captures what the SEEDER said. The install's own line goes to the logger handed to
    /// <c>PackageInstaller.Install</c>; the reason line comes from <c>ShippedPrebuiltBundles</c>
    /// through the mesh's logger factory, and the whole point of #3429 is that BOTH have to be
    /// there.</summary>
    private readonly LogSink _meshLog = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(s => s
                // The deployment's bundle mount, exactly as an image or a volume declares it.
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Ai:KeyProtection:MasterKey"] = "test-master-key-do-not-use-outside-tests",
                    })
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [ShippedPrebuiltBundles.DirectoryConfigKey] = _bundleDirectory,
                    })
                    .Build())
                // 🚨 The production registration, and the narrow one on purpose: until #3429 the
                // ONLY way to get an IPrebuiltAssemblyConsumer was AddDynamicTypePreWarming, which
                // also adds a hosted service requiring IHostApplicationLifetime — so a composition
                // without a generic host had no consumer and adopted nothing, silently.
                .AddPrebuiltAssemblyConsumption()
                // 🚨 The provider-scoped filter is load-bearing, not tidiness: test/appsettings.json
                // pins every MeshWeaver category at Warning, and the seeder's reason line is
                // Information (a mesh holding more types than the bake covers is a fact about the
                // deployment, not a fault — the install's own line above it carries the Warning).
                // Without this the assertion would measure the harness's log level and pass or fail
                // for a reason that has nothing to do with the product.
                .AddLogging(l => l
                    .AddProvider(_meshLog)
                    .AddFilter<LogSink>(category: null, level: LogLevel.Trace)));

    /// <summary>Real PE bytes with a real MVID. The seeder stamps
    /// <c>NodeTypeDefinition.LatestAssemblyMvid</c> from the bytes it is handed, so this identity is
    /// the ONE positive signal that an adoption LANDED — a Roslyn compile of this package's (empty)
    /// source set can never produce it.</summary>
    private static byte[] BundleBytes() =>
        File.ReadAllBytes(typeof(BundleWriter).Assembly.Location);

    private void MountBundle(string fileName, params string[] nodePaths)
    {
        Directory.CreateDirectory(_bundleDirectory);
        using var file = File.Create(Path.Combine(_bundleDirectory, fileName));
        BundleWriter.Write(
            file, Package, "1.0.0", PrebuiltAssemblySeeder.LiveFrameworkMvid,
            nodePaths
                .Select(p => new BundleWriter.AssemblyEntry(p, () => new MemoryStream(BundleBytes())))
                .ToArray());
    }

    /// <summary>The package's NodeType, authored as a repo authors one — <c>nodeType: NodeType</c>
    /// with an untyped content object, which is what the polymorphic converter hands the installer
    /// for content carrying no <c>$type</c>.</summary>
    private const string TypeNodeJson = """
        {
          "id": "Widget",
          "namespace": "PrebuiltPkg",
          "path": "PrebuiltPkg/Widget",
          "nodeType": "NodeType",
          "name": "Widget",
          "state": "Active",
          "content": { "configuration": "config => config" }
        }
        """;

    private Task<InstallResult> InstallAfterBoot(ILogger logger) =>
        PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = Package,
                    Name = Package,
                    Kind = PackageKind.NodeRepo,
                    TargetPartition = Package,
                    SourceFolder = Package,
                    Version = "1.0.0",
                },
                [
                    new PackageFile($"{Package}.md", $"# {Package}"),
                    new PackageFile($"{CoveredType}.json", TypeNodeJson),
                ],
                "HEAD",
                logger)
            .Should().Within(180.Seconds())
            .Emit("the install itself must complete before anything about adoption can be read");

    /// <summary>
    /// THE POSITIVE CONTROL — a package installed after boot whose baked assembly is mounted is
    /// adopted, and the install SAYS so. Without this arm the silence arm below would pass against
    /// a harness in which adoption is simply unreachable, which is the shape of a gate that cannot
    /// fail.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AfterBoot_APackageWhoseBundleIsMounted_AdoptsIt()
    {
        try
        {
            MountBundle($"{Package}.zip", CoveredType);
            var expectedMvid = ServedBuildIdentity.OfBytes(BundleBytes());
            expectedMvid.Should().NotBeNullOrEmpty("the mounted bundle carries a real PE image");

            var install = new LogSink();
            var result = await InstallAfterBoot(install.Logger);
            result.Written.Should().BeGreaterThan(0, "the install must have written its nodes");

            await Mesh.GetWorkspace().GetMeshNodeStream(CoveredType)
                .Should().Within(120.Seconds())
                .Match(
                    n => string.Equals(
                        n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.LatestAssemblyMvid,
                        expectedMvid, StringComparison.Ordinal),
                    "the bytes for this type were mounted when the package installed — install-time "
                    + "adoption is the ONLY lane that can serve a package that arrives after boot");

            install.Dump(Output, "INSTALL");
            install.Lines.Should().Contain(
                l => l.Contains($"Install: {Package}: adopted 1 prebuilt", StringComparison.Ordinal),
                "an adoption that happened must be reported, naming the package");
        }
        finally
        {
            RemoveMount();
        }
    }

    /// <summary>
    /// THE DEFECT — the same install, the same mount, and no bundle covering these types. Before
    /// #3429 this completed in silence: no adoption line, no reason, nothing to grep, and the ten
    /// Education types compiled. It must now name the package, the uncovered type and the reason.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AfterBoot_APackageNoBundleCovers_SaysWhy()
    {
        try
        {
            // A real, well-formed, identity-matching bundle — for somebody else's types. This is
            // exactly Edu's shape as measured: bundles mounted, none of them a candidate.
            MountBundle("Other.zip", "SomeOtherPackage/Thing");

            var install = new LogSink();
            await InstallAfterBoot(install.Logger);

            install.Dump(Output, "INSTALL");
            _meshLog.Dump(Output, "MESH");

            install.Lines.Should().Contain(
                l => l.StartsWith("Warning: ", StringComparison.Ordinal)
                     && l.Contains($"Install: {Package}: adopted NO prebuilt assembly", StringComparison.Ordinal),
                "the install lane must say, loudly and naming the PACKAGE, that it adopted nothing — "
                + "a completed seed that covered zero used to log exactly as much as a deployment "
                + "with no bundles at all");

            _meshLog.Lines.Should().Contain(
                l => l.Contains("adopted no prebuilt assembly for 1 of 1 requested", StringComparison.Ordinal)
                     && l.Contains(CoveredType, StringComparison.Ordinal),
                "the seeder must name the UNCOVERED type paths and count them against what was "
                + "REQUESTED — 'nothing adopted' without them cannot be acted on, and a denominator "
                + "derived from per-entry sums would double-count a type two sources both carry");

            _meshLog.Lines.Should().Contain(
                l => l.Contains("name only NodeTypes OUTSIDE this set", StringComparison.Ordinal),
                "…and the REASON, which is the half #3429 asks for: a bundle source WAS mounted and "
                + "read, and none of its entries named a requested type. That reads differently from "
                + "'no bundle source exists' and from 'a bundle named it and it did not land', and "
                + "the three must never look alike again");
        }
        finally
        {
            RemoveMount();
        }
    }

    /// <summary>Removes the mounted bundle directory. Best-effort in a <c>finally</c>, matching this
    /// suite's own convention (PrebuiltPublicationTest): a failing assertion must not be replaced by
    /// a cleanup exception, and a temp root left behind on one run would accumulate across CI.</summary>
    private void RemoveMount()
    {
        try { Directory.Delete(_bundleDirectory, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>An <see cref="ILoggerProvider"/> that keeps every line, so a test can assert on what
    /// the product SAID. Not a mock of anything the framework owns — the logging abstraction is the
    /// product's own diagnostic surface, and this issue is entirely about that surface.</summary>
    private sealed class LogSink : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        /// <summary>A logger to hand to an API that takes one directly.</summary>
        public ILogger Logger => new SinkLogger(_lines);

        public ILogger CreateLogger(string categoryName) => new SinkLogger(_lines);

        public void Dispose() { }

        public void Dump(ITestOutputHelper output, string tag)
        {
            foreach (var line in _lines)
                output.WriteLine($"{tag} {line}");
        }

        private sealed class SinkLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
                => lines.Enqueue($"{level}: {formatter(state, ex)}");
        }

        private sealed class NoScope : IDisposable
        {
            public static readonly NoScope Instance = new();
            public void Dispose() { }
        }
    }
}
