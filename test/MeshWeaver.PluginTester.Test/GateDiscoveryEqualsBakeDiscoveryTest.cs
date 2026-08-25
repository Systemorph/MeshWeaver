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
using MeshWeaver.PluginCatalog;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 <b>The gate's discovery and the baker's discovery must be the SAME SET.</b>
///
/// <para>CI runs two halves over one tree: <c>TreeBake</c> decides which NodeTypes to COMPILE and
/// ship bytes for, and <see cref="PluginGateRunner"/> decides which NodeTypes to JUDGE (compile
/// status, default-area render, executable <c>Tests</c> area). Nothing forces those two decisions
/// to agree, and when they disagree the run stays GREEN either way:</para>
/// <list type="bullet">
///   <item>a type the baker sees and the gate does not is <b>installed, compiled and shipped
///     UNGATED</b> — nothing ever renders or tests it;</item>
///   <item>a type the gate sees and the baker/installer does not never appears in the mesh, so the
///     gate waits out its whole compile budget and dies with a TimeoutException that reads as
///     harness noise.</item>
/// </list>
///
/// <para><b>Both have happened</b> (#2063). The gate parsed package bytes with a raw
/// <c>JsonDocument.Parse</c> while the installer stripped the UTF-8 BOM
/// (<c>FileFormatParserRegistry.WithoutBom</c>, #1767), so every BOM'd <c>.json</c> was dropped by
/// one half and kept by the other: <c>samples/Graph/Data/PensionFund</c> ships 5 BOM'd NodeTypes
/// and CI printed <c>[PASS] PensionFund (72 node(s), 0 type(s))</c> — a green verdict over nothing.
/// And the gate mapped file→path with <c>NodeFileMapper.FromRelativePath</c>, only half of
/// <c>PackageInstaller.NodePathForFile</c>, so a NodeType-shaped <c>.json</c> under
/// <c>content/**</c> was discovered by the gate and installed by nobody.</para>
///
/// <para><b>So this test asserts the SETS, not either symptom.</b> The BOM and the exclusions are
/// the two known drifts; the equality is what stops the third one. The fixture below deliberately
/// carries every file shape whose treatment the two halves could disagree about.</para>
///
/// <para>Discovery only — no mesh, no Roslyn, no compile. That is the point: the assertion is about
/// which files each half decides are NodeTypes, which is answerable in milliseconds, so it can be
/// exhaustive about file shapes in a way <c>BakeEquivalenceTest</c> (which compiles both sides) is
/// not.</para>
/// </summary>
public class GateDiscoveryEqualsBakeDiscoveryTest(ITestOutputHelper output)
{
    private const string WidgetIndexJson =
        """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin."}}""";

    /// <summary>
    /// A NodeType node file. Written as a template with <c>%ID%</c>/<c>%NS%</c> placeholders rather
    /// than interpolation: the JSON's own trailing <c>}}</c> collides with raw-string interpolation
    /// braces, and the readable spelling is worth more here than the interpolation.
    /// </summary>
    private const string NodeTypeTemplate =
        """{"$type":"MeshNode","id":"%ID%","namespace":"%NS%","path":"%NS%/%ID%","mainNode":"%NS%/%ID%","name":"%ID%","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A %ID%.","configuration":"config => config.WithContentType<%ID%>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    private static string NodeTypeJson(string id, string ns) =>
        NodeTypeTemplate.Replace("%ID%", id, StringComparison.Ordinal)
            .Replace("%NS%", ns, StringComparison.Ordinal);

    /// <summary>An ordinary node that is NOT a NodeType — neither half may discover it.</summary>
    private const string MarkdownNodeJson =
        """{"$type":"MeshNode","id":"Readme","namespace":"Widget","path":"Widget/Readme","name":"Readme","nodeType":"Markdown","state":"Active","content":{"$type":"MarkdownContent","markdown":"hello"}}""";

    [Fact(Timeout = 120_000)]
    public async Task GateDiscovery_EqualsBakeDiscovery_OverTheSameTree()
    {
        var repo = CreateRepo(root =>
        {
            WriteFile(root, "Widget/index.json", WidgetIndexJson);

            // 1. The ordinary case — both halves must see it.
            WriteFile(root, "Widget/Thing.json", NodeTypeJson("Thing", "Widget"));

            // 2. 🚨 THE BOM. Byte-identical to (1) except for a leading U+FEFF. Package content
            //    arrives as BYTES and is decoded with Encoding.UTF8.GetString, which PRESERVES the
            //    BOM (File.ReadAllText's encoding detection strips it — which is why a path that
            //    only ever read from disk never saw this). This is the file the gate dropped and
            //    the installer kept.
            WriteFileWithBom(root, "Widget/Bommed.json", NodeTypeJson("Bommed", "Widget"));

            // 3. A nested BOM'd type: the PensionFund shape (BOM'd types in a subfolder), so the
            //    path mapping is exercised on a BOM'd file and not only the BOM strip.
            WriteFileWithBom(root, "Widget/Nested/Deep.json", NodeTypeJson("Deep", "Widget/Nested"));

            // 4. 🚨 THE EXCLUSIONS. A NodeType-shaped .json under content/** is a content ASSET,
            //    not a node: PackageInstaller.NodePathForFile returns null for it, so it is never
            //    installed. A gate that discovered it would wait out its full compile timeout for
            //    a node that cannot exist.
            WriteFile(root, "Widget/content/Asset.json", NodeTypeJson("Asset", "Widget/content"));

            // 5. Files neither half may treat as a node, for three different reasons.
            WriteFile(root, "Widget/Readme.json", MarkdownNodeJson);   // a node, but not a NodeType
            WriteFile(root, "README.md", "# not a node");              // excluded by name
            WriteFile(root, "Widget/Unparseable.json", "{ this is not json");
        });
        try
        {
            // ── the BAKER's discovery: exactly what TreeBake.BakeAll folds over (`compilable`) ──
            var snapshot = LocalNodeRepo.LoadSync(repo);
            var packages = await LocalNodeRepo.DiscoverPackages(snapshot).FirstAsync().ToTask();
            var skipped = new List<string>();
            var bakeDiscovered = TreeNodeLoader
                .Load(snapshot, packages, (path, reason) => skipped.Add($"{path}: {reason}"))
                .Where(t => t.Node.Content is NodeTypeDefinition)
                .Select(t => t.Node.Path)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            // ── the GATE's discovery: the same call TestPackage makes, on the same package bytes
            //    the gate fetches (NodeRepoPackageSource over the same snapshot) ──
            var source = new NodeRepoPackageSource(
                (_, _, _, _) => Observable.Return(snapshot), repoUrl: "local");
            var gateDiscovered = new List<string>();
            foreach (var package in packages)
            {
                var files = await source.FetchPackageFiles(package, "HEAD").FirstAsync().ToTask();
                gateDiscovered.AddRange(
                    PluginGateRunner.DiscoverNodeTypes(package, files).Select(t => t.Path));
            }
            gateDiscovered.Sort(StringComparer.Ordinal);

            output.WriteLine("bake discovered: " + string.Join(", ", bakeDiscovered));
            output.WriteLine("gate discovered: " + string.Join(", ", gateDiscovered));
            output.WriteLine("bake skipped:    " + string.Join(" | ", skipped));

            // THE ASSERTION. Not "the gate found the BOM'd one" — the SETS. A future divergence of
            // any shape (a new exclusion, a new parser tolerance, a new file convention) fails here
            // whether it drops a type or invents one.
            Assert.Equal(bakeDiscovered, gateDiscovered);

            // …and pin what that set actually IS, so the equality cannot be satisfied by both
            // halves regressing together — two empty sets are equal too, which is exactly the
            // vacuous green this test exists to make impossible.
            Assert.Equal(
                new[] { "Widget/Bommed", "Widget/Nested/Deep", "Widget/Thing" },
                gateDiscovered);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    /// <summary>
    /// The NEGATIVE control: the fixture above must actually be able to fail. A tree whose ONLY
    /// NodeType carries a BOM is the #2063 case in miniature — before the fix the gate discovered
    /// zero types here while the bake discovered one, and the gate reported a pass.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABommedNodeType_IsTheOnlyType_AndBothHalvesFindIt()
    {
        var repo = CreateRepo(root =>
        {
            WriteFile(root, "Widget/index.json", WidgetIndexJson);
            WriteFileWithBom(root, "Widget/Only.json", NodeTypeJson("Only", "Widget"));
        });
        try
        {
            var snapshot = LocalNodeRepo.LoadSync(repo);
            var packages = await LocalNodeRepo.DiscoverPackages(snapshot).FirstAsync().ToTask();
            var source = new NodeRepoPackageSource(
                (_, _, _, _) => Observable.Return(snapshot), repoUrl: "local");
            var files = await source.FetchPackageFiles(packages[0], "HEAD").FirstAsync().ToTask();
            var gate = PluginGateRunner.DiscoverNodeTypes(packages[0], files);

            // The count IS the assertion — `0 type(s)` was the silent pass.
            Assert.Single(gate);
            Assert.Equal("Widget/Only", gate[0].Path);
            // A BOM'd type still carries its configuration lambda: the strip must happen before the
            // parse, not after, or the type is discovered as a no-op with nothing to compile.
            Assert.True(gate[0].Compiles);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    // ── fixture plumbing ──

    private static string CreateRepo(Action<string> build)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "mw-gate-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        build(root);
        return root;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // No BOM: UTF8Encoding(false) — the default `File.WriteAllText` on .NET writes none, but
        // spelling it out keeps this file's whole point explicit beside WriteFileWithBom.
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteFileWithBom(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory costs disk, never correctness.
        }
    }
}
