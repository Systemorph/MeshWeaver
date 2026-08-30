#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// 🚨 <b>#2417 — "a local install can never land a module binary: 26 of 28 module packages record
/// as installed but no binary lands".</b>
///
/// <para>The install orchestrator's content short-circuit compares the install record's
/// <c>ModuleVersion</c> — a hash of the package's <c>manifest.lock</c> — against the one the source
/// serves, and returns <c>InstallResult(0,0)</c> when they match. That is a correct and valuable
/// CONTENT optimisation: no node needs to travel.</para>
///
/// <para>What made it self-sealing is that the early return was the ONE exit of that method not
/// wrapped in <c>WithModule</c>. So the content answer stood in for the module answer, and once a
/// <c>moduleVersion</c> was stamped, no install and no reconcile would ever ask about the binary
/// again — on any deployment, not only a local one. A recreated volume, a half-completed landing,
/// or a package installed while no bundle source was configured were all permanent and silent.</para>
///
/// <para>This test asserts the DELIVERY — that the module lane was actually ENTERED — rather than
/// the <c>InstallResult</c>, which reports <c>(0,0)</c> either way and is precisely the value that
/// was already lying.</para>
/// </summary>
public class ModuleLaneAfterContentUpToDateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly LogSink Sink = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
            .ConfigureServices(services =>
                services.AddSingleton<ILoggerProvider>(new SinkProvider(Sink)));

    private const string ModuleHash = "aaaaaaaaaaaaaaa1";
    private const string ModuleName = "Acme.Gizmo";

    private static readonly IReadOnlyList<PackageFile> Files =
    [
        new("Gizmo/index.json",
            """{"$type":"MeshNode","id":"Gizmo","namespace":"","path":"Gizmo","mainNode":"Gizmo","name":"Gizmo Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A gizmo plugin.","minMeshVersion":"1.0.0"}}"""),
        new("Gizmo/Notes.md", "# Notes"),
        new("Gizmo/manifest.lock",
            $$$"""{"schema":"mw-manifest/1","module":"Gizmo","moduleVersion":"{{{ModuleHash}}}","sourceCommit":"c1","files":{"Gizmo/index.json":"h-root-1","Gizmo/Notes.md":"h-notes-1"}}"""),
    ];

    /// <summary>The package DECLARES a compiled module — the whole point; a package without one has
    /// no module lane to skip.</summary>
    private static PackageManifest Pkg() => new()
    {
        Id = "Gizmo",
        Name = "Gizmo Plugin",
        Kind = PackageKind.NodeRepo,
        TargetPartition = "Gizmo",
        SourceFolder = "Gizmo",
        Version = "commit-1",
        ModuleVersion = ModuleHash,
        Module = ModuleName,
    };

    private sealed class LogSink
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public void Add(string category, string message) => _lines.Enqueue($"{category}|{message}");
        public IReadOnlyList<string> Lines => _lines.ToArray();
        public IReadOnlyList<string> From(string category) =>
            _lines.Where(l => l.StartsWith(category + "|", StringComparison.Ordinal)).ToArray();
    }

    private sealed class SinkProvider(LogSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new SinkLogger(sink, categoryName);
        public void Dispose() { }

        private sealed class SinkLogger(LogSink sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Add(category, formatter(state, exception));
        }
    }

    /// <summary>
    /// A source that serves the files (so the first install works) and carries a bundle client (so
    /// the module lane is reachable). The client points at a closed local port: this test asks
    /// only whether the lane was ENTERED, and every failure past that point is absorbed by
    /// <c>AdoptModule</c> into a logged zero by design.
    /// </summary>
    private sealed class RecordingSource(IReadOnlyList<PackageFile> files) : IPackageSource
    {
        public readonly List<IReadOnlyCollection<string>?> Fetches = [];

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
        {
            Fetches.Add(null);
            return Observable.Return(files);
        }

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths)
        {
            Fetches.Add(paths);
            var wanted = paths is null ? null : new HashSet<string>(paths, StringComparer.Ordinal);
            return Observable.Return<IReadOnlyList<PackageFile>>(
                wanted is null ? files : files.Where(f => wanted.Contains(f.RelativePath)).ToList());
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task ContentUpToDate_StillAsksTheModuleLane()
    {
        // ── The install that stamps the record. Any source shape does; what matters is that the
        //    record ends up carrying this moduleVersion.
        await PackageInstaller.Install(Mesh, Pkg(), Files, "commit-1").FirstAsync().Await();

        // ── Now install again, from a REGISTRY source that can serve bundles, at the SAME content
        //    version. Pre-fix this returns (0,0) without a single module question being asked.
        var registry = new RegistryPackageSource(Mesh, "http://127.0.0.1:1")
        {
            Bundles = new PluginBundleClient(Mesh, "http://127.0.0.1:1"),
        };
        var source = new RecordingSource(Files);

        var result = await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, registry, "commit-1", Pkg(), logger: null)
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        result.Written.Should().Be(0, "the CONTENT is up to date — nothing should be re-fetched");
        source.Fetches.Should().BeEmpty("the content short-circuit must survive this change intact");

        // 🚨 The delivery: PluginBundleClient was reached at all. WHAT it then decided is not this
        // test's business (there is no registry behind that port, so it will report a miss and
        // absorb it into a logged zero, exactly as designed) — that the question was ASKED is.
        var bundleLines = Sink.From("MeshWeaver.PluginCatalog.PluginBundleClient");
        bundleLines.Should().NotBeEmpty(
            "an up-to-date CONTENT hash says nothing about whether the module's binary is on disk; "
            + "letting it stand in for the module answer is what made a missing binary permanent");
        bundleLines.Should().Contain(l => l.Contains(ModuleName, StringComparison.Ordinal));
    }
}
