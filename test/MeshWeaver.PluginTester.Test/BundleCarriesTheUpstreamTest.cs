using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The Education shape, pinned: <b>repo 1 ships a base package; repo 2 depends on it and must be
/// able to install it from repo 1's BUNDLE — never from repo 1's source.</b>
///
/// <para>Until this change, that was impossible by construction. <c>BundleReader.Manifest.Content</c>
/// has said all along that "a consumer that means to USE this package as an upstream needs
/// [the node definitions]: the bytes only stamp nodes that already exist, so without the
/// definitions there is nothing to stamp" — and both bake call sites passed no content, so every
/// published bundle was assemblies-only. The consequence was measured 2026-08-27: every plugin
/// repo stages its upstreams as SOURCE (<c>stage-repo</c>) and recompiles them, five meshes per
/// Education run each recompiling Store's 17 assemblies; core passes <c>--seed</c> in 9 places and
/// every plugin repo in 0.</para>
///
/// <para>The gate's own refusal names the gap in so many words
/// (<see cref="BakeSeedConsumer.Shortfall"/>): "the bake declares assemblies for N NodeType(s),
/// NONE of which this run installed — bake and gate must be staged from the same tree". With the
/// definitions in the bundle, they no longer have to be.</para>
///
/// <para>This test runs the BUILD step only — no mesh, no Roslyn round-trip — because the claim
/// under test is about what the artifact CARRIES, and that is checkable in milliseconds. The
/// mesh-level round-trip (baked bytes are the bytes served) is <c>BakeGateSplitTest</c>'s job and
/// is unchanged by this.</para>
/// </summary>
public class BundleCarriesTheUpstreamTest(ITestOutputHelper output)
{
    // ── Repo 1: the BASE package. The shape every store product has — a root plus one NodeType. ──
    private const string BaseIndexJson =
        """{"$type":"MeshNode","id":"Base","namespace":"","path":"Base","mainNode":"Base","name":"Base Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"The base package repo 2 depends on."}}""";

    private const string BaseTypeJson =
        """{"$type":"MeshNode","id":"Widget","namespace":"Base","path":"Base/Widget","mainNode":"Base/Widget","name":"Widget","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A widget.","configuration":"config => config.WithContentType<Widget>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    private const string BaseSource =
        """
        public record Widget
        {
            public string Name { get; init; } = string.Empty;
        }
        """;

    // A non-node asset, so the test proves the WHOLE tree ships — not only the JSON.
    private const string BaseGuide = "# Base\n\nThe package repo 2 installs.\n";

    [Fact(Timeout = 300_000)]
    public void TheBaseBundle_CarriesEveryFileRepo2NeedsToInstallIt()
    {
        var repo1 = TempDirectory("mw-upstream-repo1");
        var bakeDir = TempDirectory("mw-upstream-bake");
        try
        {
            Write(repo1, "Base/index.json", BaseIndexJson);
            Write(repo1, "Base/Widget.json", BaseTypeJson);
            Write(repo1, "Base/Widget/Source/Widget.cs", BaseSource);
            Write(repo1, "Base/Guide.md", BaseGuide);

            // ── Repo 1 BUILDS. No mesh. ──
            var log = new StringWriter();
            var bake = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo1,
                OutputDirectory = bakeDir,
                SourceSha = "cafebabe",
                Output = log,
            });
            output.WriteLine(log.ToString());
            Assert.Null(bake.FatalError);
            Assert.All(bake.Types, t => Assert.Null(t.Error));

            var bundlePath = Path.Combine(bakeDir, "Base.zip");
            Assert.True(File.Exists(bundlePath), "the bake must write one bundle per package");
            var bytes = File.ReadAllBytes(bundlePath);

            // ── THE CLAIM: the bundle carries the package, not only its bytes. ──
            var (manifest, files) = BundleReader.ReadContent(bytes);
            Assert.NotNull(manifest);
            Assert.NotNull(manifest!.Content);
            Assert.True(manifest.SourceIncluded,
                "the bake withholds nothing, and must DECLARE so — a consumer that cannot resolve an "
                + "include has to know whether the source was withheld or never existed");

            var carried = files.ToDictionary(f => f.RelativePath, f => f.Bytes, StringComparer.Ordinal);
            foreach (var expected in new[] { "index.json", "Widget.json", "Widget/Source/Widget.cs", "Guide.md" })
                Assert.True(carried.ContainsKey(expected),
                    $"the bundle must carry '{expected}' relative to the package root — without it "
                    + "repo 2 has nothing to install, only bytes that stamp nodes it does not have. "
                    + $"Carried: [{string.Join(", ", carried.Keys)}]");

            // Byte-exact, not merely present: what repo 2 installs is what repo 1 authored.
            Assert.Equal(BaseIndexJson, Encoding.UTF8.GetString(carried["index.json"]));
            Assert.Equal(BaseSource, Encoding.UTF8.GetString(carried["Widget/Source/Widget.cs"]));
            Assert.Equal(BaseGuide, Encoding.UTF8.GetString(carried["Guide.md"]));

            // ── And the assemblies are still there — content is ADDED, not swapped in. ──
            var (_, assemblies) = BundleReader.Read(bytes);
            Assert.Contains(assemblies, a => a.NodePath == "Base/Widget");
        }
        finally
        {
            Cleanup(repo1);
            Cleanup(bakeDir);
        }
    }

    /// <summary>
    /// The consumer side of the same claim: from repo 1's bundle ALONE — no checkout of repo 1 —
    /// repo 2 can lay the base package down as a tree the tester recognises and bakes. This is what
    /// "install my dependencies" means; today every repo does it with a sparse checkout instead.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void Repo2_CanReconstructTheBasePackage_FromRepo1sBundleAlone()
    {
        var repo1 = TempDirectory("mw-upstream-src");
        var bake1 = TempDirectory("mw-upstream-bake1");
        var repo2 = TempDirectory("mw-upstream-repo2");
        var bake2 = TempDirectory("mw-upstream-bake2");
        try
        {
            Write(repo1, "Base/index.json", BaseIndexJson);
            Write(repo1, "Base/Widget.json", BaseTypeJson);
            Write(repo1, "Base/Widget/Source/Widget.cs", BaseSource);
            Write(repo1, "Base/Guide.md", BaseGuide);
            var first = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo1, OutputDirectory = bake1, SourceSha = "cafebabe", Output = TextWriter.Null,
            });
            Assert.Null(first.FatalError);

            // Repo 2 holds ONLY the bundle. Its checkout of repo 1 does not exist.
            var (_, files) = BundleReader.ReadContent(File.ReadAllBytes(Path.Combine(bake1, "Base.zip")));
            Assert.NotEmpty(files);
            foreach (var f in files)
            {
                var target = Path.Combine(repo2, "Base", f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, f.Bytes);
            }

            // The reconstructed tree is a package the tester discovers and can build — the same
            // outcome a sparse checkout of repo 1 would have produced, from the artifact instead.
            var log = new StringWriter();
            var second = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo2, OutputDirectory = bake2, SourceSha = "cafebabe", Output = log,
            });
            output.WriteLine(log.ToString());
            Assert.Null(second.FatalError);
            Assert.Contains(second.Types, t => t.NodePath == "Base/Widget" && t.Error is null);
            Assert.True(File.Exists(Path.Combine(bake2, "Base.zip")),
                "repo 2 rebuilt the base from the bundle's tree — proof the bundle carried a "
                + "complete, installable package and not just assemblies");
        }
        finally
        {
            Cleanup(repo1);
            Cleanup(bake1);
            Cleanup(repo2);
            Cleanup(bake2);
        }
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
        catch { /* best effort — a locked temp dir must not fail the test */ }
    }
}
