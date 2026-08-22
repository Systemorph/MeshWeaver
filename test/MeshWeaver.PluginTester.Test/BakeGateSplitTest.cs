#pragma warning disable CS1591

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
/// 🚨 THE SPLIT (#1763), end to end: a bake that stands up NO mesh produces the assemblies, and a
/// gate that stands one up CONSUMES them — rendering and executing <c>Tests</c> areas on the very
/// bytes that ship rather than on a private recompile of the same sources.
///
/// <para><b>How adoption is proved, and why it cannot be faked.</b> A gate verdict is blind to
/// where the bytes came from: a type the gate compiled itself renders and tests exactly like a
/// type it adopted, which is why the consuming half could silently stop working with every run
/// staying green. So the assertion is on the BYTES. The seeded run is asked to re-emit what its
/// store holds (<c>--bake-output</c>), and every assembly it writes must be byte-identical to the
/// one the compiler-driven bake produced.</para>
///
/// <para><b>The control makes that non-vacuous.</b> The same gate run WITHOUT a seed compiles the
/// same sources, and its assemblies must NOT be byte-identical — dynamic NodeType compilation is
/// not deterministic (Roslyn mints a fresh MVID per emit, and the generated skeleton carries a
/// wall clock), so "the bytes match" can only mean "these bytes were not compiled here". The
/// control runs in the same test, so the day compilation becomes deterministic this test fails
/// loudly instead of quietly proving nothing.</para>
/// </summary>
public class BakeGateSplitTest(ITestOutputHelper output)
{
    private const string WidgetIndexJson =
        """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin."}}""";

    private const string ThingNodeTypeJson =
        """{"$type":"MeshNode","id":"Thing","namespace":"Widget","path":"Widget/Thing","mainNode":"Widget/Thing","name":"Thing","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A thing.","configuration":"config => config.WithContentType<Thing>().AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Tests\", ThingTestsArea.Tests))","includeGlobalTypes":true}}""";

    private const string ThingSource =
        """
        public record Thing
        {
            public string Name { get; init; } = string.Empty;

            public int Answer() => 42;
        }
        """;

    private const string ThingTests =
        """
        public static class ThingTests
        {
            public static void Answer_Is42()
            {
                if (new Thing().Answer() != 42)
                    throw new System.Exception("expected the answer to be 42");
            }
        }
        """;

    private const string ThingTestsArea =
        """
        using System;
        using System.Reactive.Linq;
        using MeshWeaver.Layout;
        using MeshWeaver.Layout.Composition;

        public static class ThingTestsArea
        {
            public static IObservable<UiControl?> Tests(LayoutAreaHost host, RenderingContext _)
            {
                var sb = new System.Text.StringBuilder("### Thing tests\n\n| Test | Result |\n|---|---|\n");
                var passed = 0;
                try
                {
                    ThingTests.Answer_Is42();
                    sb.Append("| Answer is 42 | ✅ pass |\n");
                    passed++;
                }
                catch (Exception ex) { sb.Append($"| Answer is 42 | ❌ {ex.Message} |\n"); }
                sb.Append($"\n**{passed}/1 passed.**");
                return Observable.Return<UiControl?>(Controls.Markdown(sb.ToString()));
            }
        }
        """;

    [Fact(Timeout = 900_000)]
    public async Task TheGateRunsOnTheBakedBytes_AndCompilesNothingItWasHandedABakeFor()
    {
        var repo = CreateRepo();
        var bakeDir = TempDirectory("mw-split-bake");
        var seededOut = TempDirectory("mw-split-seeded");
        var controlOut = TempDirectory("mw-split-control");
        try
        {
            // ── 1. THE BAKE. No mesh: this is a build step. ──
            var bakeLog = new StringWriter();
            var bake = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = bakeDir,
                SourceSha = "deadbeef",
                Output = bakeLog,
            });
            output.WriteLine("── bake (no mesh) ──");
            output.WriteLine(bakeLog.ToString());
            Assert.Null(bake.FatalError);
            Assert.All(bake.Types, t => Assert.Null(t.Error));

            // ── 2. THE SEED is readable and addressed to this process. ──
            var (seed, problem) = BakeSeed.Read(bakeDir, PrebuiltAssemblySeeder.LiveFrameworkMvid);
            Assert.Null(problem);
            Assert.NotNull(seed);
            Assert.Contains("Widget/Thing", seed!.DeclaredTypePaths);

            // ── 3. THE GATE, consuming it. Renders + executes Tests on the BAKED bytes. ──
            var seededLog = new StringWriter();
            var seeded = await PluginGateRunner.Run(new GateOptions
            {
                RepoRoot = repo,
                Output = seededLog,
                Seed = seed,
                BakeOutputDirectory = seededOut,
                SourceSha = "deadbeef",
                CompileTimeout = TimeSpan.FromMinutes(4),
                RenderTimeout = TimeSpan.FromMinutes(2),
            }).FirstAsync().ToTask();
            output.WriteLine("── gate consuming the bake ──");
            output.WriteLine(seededLog.ToString());
            Assert.Null(seeded.FatalError);
            var thing = seeded.Packages.Single(p => p.Id == "Widget").NodeTypes
                .Single(t => t.Path == "Widget/Thing");
            Assert.Equal(CheckOutcome.Passed, thing.Compile);
            Assert.Equal(CheckOutcome.Passed, thing.Render);
            Assert.Equal(CheckOutcome.Passed, thing.Tests);
            Assert.Equal(0, seeded.ExitCode);

            // ── 4. THE CONTROL: the same gate producing its own bytes. ──
            var controlLog = new StringWriter();
            var control = await PluginGateRunner.Run(new GateOptions
            {
                RepoRoot = repo,
                Output = controlLog,
                BakeOutputDirectory = controlOut,
                SourceSha = "deadbeef",
                CompileTimeout = TimeSpan.FromMinutes(4),
                RenderTimeout = TimeSpan.FromMinutes(2),
            }).FirstAsync().ToTask();
            output.WriteLine("── gate compiling for itself (the control) ──");
            output.WriteLine(controlLog.ToString());
            Assert.Null(control.FatalError);

            var baked = AssembliesOf(bakeDir);
            var fromSeededGate = AssembliesOf(seededOut);
            var fromControlGate = AssembliesOf(controlOut);

            Assert.Equal(
                baked.Keys.OrderBy(k => k, StringComparer.Ordinal),
                fromSeededGate.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(
                baked.Keys.OrderBy(k => k, StringComparer.Ordinal),
                fromControlGate.Keys.OrderBy(k => k, StringComparer.Ordinal));

            foreach (var (nodePath, bakedBytes) in baked)
            {
                // THE ASSERTION: the seeded gate served, rendered and tested the bake's own bytes.
                Assert.True(
                    bakedBytes.SequenceEqual(fromSeededGate[nodePath]),
                    $"the seeded gate holds DIFFERENT bytes for {nodePath} than the bake produced "
                    + "— it declined the bundle and compiled the type itself, so the run judged "
                    + "bytes that will never ship");
                // THE CONTROL: without a seed the gate compiles, and a compile is not reproducible
                // (fresh MVID per emit + the generated skeleton's wall clock). If this ever passes,
                // byte-identity has stopped being evidence of adoption and the assertion above is
                // vacuous — see the class remarks.
                Assert.False(
                    bakedBytes.SequenceEqual(fromControlGate[nodePath]),
                    $"an UNSEEDED gate produced byte-identical bytes for {nodePath}. Compilation "
                    + "has become deterministic, which makes the adoption assertion above vacuous "
                    + "— replace it with a stronger discriminator before trusting this test again");
            }
        }
        finally
        {
            Cleanup(repo);
            Cleanup(bakeDir);
            Cleanup(seededOut);
            Cleanup(controlOut);
        }
    }

    /// <summary>
    /// 🚨 A bake the gate CANNOT consume must be refused before the mesh boots — never discovered
    /// never. A gate pointed at a bake keyed to another framework identity declines every assembly
    /// individually, compiles the whole tree itself, and exits GREEN having judged none of the
    /// bytes that ship. Nothing in the verdict distinguishes that from a perfect consumption,
    /// which is why the check has to be a refusal rather than a log line.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void ABakeThisProcessCannotAddress_IsRefusedBeforeAnyMeshIsBuilt()
    {
        var repo = CreateRepo();
        var bakeDir = TempDirectory("mw-split-address");
        try
        {
            var bake = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = bakeDir,
                Output = new StringWriter(),
            });
            Assert.Null(bake.FatalError);

            // As produced: readable and addressed to this process.
            var (usable, noProblem) = BakeSeed.Read(bakeDir, PrebuiltAssemblySeeder.LiveFrameworkMvid);
            Assert.Null(noProblem);
            Assert.NotNull(usable);

            // The SAME directory, read by a host that resolves a different identity — the #1814
            // shape one level down.
            var (foreign, addressProblem) = BakeSeed.Read(bakeDir, "s0000000000000000000000000000dead");
            Assert.Null(foreign);
            Assert.Contains("framework identity", addressProblem);
            output.WriteLine(addressProblem!);

            // A directory with no bundles is a configuration error too: "consumed everything" and
            // "there was nothing to consume" must not render as the same green run.
            var empty = TempDirectory("mw-split-empty");
            Directory.CreateDirectory(empty);
            File.WriteAllText(
                Path.Combine(empty, BakeOutput.FrameworkMvidFile),
                PrebuiltAssemblySeeder.LiveFrameworkMvid);
            var (none, emptyProblem) = BakeSeed.Read(empty, PrebuiltAssemblySeeder.LiveFrameworkMvid);
            Assert.Null(none);
            Assert.Contains("no *.zip bundles", emptyProblem);
            Cleanup(empty);

            // A directory that is not a bake at all.
            var notABake = TempDirectory("mw-split-notabake");
            Directory.CreateDirectory(notABake);
            var (missing, missingProblem) =
                BakeSeed.Read(notABake, PrebuiltAssemblySeeder.LiveFrameworkMvid);
            Assert.Null(missing);
            Assert.Contains(BakeOutput.FrameworkMvidFile, missingProblem);
            Cleanup(notABake);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(bakeDir);
        }
    }

    /// <summary>
    /// 🚨 One type's failure must fail THAT TYPE, never the whole bake.
    ///
    /// <para>The per-type catch used to name exactly two exception types; anything else unwound
    /// past the bundle writer and killed the run — <c>FATAL</c>, exit 70, and zero bundles for the
    /// packages that had compiled perfectly. Measured on <c>samples/Graph/Data</c>: one
    /// <c>FatalProtocolException</c> out of the NuGet resolver (a host-configuration fault on a
    /// single type carrying a <c>#r "nuget:"</c> directive) discarded a bake in which 23 of 24
    /// NodeTypes were already built.</para>
    ///
    /// <para>It is also an EQUIVALENCE break, which is why it belongs in this file: the mesh-driven
    /// producer contains exactly this per type (the type settles at <c>CompilationStatus.Error</c>
    /// and the ratchet decides what that is worth), so a known-debt failure the gate tolerates
    /// would become a total bake failure on the other side.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void OneTypesFailure_FailsThatType_NotTheWholeBake()
    {
        // A source referencing a package from a source that does not exist: the resolver throws a
        // NuGetProtocol fault, which is neither a CompilationException nor a source-discovery one.
        const string brokenNodeType =
            """{"$type":"MeshNode","id":"Broken","namespace":"Widget","path":"Widget/Broken","mainNode":"Widget/Broken","name":"Broken","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"References a package that cannot resolve.","configuration":"config => config.WithContentType<Broken>()","includeGlobalTypes":true}}""";
        const string brokenSource =
            """
            #r "nuget:This.Package.Does.Not.Exist.Anywhere, 999.999.999"

            public record Broken
            {
                public int Answer() => 1;
            }
            """;

        var repo = CreateRepo();
        Write(repo, "Widget/Broken.json", brokenNodeType);
        Write(repo, "Widget/Broken/Source/Broken.cs", brokenSource);
        var bakeDir = TempDirectory("mw-split-contained");
        var log = new StringWriter();
        try
        {
            var report = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = bakeDir,
                Output = log,
            });
            output.WriteLine(log.ToString());

            // The bake completed and produced a VERDICT — not a fatal that discards everything.
            Assert.Null(report.FatalError);
            var broken = Assert.Single(report.Types, t => t.NodePath == "Widget/Broken");
            Assert.False(broken.Success);
            Assert.NotNull(broken.Error);

            // …and the healthy type still shipped, which is the half a fatal would have destroyed.
            var healthy = Assert.Single(report.Types, t => t.NodePath == "Widget/Thing");
            Assert.True(healthy.Success, healthy.Error);
            Assert.Contains("Widget/Thing", AssembliesOf(bakeDir).Keys);
            Assert.NotEqual(0, report.ExitCode);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(bakeDir);
        }
    }

    // ── fixture ──

    private static string CreateRepo()
    {
        var root = TempDirectory("mw-split-fixture");
        Write(root, "Widget/index.json", WidgetIndexJson);
        Write(root, "Widget/Thing.json", ThingNodeTypeJson);
        Write(root, "Widget/Thing/Source/Thing.cs", ThingSource);
        Write(root, "Widget/Thing/Test/ThingTests.cs", ThingTests);
        Write(root, "Widget/Thing/Test/ThingTestsArea.cs", ThingTestsArea);
        return root;
    }

    /// <summary>Every assembly a bake directory carries, by NodeType path.</summary>
    private static Dictionary<string, byte[]> AssembliesOf(string directory)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var zip in Directory.EnumerateFiles(directory, "*.zip")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var (_, payloads) = BundleReader.ReadFile(zip);
            foreach (var payload in payloads)
                result[payload.NodePath] = payload.Assembly;
        }
        return result;
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
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover fixture costs disk, never correctness.
        }
    }
}
