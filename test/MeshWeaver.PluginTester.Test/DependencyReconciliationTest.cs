using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The reconciliation report and the dynamic-edge ratchet (<c>Doc/Architecture/DependencyGraph</c>
/// → <i>The reconciliation report</i>, <i>The ratchet</i>): what a tree USED (its edges file)
/// against what it DECLARED (package manifests, <c>#r "nuget:"</c>, <c>.csproj</c> references), and
/// the shrink-only list of reflection bindings into core.
///
/// <para>The ratchet has the warning ratchet's controls: observe-only enforces nothing and says so,
/// an empty baseline tolerates nothing, an unlisted edge is NEW, a listed edge the source no longer
/// carries is STALE, and an entry whose source was not extracted is unverifiable, never stale.</para>
/// </summary>
public sealed class DependencyReconciliationTest : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mw-depreport-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private const string HomeAppSource = "MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph";

    private static DependencyEdge Edge(string kind, string target, string? targetKind = null) => new()
    {
        Target = target,
        Kind = kind,
        TargetKind = targetKind,
        EvidenceClass = kind is DependencyEdgeKinds.Dynamic or DependencyEdgeKinds.DynamicUnresolved
            ? DependencyEvidenceClasses.Dynamic
            : DependencyEvidenceClasses.Static,
    };

    private static DependencyEdgesFile.Document Document(ImmutableArray<string> failed, params DependencyEdgeSet[] sets) =>
        new(DependencyEdgesFile.Schema, "c003e001", "abc", [.. sets], failed);

    private static DependencyEdgeSet Set(string source, params DependencyEdge[] edges) =>
        new() { Source = source, Edges = [.. edges] };

    /// <summary>A source with a dynamic edge into the platform, one unresolved, and one into the
    /// framework — only the first two are "into core".</summary>
    private static DependencyEdgesFile.Document Maps() => Document([],
        Set("App/Maps",
            Edge(DependencyEdgeKinds.Dynamic, HomeAppSource, DependencyEdgeKinds.Platform),
            Edge(DependencyEdgeKinds.DynamicUnresolved, "?System.Type.GetType"),
            Edge(DependencyEdgeKinds.Dynamic, "System.Text.StringBuilder, System.Runtime", DependencyEdgeKinds.Framework)));

    [Fact]
    public void ObserveOnly_EnforcesNothing_AndNamesTheRepositoryThatHasNotAdopted()
    {
        var ratchet = DynamicEdgeRatchet.Evaluate(Maps(), DynamicEdgeBaseline.ObserveOnly);
        Assert.True(ratchet.Success);
        Assert.Equal(2, ratchet.New.Length);
        var log = new StringWriter();
        ratchet.Write(log, DynamicEdgeBaseline.ObserveOnly, "acme/plugins");
        Assert.Contains("dynamic-edge ratchet — OBSERVE-ONLY: acme/plugins has not adopted it", log.ToString());
        Assert.Contains($"dependencies: baseline App/Maps Dynamic {HomeAppSource}", log.ToString());
        Assert.DoesNotContain("StringBuilder", log.ToString());
    }

    [Fact]
    public void AnEmptyBaseline_ToleratesNothing_EveryEdgeIntoCoreIsNew()
    {
        var baseline = DynamicEdgeBaseline.Parse(["# nothing tolerated"]);
        var ratchet = DynamicEdgeRatchet.Evaluate(Maps(), baseline);
        Assert.False(ratchet.Success);
        Assert.Equal(
            [("App/Maps", DependencyEdgeKinds.Dynamic, HomeAppSource), ("App/Maps", DependencyEdgeKinds.DynamicUnresolved, "?System.Type.GetType")],
            ratchet.New.ToArray());
        var log = new StringWriter();
        ratchet.Write(log, baseline, null);
        Assert.Contains("dynamic-edge ratchet — ENFORCED against 0 baseline entr(ies): 2 NEW", log.ToString());
    }

    [Fact]
    public void AListedEdge_IsKnown_AndAListedEdgeTheSourceNoLongerCarries_IsStale()
    {
        var baseline = DynamicEdgeBaseline.Parse([
            $"App/Maps Dynamic {HomeAppSource}   # the AmapsConnect shape",
            "App/Maps DynamicUnresolved ?System.Type.GetType",
        ]);
        Assert.True(DynamicEdgeRatchet.Evaluate(Maps(), baseline).Success);

        var withGone = DynamicEdgeBaseline.Parse([.. baseline.Entries.Select(e => e.ToString()), "App/Maps Dynamic *, MeshWeaver.Layout"]);
        var stale = DynamicEdgeRatchet.Evaluate(Maps(), withGone);
        Assert.False(stale.Success);
        Assert.Equal("App/Maps Dynamic *, MeshWeaver.Layout", Assert.Single(stale.Stale).ToString());
    }

    [Fact]
    public void AnEntryWhoseSourceWasNotExtracted_IsUnverifiable_NeverStale()
    {
        var baseline = DynamicEdgeBaseline.Parse([
            $"App/Maps Dynamic {HomeAppSource}",
            "App/Maps DynamicUnresolved ?System.Type.GetType",
            "App/Broken Dynamic *, MeshWeaver.Graph",
        ]);
        var ratchet = DynamicEdgeRatchet.Evaluate(Maps() with { Failed = ["App/Broken"] }, baseline);
        Assert.True(ratchet.Success);
        Assert.Equal("App/Broken", Assert.Single(ratchet.Unverifiable).Source);
    }

    [Fact]
    public void AMalformedBaseline_IsRefused()
    {
        Assert.Throws<FormatException>(() => DynamicEdgeBaseline.Parse(["App/Maps Platform MeshWeaver.Graph"]));
        Assert.Throws<FormatException>(() => DynamicEdgeBaseline.Parse(["App/Maps Dynamic"]));
        Assert.Throws<FileNotFoundException>(() => DynamicEdgeBaseline.Load(Path.Combine(_root, "absent.allow")));
    }

    [Fact]
    public void APackageTree_IsReconciledAgainstItsManifestsAndNuGetDirectives()
    {
        Write("Lib/index.json", """
            {"$type":"MeshNode","id":"Lib","namespace":"","path":"Lib","mainNode":"Lib","name":"Lib","nodeType":"Store/Plugin","state":"Active",
             "content":{"$type":"PluginContent","description":"Lib.","module":"Lib.Module","requires":[]}}
            """);
        Write("App/index.json", """
            {"$type":"MeshNode","id":"App","namespace":"","path":"App","mainNode":"App","name":"App","nodeType":"Store/Plugin","state":"Active",
             "content":{"$type":"PluginContent","description":"App.","requires":["Lib@^1.0.0","Elsewhere@^2.0.0"]}}
            """);
        Write("App/Maps/Source/Connect.cs", """
            #r "nuget:Acme.Declared, 1.0.0"
            public static class Connect { }
            """);
        var document = Document([],
            Set("App/Maps",
                Edge(DependencyEdgeKinds.NodeType, "Lib/Thing"),
                Edge(DependencyEdgeKinds.Module, "Lib.Module"),
                Edge(DependencyEdgeKinds.Module, "Other.Module"),
                Edge(DependencyEdgeKinds.Platform, "MeshWeaver.Graph"),
                Edge(DependencyEdgeKinds.Package, "acme.undeclared"),
                Edge(DependencyEdgeKinds.Dynamic, HomeAppSource, DependencyEdgeKinds.Platform)),
            Set("Lib/Thing"));

        var report = DependencyReconciliation.Reconcile(document, _root);

        var app = report.Units.Single(u => u.Unit == "App");
        Assert.Equal("package", app.Shape);
        // Used but undeclared: a module no required package declares, and a package no directive names.
        Assert.Equal(["Module Other.Module", "Package acme.undeclared"],
            app.UsedButUndeclared.Select(f => $"{f.Kind} {f.Target}").ToArray());
        // Declared but unused: the #r directive nothing binds. Lib IS used (NodeType + module edge).
        Assert.Equal(["nuget Acme.Declared"], app.DeclaredButUnused.Select(f => $"{f.Kind} {f.Target}").ToArray());
        // A requirement outside the tree cannot be judged — said, not guessed.
        Assert.Contains(app.Unverifiable, u => u.StartsWith("requires Elsewhere", StringComparison.Ordinal));
        Assert.Equal(HomeAppSource, Assert.Single(app.DynamicIntoCore).Target);
        Assert.Equal(1, app.Platform["MeshWeaver.Graph"]);

        // Lib declares a module no Lib source compiles against.
        var lib = report.Units.Single(u => u.Unit == "Lib");
        Assert.Equal(["module Lib.Module"], lib.DeclaredButUnused.Select(f => $"{f.Kind} {f.Target}").ToArray());
        Assert.Empty(lib.UsedButUndeclared);
    }

    [Fact]
    public void AModuleWorkspace_IsReconciledAgainstItsProjectFiles()
    {
        Write("Used/Used.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("Unused/Unused.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../Used/Used.csproj" />
                <ProjectReference Include="../Unused/Unused.csproj" />
                <PackageReference Include="MeshWeaver.Graph" />
                <PackageReference Include="MeshWeaver.Layout" />
              </ItemGroup>
            </Project>
            """);
        var document = Document([],
            Set("App",
                Edge(DependencyEdgeKinds.Module, "Used"),
                Edge(DependencyEdgeKinds.Platform, "MeshWeaver.Graph"),
                Edge(DependencyEdgeKinds.Platform, "MeshWeaver.Mesh.Contract")));

        var app = Assert.Single(DependencyReconciliation.Reconcile(document, _root).Units);
        Assert.Equal("module", app.Shape);
        Assert.Equal(["Platform MeshWeaver.Mesh.Contract"],
            app.UsedButUndeclared.Select(f => $"{f.Kind} {f.Target}").ToArray());
        Assert.Equal(["PackageReference MeshWeaver.Layout", "ProjectReference Unused"],
            app.DeclaredButUnused.Select(f => $"{f.Kind} {f.Target}").ToArray());
    }

    [Fact]
    public void TheVerb_WritesTheReport_AndFailsOnlyOnTheRatchet()
    {
        var edges = Path.Combine(_root, "out");
        DependencyEdgesFile.WriteDocument(edges, Maps());
        var baselinePath = Write("dynamic.allow", "");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Assert.Equal(0, DependencyReconciliation.Run([edges], stdout, stderr));
        Assert.True(File.Exists(Path.Combine(edges, DependencyReconciliation.FileName)));
        Assert.Contains("OBSERVE-ONLY", stdout.ToString());

        Assert.Equal(1, DependencyReconciliation.Run([edges, "--dynamic-baseline", baselinePath], stdout, stderr));
        Assert.Equal(2, DependencyReconciliation.Run([edges, "--dynamic-baseline", Path.Combine(_root, "absent")], stdout, stderr));
        Assert.Contains("does not exist", stderr.ToString());
    }
}
