#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 <b>The tester's file sweep reads THIS checkout, never another git tree nested inside it.</b>
///
/// <para>A local checkout that keeps its linked worktrees inside itself (<c>.worktrees/&lt;x&gt;</c>,
/// or any folder whose <c>.git</c> is a FILE) made <see cref="LocalNodeRepo.LoadSync"/> read every
/// one of them before package discovery — minutes of silence, a snapshot carrying other branches'
/// files, and a top-level linked worktree whose root holds a node-typed <c>index.json</c>
/// discovered as a package of its own. A clean CI checkout has none of these, so CI never saw it;
/// the fixture below builds the local shape on purpose.</para>
/// </summary>
public class LocalNodeRepoSweepSkipsNestedTreesTest
{
    private const string WidgetIndexJson =
        """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin."}}""";

    private const string DupIndexJson =
        """{"$type":"MeshNode","id":"Dup","namespace":"","path":"Dup","mainNode":"Dup","name":"Dup","nodeType":"Space","state":"Active"}""";

    // What `git worktree add` writes into a linked worktree's root.
    private const string GitFile = "gitdir: /elsewhere/.git/worktrees/x\n";

    [Fact(Timeout = 120_000)]
    public async Task NestedWorktreesAndRepositories_AreNotSwept_AndOnlyTheRootPackageIsDiscovered()
    {
        var repo = CreateRepo(root =>
        {
            // The sweep root is itself a linked worktree — it must still be swept.
            WriteFile(root, ".git", GitFile);
            WriteFile(root, "Widget/index.json", WidgetIndexJson);
            WriteFile(root, "Widget/Readme.md", "# Widget");

            // 1. The conventional worktree folder: a duplicate of the root package, other branch.
            WriteFile(root, ".worktrees/x/.git", GitFile);
            WriteFile(root, ".worktrees/x/Widget/index.json", WidgetIndexJson);

            // 2. A linked worktree checked out at the top level under another name — its own root
            //    index.json declares a node type, so before the fix it became a package "Dup".
            WriteFile(root, "Dup/.git", GitFile);
            WriteFile(root, "Dup/index.json", DupIndexJson);
            WriteFile(root, "Dup/Widget/index.json", WidgetIndexJson);

            // 3. A nested repository (a `.git` DIRECTORY) inside a package folder.
            WriteFile(root, "Widget/vendor/.git/HEAD", "ref: refs/heads/main\n");
            WriteFile(root, "Widget/vendor/Foreign.json", WidgetIndexJson);
        });
        try
        {
            var snapshot = LocalNodeRepo.LoadSync(repo);
            var packages = await LocalNodeRepo.DiscoverPackages(snapshot).FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "Widget" }, packages.Select(p => p.Id).ToArray());

            // The root's own `.git` pointer file is VCS plumbing, like a CI checkout's `.git` dir.
            var paths = snapshot.Files.Select(f => f.Path).ToArray();
            Assert.Equal(new[] { "Widget/Readme.md", "Widget/index.json" }, paths);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    private static string CreateRepo(Action<string> build)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "mw-sweep-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        build(root);
        return root;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
