#pragma warning disable CS1591
using System;
using System.IO;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #3384 made the shipped node-repo format the default for every reader of a catalog node — and
/// turned every undeclared <c>package.json</c> catalog into an empty page (measured on the Plugins
/// pin move to 3.0.0-ci.7917: two migrated PluginCatalog tests rendered zero cards). A declared
/// format still wins; an undeclared one on a local checkout is read off the layout.
/// </summary>
public class UndeclaredCatalogFormatIsDetectedTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-catfmt-" + Guid.NewGuid().ToString("N"));

    private string Repo(string name) { var p = Path.Combine(root, name); Directory.CreateDirectory(p); return p; }
    private static void Touch(string repo, string relative)
    {
        var p = Path.Combine(repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "{}");
    }

    [Fact]
    public void AnUndeclaredPackageJsonCatalog_IsReadAsPackageJson()
    {
        var repo = Repo("pkg");
        Touch(repo, "catalog/pack-a/package.json");
        Touch(repo, "catalog/pack-b/package.json");
        Assert.False(PackageSources.IsNodeRepoFormatOrDetected(null, repo, "catalog"),
            "two package.json manifests under the subdir and no index.json anywhere is the manifest shape — the two Plugins tests that went dark on 7917");
        Assert.False(PackageSources.IsNodeRepoFormatOrDetected("", repo, "catalog"));
    }

    [Fact]
    public void AnUndeclaredNodeRepoCheckout_StaysNodeRepo()
    {
        var repo = Repo("nr");
        Touch(repo, "Edu/index.json");
        Touch(repo, "Store/index.json");
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, repo, null), "Space roots at the repo root — the #3384 case keeps working");
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, repo, "plugins"), "a subdir that does not exist changes nothing");
    }

    [Fact]
    public void BothShapesAtOnce_FallBackToTheShippedDefault()
    {
        var repo = Repo("both");
        Touch(repo, "Edu/index.json");
        Touch(repo, "catalog/pack-a/package.json");
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, repo, "catalog"), "ambiguity is not evidence for the manifest shape");
    }

    [Fact]
    public void ADeclaredFormat_WinsOverTheLayout()
    {
        var repo = Repo("declared");
        Touch(repo, "catalog/pack-a/package.json");
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected("node-repo", repo, "catalog"), "declared node-repo over a manifest layout is honoured — the operator said so");
        var nr = Repo("declared-nr");
        Touch(nr, "Edu/index.json");
        Assert.False(PackageSources.IsNodeRepoFormatOrDetected("package-json", nr, null), "declared package-json over a node-repo layout is honoured too");
    }

    [Fact]
    public void AUrlOrAnAbsentCheckout_KeepsTheShippedDefault()
    {
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, "https://github.com/Systemorph/MeshWeaver.Plugins", "catalog"), "nothing to inspect before a fetch");
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, Path.Combine(root, "does-not-exist"), "catalog"), "an absent directory is not evidence");
        var empty = Repo("empty");
        Directory.CreateDirectory(Path.Combine(empty, "catalog"));
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, empty, "catalog"), "an existing but EMPTY checkout is not evidence for the manifest shape either — it must not silently pick package.json");
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, null, null));
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, "", null));
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, "   ", null), "whitespace is absent, not a path to scan");
        var pkg = Repo("rooted");
        Touch(pkg, "catalog/pack-a/package.json");
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, pkg, Path.Combine(pkg, "catalog")),
            "a ROOTED subdir would make Path.Combine drop the repo and scan elsewhere — refused into the default");
        Assert.True(PackageSources.IsNodeRepoFormatOrDetected(null, "\0not-a-path\0", "catalog"), "a malformed path fails closed to the default, never throws into a render");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
