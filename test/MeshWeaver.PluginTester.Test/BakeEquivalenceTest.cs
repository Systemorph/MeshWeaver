#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Plugin.Packaging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 THE SAFETY ARGUMENT for issue #1763. Bakes one content set BOTH ways — through the
/// MESH-driven gate (<see cref="PluginGateRunner"/> with <c>--bake-output</c>: stand up an
/// in-process mesh, import the repo, let the mesh compile every NodeType, collect what it produced)
/// and through the COMPILER-driven build step (<see cref="TreeBake"/>: resolve sources from the
/// tree, compile with <c>MeshWeaver.Compiler</c>, emit) — and asserts the two producers agree.
///
/// <para><b>Why this test is the deliverable and the speed is not.</b> A baker that resolves
/// sources even slightly differently from the runtime emits assemblies that are subtly not what the
/// mesh would have built, and NOTHING fails: the bundle is well-formed, the framework identity
/// matches, every consumer adopts it, and the defect first surfaces as a page rendering empty in
/// production. There is no exception to grep for and no log line to find. So the equivalence
/// between "what the build resolved" and "what the mesh would have resolved" is pinned here, on a
/// content set that exercises every resolution rule that could fork:</para>
/// <list type="bullet">
///   <item>the DEFAULT <c>Source/</c> + <c>Test/</c> subtree queries, including a NESTED folder
///     (<c>namespace:… scope:subtree</c> must reach <c>Source/Sub/Deep.cs</c>);</item>
///   <item>a CROSS-PACKAGE <c>shared=@Lib/Shared/Source</c> query — the one the runtime answers
///     against the whole mesh, and which a per-package tree walk would silently resolve to
///     nothing (#1218's shape);</item>
///   <item>an <c>@@</c> include pulling a Code node that NO source query matches;</item>
///   <item>an EXECUTABLE code cell under <c>Source/</c>, which both paths must EXCLUDE (it runs
///     via the kernel; folding it in collides with the class declarations beside it);</item>
///   <item>a <c>// NodeType: Scope</c>-headered <c>.cs</c>, which the implicit <c>nodeType:Code</c>
///     filter must exclude from both.</item>
/// </list>
///
/// <para><b>What is compared, and what deliberately is not.</b> The bundle SHAPE (node paths), the
/// per-type DEPENDENCY RECORDS (#1719 — what a consumer validates before adopting), the framework
/// identity, the resolved SOURCE SET per type, and the emitted assemblies' TYPE AND MEMBER SURFACE.
/// Not the bytes: the mesh folds its source set through an <c>ImmutableDictionary</c> and emits
/// <c>dict.Values</c>, i.e. hash-bucket order over per-process-randomised string hashes, so the
/// mesh-driven bake is not byte-reproducible against ITSELF, let alone against a deterministic
/// producer. (The generated skeleton also stamps <c>// Generated at: {UtcNow}</c>.) The compiler
/// path sorts, which is strictly better; the concatenation order of independent top-level
/// declarations is what the surface comparison proves irrelevant.</para>
/// </summary>
public class BakeEquivalenceTest(ITestOutputHelper output)
{
    // ── Lib: a package whose NodeType's Source is CONSUMED by the other package ──

    private const string LibIndexJson =
        """{"$type":"MeshNode","id":"Lib","namespace":"","path":"Lib","mainNode":"Lib","name":"Lib","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Shared sources."}}""";

    private const string SharedNodeTypeJson =
        """{"$type":"MeshNode","id":"Shared","namespace":"Lib","path":"Lib/Shared","mainNode":"Lib/Shared","name":"Shared","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"Shared library.","configuration":"config => config.WithContentType<SharedMarker>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    private const string HelperSource =
        """
        public static class Helper
        {
            public static int Answer() => 42;
        }

        public record SharedMarker
        {
            public string Note { get; init; } = string.Empty;
        }
        """;

    // ── Widget: consumes Lib's Source via `shared=`, uses an @@ include, and ships two files the
    //    resolution must EXCLUDE (an executable cell and a Scope-typed .cs) ──

    private const string WidgetIndexJson =
        """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin."}}""";

    // 🚨 `"compilationStatus":"Ok"` is not decoration. Enums persist as STRINGS, and a content
    // reader built on plain web defaults cannot convert them — which silently made 28 NodeTypes
    // across ACME/Cornerstone/FutuRe/Northwind unmaterialisable when this bake was first run over
    // samples/Graph/Data. Every NodeType that has ever compiled carries this stamp, so a fixture
    // without one does not resemble real content at all. If the converter regresses, the tree bake
    // drops Widget/Thing and the bundle-set assertion below goes red.
    /// <summary>
    /// A plain data node written WITH a UTF-8 BOM (#1767). Deliberately not a NodeType: what is
    /// under test is that both producers materialise it identically, not that it compiles.
    /// </summary>
    private const string BommedNodeTypeJson =
        """{"$type":"MeshNode","id":"Bommed","namespace":"Widget","path":"Widget/Bommed","mainNode":"Widget/Bommed","name":"A BOM'd node file","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Written with a UTF-8 BOM; both bakes must see it."}}""";

    private const string ThingNodeTypeJson =
        """{"$type":"MeshNode","id":"Thing","namespace":"Widget","path":"Widget/Thing","mainNode":"Widget/Thing","name":"Thing","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A thing.","configuration":"config => config.WithContentType<Thing>().AddDefaultLayoutAreas()","includeGlobalTypes":true,"compilationStatus":"Ok","lastCompiledVersion":7,"sources":["namespace:Source scope:subtree","shared=@Lib/Shared/Source"]}}""";

    // Pulls Helper (cross-package), Deep (nested subtree) and — through the @@ directive — a Code
    // node no source query matches.
    private const string ThingSource =
        """
        public record Thing
        {
            public string Name { get; init; } = string.Empty;

            public int Answer() => Helper.Answer() + Deep.Bonus();
        }

        @@Widget/Snippets/Greeting
        """;

    private const string DeepSource =
        """
        public static class Deep
        {
            public static int Bonus() => 0;
        }
        """;

    private const string GreetingSource =
        """
        public static class Greeting
        {
            public static string Hello() => "hi";
        }
        """;

    private const string ThingTestsSource =
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

    // 🚨 An executable code cell living UNDER Source/. Both paths must skip it: it runs through the
    // kernel, and its top-level statements would collide with the class declarations beside it.
    // Authored as .json because the `.cs` header cannot carry `isExecutable` (AGENTS.md → node file
    // formats), which is exactly why a tree baker that only understood `.cs` would fold it in.
    private const string ExecutableCellJson =
        """{"$type":"MeshNode","id":"Cell","namespace":"Widget/Thing/Source","path":"Widget/Thing/Source/Cell","mainNode":"Widget/Thing/Source/Cell","name":"Cell","nodeType":"Code","state":"Active","content":{"$type":"CodeConfiguration","code":"var x = 1; x.Display();","language":"csharp","isExecutable":true}}""";

    // A Scope-typed .cs under Source/: the implicit `nodeType:Code` filter must exclude it. If it
    // leaked in, `ScopeOnly` would appear in the emitted surface.
    private const string ScopeTypedSource =
        """
        // <meshweaver>
        // NodeType: Scope
        // </meshweaver>
        public static class ScopeOnly
        {
            public static int Value() => 7;
        }
        """;

    [Fact(Timeout = 600_000)]
    public async Task MeshDrivenAndCompilerDrivenBakes_ProduceTheSameArtifacts()
    {
        var repo = CreateRepo(root =>
        {
            WriteFile(root, "Lib/index.json", LibIndexJson);
            WriteFile(root, "Lib/Shared.json", SharedNodeTypeJson);
            WriteFile(root, "Lib/Shared/Source/Helper.cs", HelperSource);

            WriteFile(root, "Widget/index.json", WidgetIndexJson);
            WriteFile(root, "Widget/Thing.json", ThingNodeTypeJson);
            WriteFile(root, "Widget/Thing/Source/Thing.cs", ThingSource);
            WriteFile(root, "Widget/Thing/Source/Sub/Deep.cs", DeepSource);
            WriteFile(root, "Widget/Thing/Source/Scoped.cs", ScopeTypedSource);
            WriteFile(root, "Widget/Thing/Source/Cell.json", ExecutableCellJson);
            WriteFile(root, "Widget/Thing/Test/ThingTests.cs", ThingTestsSource);
            WriteFile(root, "Widget/Snippets/Greeting.cs", GreetingSource);
            // 🚨 A node file the RUNTIME cannot parse — malformed JSON. The bake must skip it too:
            // neither die on it (it is not a bake failure) nor parse it (that would compile content
            // the mesh never imports). Both producers therefore emit the SAME bundle set, which is
            // what the assertions below check.
            WriteFile(root, "Widget/Unparseable.json", "{ this is not json");
            // 🚨 A BOM'd file is NOT that case any more (#1767). It used to be — PensionFund ships
            // 62 BOM'd .json files and the importer skipped every one, gating zero NodeTypes while
            // reporting success — and this test pinned that skip as the contract. The BOM is now
            // stripped in FileFormatParserRegistry, which both producers share, so the file parses
            // on BOTH sides and equivalence still holds. Keeping it here means a regression that
            // re-broke only one of the two loaders would show up as a bundle-set difference.
            WriteFileWithBom(root, "Widget/Bommed.json", BommedNodeTypeJson);
        });
        var meshDir = Path.Combine(Path.GetTempPath(), "mw-bake-mesh-" + Guid.NewGuid().ToString("N"));
        var treeDir = Path.Combine(Path.GetTempPath(), "mw-bake-tree-" + Guid.NewGuid().ToString("N"));
        var meshLog = new StringWriter();
        var treeLog = new StringWriter();
        try
        {
            // ── the COMPILER-driven bake: no MeshBuilder, no AddGraph, no import, no hub ──
            var treeReport = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = treeDir,
                SourceSha = "deadbeef",
                Output = treeLog,
            });
            output.WriteLine("── compiler-driven bake ──");
            output.WriteLine(treeLog.ToString());
            Assert.Null(treeReport.FatalError);
            Assert.All(treeReport.Types, t => Assert.Null(t.Error));

            // ── the MESH-driven bake: exactly what CI runs today ──
            var meshReport = await PluginGateRunner.Run(new GateOptions
            {
                RepoRoot = repo,
                Output = meshLog,
                BakeOutputDirectory = meshDir,
                SourceSha = "deadbeef",
                CompileTimeout = TimeSpan.FromMinutes(4),
                RenderTimeout = TimeSpan.FromMinutes(2),
            }).FirstAsync().Await();
            output.WriteLine("── mesh-driven bake ──");
            output.WriteLine(meshLog.ToString());
            Assert.Null(meshReport.FatalError);

            var mesh = ReadBakeDirectory(meshDir);
            var tree = ReadBakeDirectory(treeDir);

            // 1. The framework identity — the gate every consumer applies before adopting a byte.
            Assert.Equal(mesh.FrameworkIdentity, tree.FrameworkIdentity);

            // 2. The bundle SET: the same packages produced the same NodeTypes. A short set here is
            //    the whole failure mode — a type the runtime would have built that the build did
            //    not, or the reverse.
            Assert.Equal(
                mesh.Bundles.Keys.OrderBy(k => k, StringComparer.Ordinal),
                tree.Bundles.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (var (bundle, meshEntries) in mesh.Bundles)
            {
                var treeEntries = tree.Bundles[bundle];
                Assert.Equal(
                    meshEntries.Keys.OrderBy(k => k, StringComparer.Ordinal),
                    treeEntries.Keys.OrderBy(k => k, StringComparer.Ordinal));
            }

            foreach (var (bundle, meshEntries) in mesh.Bundles)
            {
                var treeEntries = tree.Bundles[bundle];
                foreach (var (nodePath, meshEntry) in meshEntries)
                {
                    var treeEntry = treeEntries[nodePath];

                    // 3. THE SOURCE SET. The single most valuable assertion in this file: which
                    //    Code nodes each producer decided the compile consumes. Everything else is
                    //    downstream of it.
                    Assert.Equal(
                        meshEntry.SourceVersions.Keys.OrderBy(k => k, StringComparer.Ordinal),
                        treeEntry.SourceVersions.Keys.OrderBy(k => k, StringComparer.Ordinal));

                    // 3b. 🚨 THE SOURCE FINGERPRINT (#2813) — THE equality this whole mechanism
                    //     rests on. The consumer refuses an adoption whose recorded fingerprint
                    //     differs from the one ITS OWN hub computes over ITS live sources, so a
                    //     producer that hashes even slightly differently turns every good bundle
                    //     into a refusal: a self-inflicted outage strictly worse than the staleness
                    //     bug. The mesh bake computes it through NodeSources.GetSources on a real
                    //     workspace — i.e. it IS the consumer-side value — and the tree bake
                    //     computes it from files with no mesh anywhere. Equal here means a bundle
                    //     baked by the compiler-driven producer is adoptable as VERIFIED by a mesh
                    //     holding the same content, which is the claim the fix makes.
                    Assert.False(
                        string.IsNullOrEmpty(meshEntry.SourceFingerprint),
                        $"the mesh bake recorded no source fingerprint for {nodePath} — a bundle "
                        + "without one adopts as AdoptedUnverified and the staleness refusal can "
                        + "never fire, which is the exact state #2813 sat in");
                    Assert.False(
                        string.IsNullOrEmpty(treeEntry.SourceFingerprint),
                        $"the compiler-driven bake recorded no source fingerprint for {nodePath}");
                    Assert.Equal(meshEntry.SourceFingerprint, treeEntry.SourceFingerprint);

                    // 4. The per-type DEPENDENCY RECORD — what PrebuiltAssemblySeeder validates
                    //    before adopting. A record that differs is a bundle that declines (or,
                    //    worse, one that adopts against a surface it was not built for).
                    Assert.Equal(
                        meshEntry.Dependencies.OrderBy(kv => kv.Key, StringComparer.Ordinal),
                        treeEntry.Dependencies.OrderBy(kv => kv.Key, StringComparer.Ordinal));

                    // 5. The emitted assemblies' TYPE AND MEMBER SURFACE. Not the bytes — see the
                    //    class remarks on why the mesh path is not byte-reproducible even against
                    //    itself — but everything a consumer can observe about them.
                    Assert.Equal(SurfaceOf(meshEntry.Assembly), SurfaceOf(treeEntry.Assembly));

                    // 6. Symbols ship from both producers (#1763 names DLL + PDB explicitly).
                    Assert.True(meshEntry.HasPdb, $"the mesh bake shipped no PDB for {nodePath}");
                    Assert.True(treeEntry.HasPdb, $"the compiler bake shipped no PDB for {nodePath}");
                }
            }

            // 7. The rules actually BIT — otherwise assertions 3-5 could agree because both
            //    producers made the same mistake on the same file.
            var thing = tree.Bundles["Widget"]["Widget/Thing"];
            var thingSurface = SurfaceOf(thing.Assembly).Select(TypeNameOf).ToList();

            // 7a. Cross-package `shared=`, the NESTED subtree file, and the default Test/ folder
            //     are all in the resolved set…
            Assert.Contains("Lib/Shared/Source/Helper", thing.SourceVersions.Keys);
            Assert.Contains("Widget/Thing/Source/Sub/Deep", thing.SourceVersions.Keys);
            Assert.Contains("Widget/Thing/Test/ThingTests", thing.SourceVersions.Keys);
            // …and reached the assembly.
            Assert.Contains("Helper", thingSurface);
            Assert.Contains("Deep", thingSurface);
            Assert.Contains("ThingTests", thingSurface);
            // 7a'. …and the fingerprint DISCRIMINATES. Two types with different sources must not
            //      hash the same, or assertion 3b above is satisfiable by a constant — which is
            //      exactly the shape of guard #2813 is about (one that cannot fail).
            Assert.NotEqual(
                tree.Bundles["Widget"]["Widget/Thing"].SourceFingerprint,
                tree.Bundles["Lib"]["Lib/Shared"].SourceFingerprint);

            // 7b. The @@ include pulled in a Code node NO source query matches — so it is absent
            //     from the resolved set but present in the bytes.
            //
            //     🚨 THE ABSENCE FROM `sourceVersions` IS DELIBERATE AND STAYS (#2948 reconciled
            //     this assertion rather than loosening it). `sourceVersions` mirrors the mesh's
            //     NodeTypeDefinition.CompiledSources, which is folded over the RAW QUERY MATCH —
            //     the same set on both producers, and the set BundleReader/PrebuiltAssemblySeeder
            //     treat as provenance. Adding include targets to it would change a
            //     producer/consumer contract (and 7d's whole point is that the two sets are not
            //     interchangeable). What #2948 changed is the SOURCE FINGERPRINT, a different
            //     field answering a different question — "were these bytes built from the source
            //     this mesh holds?" — and THAT must cover the include, because the include is in
            //     the bytes.
            Assert.DoesNotContain("Widget/Snippets/Greeting", thing.SourceVersions.Keys);
            Assert.Contains("Greeting", thingSurface);
            // 7b'. …and it IS in the fingerprint's input: the compile-driven bake records the
            //      include closure it substituted, and assertion 3b above proved the MESH bake —
            //      which resolves the same include through a real workspace, with no NodeSet
            //      anywhere — arrives at the identical hash. Without this line 3b's agreement
            //      would be satisfiable by BOTH producers ignoring the include, which is exactly
            //      the state #2948 found them in.
            Assert.Contains(
                "Widget/Snippets/Greeting",
                treeReport.Types.Single(t => t.NodePath == "Widget/Thing").ResolvedIncludePaths);

            // 7c. The `nodeType:Code` filter excluded the Scope-typed .cs from the QUERY itself…
            Assert.DoesNotContain("Widget/Thing/Source/Scoped", thing.SourceVersions.Keys);
            Assert.DoesNotContain("ScopeOnly", thingSurface);

            // 7d. …while the executable cell is a DIFFERENT rule at a DIFFERENT stage, and the two
            //     sets must not be conflated. `sourceVersions` mirrors the mesh's
            //     NodeTypeDefinition.CompiledSources, which DiscoverSourceVersionSnapshot folds
            //     over the RAW query match — so the cell IS recorded there, by both producers
            //     (assertion 3 already proved they agree). It is CollectCompileSources that drops
            //     it before Roslyn, so it must be absent from the compiled set and from the bytes:
            //     folding a top-level-statement cell in beside class declarations does not merely
            //     add a member, it fails the whole compile.
            Assert.Contains("Widget/Thing/Source/Cell", thing.SourceVersions.Keys);
            var compiledSources = treeReport.Types
                .Single(t => t.NodePath == "Widget/Thing")
                .MatchedSourcePaths;
            Assert.DoesNotContain("Widget/Thing/Source/Cell", compiledSources);
            Assert.DoesNotContain("Widget/Thing/Source/Scoped", compiledSources);
            Assert.Equal(
                ["Lib/Shared/Source/Helper", "Widget/Thing/Source/Sub/Deep",
                 "Widget/Thing/Source/Thing", "Widget/Thing/Test/ThingTests"],
                compiledSources.OrderBy(p => p, StringComparer.Ordinal));
        }
        finally
        {
            Cleanup(repo);
            Cleanup(meshDir);
            Cleanup(treeDir);
        }
    }

    /// <summary>
    /// 🚨 A type the bake CANNOT resolve must go RED, never quietly vanish.
    ///
    /// <para>A NodeType with no <c>configuration</c> lambda compiles only if it has sources, so the
    /// bake needs a "nothing to compile here" skip. The trap is judging that on the resolved set
    /// alone: a source-only type whose query this evaluator cannot answer resolves to nothing, and
    /// the skip then drops it from the bundle with no entry, no RED and no line saying a type was
    /// dropped — a consumer would simply compile it, and the bake would have shipped less than it
    /// claimed while exiting 0. That is the skip-trapdoor shape AGENTS.md forbids, and the reason
    /// "established" is a PRECONDITION of the emptiness judgement rather than part of it.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void ASourceOnlyTypeWithAnUnevaluableQuery_FailsLoudly_NeverSilentlySkipped()
    {
        const string unevaluableNodeType =
            """{"$type":"MeshNode","id":"Ghost","namespace":"Widget","path":"Widget/Ghost","mainNode":"Widget/Ghost","name":"Ghost","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"Sources only, via a query the bake cannot evaluate.","sources":["namespace:*/Source scope:subtree"]}}""";

        var repo = CreateRepo(root =>
        {
            WriteFile(root, "Widget/index.json", WidgetIndexJson);
            WriteFile(root, "Widget/Ghost.json", unevaluableNodeType);
            WriteFile(root, "Widget/Ghost/Source/Ghost.cs", DeepSource);
        });
        var bakeDir = Path.Combine(Path.GetTempPath(), "mw-bake-ghost-" + Guid.NewGuid().ToString("N"));
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

            var ghost = Assert.Single(report.Types, t => t.NodePath == "Widget/Ghost");
            Assert.False(ghost.Success);
            Assert.Contains("could not be ESTABLISHED", ghost.Error);
            // …and the run does not report success on it.
            Assert.NotEqual(0, report.ExitCode);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(bakeDir);
        }
    }

    // ── reading a bake directory back through the consumers' own codec ──

    private sealed record BakeEntry(
        byte[] Assembly,
        bool HasPdb,
        IReadOnlyDictionary<string, string> Dependencies,
        IReadOnlyDictionary<string, long> SourceVersions,
        string? SourceFingerprint);

    private sealed record BakeDirectory(
        string FrameworkIdentity,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, BakeEntry>> Bundles);

    private static BakeDirectory ReadBakeDirectory(string directory)
    {
        var identity = File.ReadAllText(Path.Combine(directory, BakeOutput.FrameworkMvidFile));
        var bundles = new Dictionary<string, IReadOnlyDictionary<string, BakeEntry>>(StringComparer.Ordinal);
        foreach (var zip in Directory.EnumerateFiles(directory, "*.zip").OrderBy(f => f, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(zip);
            var (manifest, payloads) = BundleReader.Read(bytes);
            Assert.NotNull(manifest);
            // sourceVersions is provenance the reader deliberately skips, so it is read straight
            // off the manifest entry — this test is precisely the consumer that cares.
            var sourceVersions = ReadSourceVersions(bytes);
            bundles[Path.GetFileNameWithoutExtension(zip)] = payloads.ToDictionary(
                p => p.NodePath,
                p => new BakeEntry(
                    p.Assembly,
                    p.Pdb is { Length: > 0 },
                    p.Dependencies ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    sourceVersions.GetValueOrDefault(p.NodePath)
                        ?? new Dictionary<string, long>(StringComparer.Ordinal),
                    // #2813 — read through BundleReader, not off the raw manifest: this is the
                    // value a real consumer receives, so a reader that dropped it would show up
                    // here as two nulls comparing equal rather than as a passing test.
                    p.SourceFingerprint),
                StringComparer.Ordinal);
        }
        return new BakeDirectory(identity, bundles);
    }

    private static Dictionary<string, IReadOnlyDictionary<string, long>> ReadSourceVersions(byte[] bundle)
    {
        using var archive = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        var entry = archive.GetEntry(NuGetPackageWriter.ManifestEntry);
        Assert.NotNull(entry);
        using var stream = entry!.Open();
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("assemblies", out var assemblies))
            return result;
        foreach (var assembly in assemblies.EnumerateArray())
        {
            if (!assembly.TryGetProperty("nodePath", out var nodePath))
                continue;
            var versions = new Dictionary<string, long>(StringComparer.Ordinal);
            if (assembly.TryGetProperty("sourceVersions", out var sv)
                && sv.ValueKind == JsonValueKind.Object)
                foreach (var property in sv.EnumerateObject())
                    versions[property.Name] = property.Value.GetInt64();
            result[nodePath.GetString()!] = versions;
        }
        return result;
    }

    /// <summary>
    /// The observable surface of an emitted assembly: every type definition and its members, as
    /// sorted text. Read through <see cref="MetadataReader"/> rather than by loading the assembly,
    /// so two builds of the SAME assembly name can be compared in one process with no
    /// <c>AssemblyLoadContext</c> and no unload race.
    /// </summary>
    private static IReadOnlyList<string> SurfaceOf(byte[] assembly)
    {
        using var pe = new PEReader(new MemoryStream(assembly));
        var reader = pe.GetMetadataReader();
        var surface = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Namespace) is { Length: > 0 } ns
                ? $"{ns}.{reader.GetString(type.Name)}"
                : reader.GetString(type.Name);
            var members = type.GetMethods()
                .Select(m => reader.GetString(reader.GetMethodDefinition(m).Name))
                .Concat(type.GetFields()
                    .Select(f => reader.GetString(reader.GetFieldDefinition(f).Name)))
                .OrderBy(m => m, StringComparer.Ordinal);
            surface.Add($"{name}: {string.Join(",", members)}");
        }
        surface.Sort(StringComparer.Ordinal);
        return surface;
    }

    private static string TypeNameOf(string surfaceLine)
        => surfaceLine[..surfaceLine.IndexOf(':', StringComparison.Ordinal)];

    private static string CreateRepo(Action<string> populate)
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-equiv-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        populate(root);
        return root;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Writes a file WITH a UTF-8 BOM — the shape the runtime's JSON parser rejects.</summary>
    private static void WriteFileWithBom(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
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
