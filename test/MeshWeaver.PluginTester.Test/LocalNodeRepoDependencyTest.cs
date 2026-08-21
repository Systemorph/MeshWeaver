#pragma warning disable CS1591

using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// <see cref="LocalNodeRepo.OrderByDependencies"/> — and, through it, the private
/// <c>CollectDependencies</c> that is the SOURCE OF TRUTH for two other things: the gate's install
/// order, and the module-selection the node repos' <c>scripts/affected-modules.py</c> mirrors 1:1
/// (its docstring names this method, and the safety argument for narrowing a CI bake to the
/// affected closure rests entirely on that mirror being true).
///
/// <para>🚨 The case that motivated this file: a <c>requires</c> entry carries an OPTIONAL VERSION
/// RANGE — <c>"Store@^1.0.0"</c> — and the reader compared the whole entry against a bare package
/// id, so it matched nothing and the edge silently disappeared. Nothing failed; the dependency was
/// simply never collected. Measured across MeshWeaver.Plugins on 2026-08-21: <b>51 of its 52
/// declared <c>requires</c> edges were dead</b>, including every one of the eleven modules
/// <c>Essentials</c> declares. <see cref="PackageDependencyGraph.DependencyId"/> has parsed the
/// form correctly all along — this reader was the one that drifted.</para>
///
/// <para>Each test below discriminates by ORDER, because that is what a dropped edge changes: the
/// Kahn sort is alphabetical among the ready set, so a package whose only reason to come later is
/// a dependency edge comes FIRST when the edge is missing.</para>
/// </summary>
public class LocalNodeRepoDependencyTest
{
    private static PackageManifest Package(string id, params string[] requires) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = PackageKind.NodeRepo,
            TargetPartition = id,
            SourceFolder = id,
            Version = "sha",
            Requires = requires.ToImmutableList(),
        };

    private static readonly RepoSnapshot NoFiles = new("sha", []);

    private static string[] Order(params PackageManifest[] packages) =>
        LocalNodeRepo.OrderByDependencies(packages, NoFiles).Select(p => p.Id).ToArray();

    // ── the drift ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Requires_WithAVersionRange_IsStillADependencyEdge()
    {
        // "Essentials" sorts BEFORE "Mcp" alphabetically, so it leads the ready set unless the
        // edge exists. Reading the entry raw ("Mcp@^1.0.0" != "Mcp") is exactly how the edge
        // vanished — and this assertion is what fails when it does.
        Assert.Equal(
            ["Mcp", "Essentials"],
            Order(Package("Essentials", "Mcp@^1.0.0"), Package("Mcp")));
    }

    [Fact]
    public void Requires_WithAVersionRange_CollectsEveryDeclaredDependency()
    {
        // The real shape of Essentials/index.json: eleven declared modules, every one versioned.
        // Before the fix this collected NOTHING and Essentials led the order.
        var packages = new[]
        {
            Package("Essentials", "Store@^1.0.0", "Export@^1.0.0", "Mcp@^1.0.0"),
            Package("Store"), Package("Export"), Package("Mcp"),
        };
        Assert.Equal(["Export", "Mcp", "Store", "Essentials"], Order(packages));
    }

    [Fact]
    public void Requires_WithoutAVersionRange_StillWorks()
    {
        // The un-versioned form is the one that always worked; the fix must not regress it.
        Assert.Equal(["Mcp", "Essentials"], Order(Package("Essentials", "Mcp"), Package("Mcp")));
    }

    [Fact]
    public void Requires_ToleratesSurroundingWhitespace()
    {
        Assert.Equal(["Mcp", "Essentials"], Order(Package("Essentials", " Mcp @^1.0.0"), Package("Mcp")));
    }

    // ── the guards that keep the strip from inventing edges ──────────────────────────────────

    [Fact]
    public void Requires_ForAPackageOutsideTheSet_IsIgnored()
    {
        // A cross-repo dependency (Store, staged from another repo) cannot be ordered against and
        // must not fabricate an entry — the runtime's tolerance, unchanged by the strip.
        Assert.Equal(["Essentials"], Order(Package("Essentials", "Training@^1.0.0")));
    }

    [Fact]
    public void Requires_ThatIsBlankOrVersionOnly_IsNotAnEdge()
    {
        // DependencyId returns "" for these; an empty id must never be looked up, or a package
        // named "" would become everyone's dependency.
        Assert.Equal(["Essentials", "Mcp"], Order(Package("Essentials", "", "@^1.0.0", "   "), Package("Mcp")));
    }

    [Fact]
    public void Requires_OnItself_IsNotAnEdge()
    {
        // A self-edge would be an unbreakable cycle; the id comparison must survive the strip.
        Assert.Equal(["Essentials", "Mcp"], Order(Package("Essentials", "Essentials@^1.0.0"), Package("Mcp")));
    }

    [Fact]
    public void Requires_ThatCycles_EmitsEveryPackageExactlyOnce()
    {
        // Making the edges REAL makes cycles reachable that the dead comparison could never form.
        // The documented behaviour is "install the rest alphabetically", never drop or loop.
        var order = Order(Package("A", "B@^1.0.0"), Package("B", "A@^1.0.0"));
        Assert.Equal(2, order.Length);
        Assert.Equal(["A", "B"], order.Order().ToArray());
    }
}
