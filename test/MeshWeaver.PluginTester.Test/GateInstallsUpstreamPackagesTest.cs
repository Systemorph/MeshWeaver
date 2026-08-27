using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// <b>The gate INSTALLS its upstreams — the satellite shape, pinned end to end.</b>
///
/// <para>Repo 2 ships a package whose nodes are TYPED by repo 1's NodeType, and repo 1 reaches the
/// gate only as a bundle in the seed directory (exactly how the reusable gate stages it: the
/// fetched upstream publication's zips are copied beside the repo's own bake). Before
/// <see cref="SeedPackages"/> the seed's assemblies were adopted but the upstream's NODE
/// DEFINITIONS never entered the mesh, so the satellite died at install — measured 2026-08-27 on
/// the first run that ever got this far (Reinsurance run 33092019158): <c>Install of
/// 'RiskTransfer' failed: NodeType(s) not registered: Edu/Lesson, Edu/Exercise, …</c>.</para>
///
/// <para>Three claims, each of which failed or was unguarded before: the upstream is materialized
/// and INSTALLED (so the satellite's typed nodes install green); the upstream is NOT gated here
/// (its compile/render/Tests verdicts belong to its own repo); and the upstream's bundle is NOT
/// re-emitted from this gate's bake output (consumed, never republished under another repo's
/// name — the #1814 identity class).</para>
/// </summary>
public class GateInstallsUpstreamPackagesTest(ITestOutputHelper output)
{
    // ── Repo 1: the upstream. Same Base/Widget shape as BundleCarriesTheUpstreamTest. ──
    private const string BaseIndexJson =
        """{"$type":"MeshNode","id":"Base","namespace":"","path":"Base","mainNode":"Base","name":"Base Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"The upstream package."}}""";

    private const string BaseTypeJson =
        """{"$type":"MeshNode","id":"Widget","namespace":"Base","path":"Base/Widget","mainNode":"Base/Widget","name":"Widget","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A widget.","configuration":"config => config.WithContentType<Widget>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    private const string BaseSource =
        """
        public record Widget
        {
            public string Name { get; init; } = string.Empty;
        }
        """;

    // ── Repo 2: the satellite. Its content is TYPED by the upstream's NodeType. ──
    private const string CourseIndexJson =
        """{"$type":"MeshNode","id":"Course","namespace":"","path":"Course","mainNode":"Course","name":"Course Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"The satellite package."}}""";

    private const string CourseLessonJson =
        """{"$type":"MeshNode","id":"Lesson1","namespace":"Course","path":"Course/Lesson1","mainNode":"Course/Lesson1","name":"Lesson 1","nodeType":"Base/Widget","state":"Active","content":{"$type":"Widget","name":"lesson one"}}""";

    // The satellite's OWN NodeType — gated here (and baked into the gate's own output), unlike
    // the upstream's. Its presence pins that satellite compiles ride beside upstream typing.
    private const string CourseCardJson =
        """{"$type":"MeshNode","id":"Card","namespace":"Course","path":"Course/Card","mainNode":"Course/Card","name":"Card","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A card.","configuration":"config => config.WithContentType<Card>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    private const string CourseCardSource =
        """
        public record Card
        {
            public string Title { get; init; } = string.Empty;
        }
        """;

    [Fact(Timeout = 900_000)]
    public async Task TheSatelliteInstalls_BecauseTheGateInstalledItsUpstream()
    {
        var repo1 = TempDirectory("mw-up-src");
        var repo2 = TempDirectory("mw-up-satellite");
        var seedDir = TempDirectory("mw-up-seed");
        var gateBake = TempDirectory("mw-up-gate-bake");
        try
        {
            Write(repo1, "Base/index.json", BaseIndexJson);
            Write(repo1, "Base/Widget.json", BaseTypeJson);
            Write(repo1, "Base/Widget/Source/Widget.cs", BaseSource);
            Write(repo2, "Course/index.json", CourseIndexJson);
            Write(repo2, "Course/Lesson1.json", CourseLessonJson);
            Write(repo2, "Course/Card.json", CourseCardJson);
            Write(repo2, "Course/Card/Source/Card.cs", CourseCardSource);

            // ── Repo 1 BAKES (no mesh) and its bundle lands in the seed directory… ──
            var bake1 = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo1, OutputDirectory = seedDir, SourceSha = "cafebabe",
                Output = TextWriter.Null,
            });
            Assert.Null(bake1.FatalError);
            Assert.All(bake1.Types, t => Assert.Null(t.Error));

            // ── …beside repo 2's OWN bake — exactly how the reusable gate stages the two. ──
            var bake2 = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo2, OutputDirectory = seedDir, SourceSha = "cafebabe",
                Output = TextWriter.Null,
            });
            Assert.Null(bake2.FatalError);

            var (seed, problem) = BakeSeed.Read(seedDir, PrebuiltAssemblySeeder.LiveFrameworkMvid);
            Assert.Null(problem);

            // ── The GATE on repo 2 alone. Repo 1 exists only as a bundle in the seed. ──
            var log = new StringWriter();
            var report = await PluginGateRunner.Run(new GateOptions
            {
                RepoRoot = repo2,
                Output = log,
                Seed = seed,
                BakeOutputDirectory = gateBake,
                SourceSha = "cafebabe",
                CompileTimeout = TimeSpan.FromMinutes(4),
                RenderTimeout = TimeSpan.FromMinutes(2),
            }).FirstAsync().ToTask();
            output.WriteLine(log.ToString());
            report.WriteSummary(new StringWriterAdapter(output));

            Assert.Null(report.FatalError);

            // 1. The upstream was INSTALLED — and marked as such, with none of its types gated.
            var upstream = report.Packages.Single(p => p.Id == "Base");
            Assert.True(upstream.Upstream, "the seed-borne package must be marked upstream");
            Assert.Null(upstream.InstallError);
            Assert.Empty(upstream.NodeTypes);

            // 2. The satellite installed GREEN — its Base/Widget-typed node registered. This is
            //    the line that read "NodeType(s) not registered: …" before SeedPackages existed.
            var satellite = report.Packages.Single(p => p.Id == "Course");
            Assert.False(satellite.Upstream);
            Assert.Null(satellite.InstallError);
            Assert.Equal(0, report.ExitCode);

            // 3. Consumed, never re-emitted: the gate's own bake output carries the satellite's
            //    bundle and NOT the upstream's.
            var emitted = Directory.EnumerateFiles(gateBake, "*.zip")
                .Select(Path.GetFileName).ToList();
            Assert.DoesNotContain("Base.zip", emitted);
            Assert.Contains("Course.zip", emitted);
        }
        finally
        {
            Cleanup(repo1);
            Cleanup(repo2);
            Cleanup(seedDir);
            Cleanup(gateBake);
        }
    }

    private sealed class StringWriterAdapter(ITestOutputHelper output) : StringWriter
    {
        public override void WriteLine(string? value) => output.WriteLine(value ?? string.Empty);
    }

    private static string TempDirectory(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { /* best effort */ }
    }
}
