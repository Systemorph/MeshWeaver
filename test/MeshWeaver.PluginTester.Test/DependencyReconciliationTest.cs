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
        new(DependencyEdgesFile.Schema, "c003e001", "abc", [.. sets], failed) { Measured = DependencyEdgesFile.Measures.Bake };

    /// <summary>The same document as a producer that predates the measurement capabilities wrote it.</summary>
    private static DependencyEdgesFile.Document Unmeasured(DependencyEdgesFile.Document document) =>
        document with { Measured = [] };

    private const string LibIndex = """
        {"$type":"MeshNode","id":"Lib","namespace":"","path":"Lib","mainNode":"Lib","name":"Lib","nodeType":"Store/Plugin","state":"Active",
         "content":{"$type":"PluginContent","description":"Lib.","module":"Lib.Module","requires":[]}}
        """;

    private const string AppIndex = """
        {"$type":"MeshNode","id":"App","namespace":"","path":"App","mainNode":"App","name":"App","nodeType":"Store/Plugin","state":"Active",
         "content":{"$type":"PluginContent","description":"App.","requires":["Lib@^1.0.0"]}}
        """;

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
                Edge(DependencyEdgeKinds.Package, "package:acme.undeclared"),
                Edge(DependencyEdgeKinds.Dynamic, HomeAppSource, DependencyEdgeKinds.Platform)),
            Set("Lib/Thing"));

        var report = DependencyReconciliation.Reconcile(document, _root);

        var app = report.Units.Single(u => u.Unit == "App");
        Assert.Equal("package", app.Shape);
        // Used but undeclared: a module no required package declares, and a package no directive names.
        Assert.Equal(["Module Other.Module", "Package package:acme.undeclared"],
            app.UsedButUndeclared.Select(f => $"{f.Kind} {f.Target}").ToArray());
        // THE RULE: the #r directive nothing binds is NOT "unused" — a package can be used through host
        // unification or as a source generator, neither of which a compile edge shows.
        Assert.Empty(app.DeclaredButUnused);
        Assert.Contains(app.Unverifiable, u => u.StartsWith("nuget Acme.Declared — not judged: its use through assemblies the host supplies", StringComparison.Ordinal));
        // A requirement outside the tree cannot be judged — said, not guessed.
        Assert.Contains(app.Unverifiable, u => u.StartsWith("requires Elsewhere", StringComparison.Ordinal));
        Assert.Equal(HomeAppSource, Assert.Single(app.DynamicIntoCore).Target);
        Assert.Equal(1, app.Platform["MeshWeaver.Graph"]);

        // Lib declares a module no Lib source compiles against — and a module is HOSTED at run time,
        // which no compile sees, so it is unverifiable, never "unused".
        var lib = report.Units.Single(u => u.Unit == "Lib");
        Assert.Empty(lib.DeclaredButUnused);
        Assert.Contains(lib.Unverifiable, u => u.StartsWith("module Lib.Module — not judged: its use through runtime hosting", StringComparison.Ordinal));
        Assert.Empty(lib.UsedButUndeclared);
    }

    /// <summary>
    /// A module's declarations come from the PRODUCING build's evaluated model, carried in the edges
    /// file — never raw csproj XML re-read here (which would miss imports and conditions). A used
    /// ProjectReference outside the source root resolves from the host and shows up as a PLATFORM
    /// edge; it is a use, not "unused" (negative control: a ProjectReference with no edge at all).
    /// </summary>
    [Fact]
    public void AModuleWorkspace_IsReconciledAgainstTheProducersEvaluatedDeclarations()
    {
        var document = Document([],
            Set("App",
                Edge(DependencyEdgeKinds.Module, "Used"),
                Edge(DependencyEdgeKinds.Platform, "MeshWeaver.Graph"),
                Edge(DependencyEdgeKinds.Platform, "MeshWeaver.Hosted"),
                Edge(DependencyEdgeKinds.Platform, "MeshWeaver.Mesh.Contract"),
                // A MeshWeaver.* package the module-libraries SHELF supplied is a Package edge.
                Edge(DependencyEdgeKinds.Package, "package:MeshWeaver.Shelved"))) with
        {
            Measured = DependencyEdgesFile.Measures.ModuleBuild,
            Declarations = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [
                new KeyValuePair<string, DependencyEdgesFile.DeclaredDependencies>("App",
                    new(["MeshWeaver.Hosted", "Unused", "Used"], ["MeshWeaver.Graph", "MeshWeaver.Layout", "MeshWeaver.Shelved"])),
            ]),
        };

        var app = Assert.Single(DependencyReconciliation.Reconcile(document, treeRoot: null).Units);
        Assert.Equal("module", app.Shape);
        Assert.Equal(["Platform MeshWeaver.Mesh.Contract"],
            app.UsedButUndeclared.Select(f => $"{f.Kind} {f.Target}").ToArray());
        // A ProjectReference is a compile reference whose every use channel was measured (compiled
        // code, and by-name loading — this project has no dynamic edge), so it CAN be proven unused.
        Assert.Equal(["ProjectReference Unused"], app.DeclaredButUnused.Select(f => $"{f.Kind} {f.Target}").ToArray());
        // A PackageReference may ship analyzers or generators — never provably unused.
        Assert.Equal(["PackageReference MeshWeaver.Layout — not judged: its use through analyzers and source generators the package ships is not measured"],
            app.Unverifiable.ToArray());
        // The shelf package's Package edge IS its use (not "declared but unused").
        Assert.DoesNotContain(app.Unverifiable, u => u.Contains("MeshWeaver.Shelved", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE RULE, from the other side: a declaration is "unused" only when EVERY way it can be used
    /// was measured. The very ProjectReference proven unused above becomes UNVERIFIABLE the moment
    /// the project carries a dynamic edge — a by-name load could be its use — and when the producer
    /// did not measure dynamic bindings at all.
    /// </summary>
    [Fact]
    public void AProjectReference_IsProvablyUnused_OnlyWhenEveryUseChannelWasMeasured()
    {
        DependencyEdgesFile.Document Doc(ImmutableArray<string> measured, params DependencyEdge[] edges) =>
            Document([], Set("App", edges)) with
            {
                Measured = measured,
                Declarations = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [
                    new KeyValuePair<string, DependencyEdgesFile.DeclaredDependencies>("App", new(["Unused"], [])),
                ]),
            };
        string Verdict(DependencyEdgesFile.Document d)
        {
            var unit = Assert.Single(DependencyReconciliation.Reconcile(d, treeRoot: null).Units);
            return unit.DeclaredButUnused.Any() ? "unused"
                : unit.Unverifiable.Any(u => u.StartsWith("ProjectReference Unused — not judged", StringComparison.Ordinal)) ? "unverifiable"
                : "neither";
        }

        Assert.Equal("unused", Verdict(Doc(DependencyEdgesFile.Measures.ModuleBuild)));
        Assert.Equal("unverifiable", Verdict(Doc(DependencyEdgesFile.Measures.ModuleBuild,
            Edge(DependencyEdgeKinds.DynamicUnresolved, "?System.Reflection.Assembly.Load"))));
        Assert.Equal("unverifiable", Verdict(Doc([DependencyEdgesFile.Measures.ReferencedAssemblies, DependencyEdgesFile.Measures.Declarations])));
    }

    /// <summary>A file that carries no evaluated declarations leaves every declaration-level flag
    /// UNVERIFIABLE — never a clean "nothing undeclared, nothing unused".</summary>
    [Fact]
    public void AModuleWithoutRecordedDeclarations_IsUnverifiable_NotClean()
    {
        var document = Document([], Set("App", Edge(DependencyEdgeKinds.Platform, "MeshWeaver.Mesh.Contract")));
        var app = Assert.Single(DependencyReconciliation.Reconcile(document, treeRoot: null).Units);
        Assert.Empty(app.UsedButUndeclared);
        Assert.Empty(app.DeclaredButUnused);
        Assert.Contains(app.Unverifiable, u => u.StartsWith("declarations not recorded", StringComparison.Ordinal));
    }

    /// <summary>
    /// A schema-1 file written before dynamic bindings were measured has no dynamic edges because
    /// nobody looked: an EMPTY baseline must not pass as ENFORCED, and an entry must not go STALE.
    /// Negative control: the same sets, measured, are judged normally.
    /// </summary>
    [Fact]
    public void AnInputThatNeverMeasuredDynamicBindings_IsUnverifiable_AndEnforcementIsRefused()
    {
        var bare = Unmeasured(Document([], Set("App/Maps")));
        var baseline = DynamicEdgeBaseline.Parse([$"App/Maps Dynamic {HomeAppSource}"]);

        var ratchet = DynamicEdgeRatchet.Evaluate(bare, baseline);
        Assert.False(ratchet.Measured);
        Assert.False(ratchet.Success, "an enforced ratchet over an unmeasured input judged nothing");
        Assert.Empty(ratchet.Stale);
        Assert.Equal($"App/Maps Dynamic {HomeAppSource}", Assert.Single(ratchet.Unverifiable).ToString());
        var empty = DynamicEdgeRatchet.Evaluate(bare, DynamicEdgeBaseline.Parse([]));
        Assert.False(empty.Success, "an EMPTY baseline over an unmeasured input is not a clean pass");
        var log = new StringWriter();
        empty.Write(log, DynamicEdgeBaseline.Parse([]), null);
        Assert.Contains("dynamic-edge ratchet — UNVERIFIABLE", log.ToString());
        Assert.DoesNotContain("ENFORCED", log.ToString());

        // Negative control: measured, the listed edge is gone ⇒ STALE.
        var measured = DynamicEdgeRatchet.Evaluate(Document([], Set("App/Maps")), baseline);
        Assert.True(measured.Measured);
        Assert.Single(measured.Stale);

        // The report says the same thing per unit.
        var report = DependencyReconciliation.Reconcile(bare, treeRoot: null);
        Assert.Contains(Assert.Single(report.Units).Unverifiable, u => u.StartsWith("dynamic bindings were not measured", StringComparison.Ordinal));
    }

    /// <summary>
    /// "Declared but unused" is a claim about EVERY source of a package. When the only user of a
    /// requirement failed extraction, the requirement is UNVERIFIABLE, not unused. Negative control:
    /// with no failed source the same tree reports it unused.
    /// </summary>
    [Fact]
    public void AFailedSource_MakesItsPackagesUnmatchedDeclarationsUnverifiable()
    {
        Write("Lib/index.json", LibIndex);
        Write("App/index.json", AppIndex);
        var withFailure = Document(["App/Broken"], Set("App/Other"), Set("Lib/Thing"));

        var app = DependencyReconciliation.Reconcile(withFailure, _root).Units.Single(u => u.Unit == "App");
        Assert.DoesNotContain(app.DeclaredButUnused, f => f.Kind == "requires");
        Assert.Contains(app.Unverifiable, u => u.StartsWith("requires Lib — not judged: 1 source(s)", StringComparison.Ordinal));
        Assert.Equal(["App/Broken"], app.NotExtracted.ToArray());

        // Negative control: with every source extracted the failed-source reason is gone — the
        // requirement is still unverifiable, now because its RUNTIME use is never measured.
        var complete = DependencyReconciliation.Reconcile(Document([], Set("App/Other"), Set("Lib/Thing")), _root)
            .Units.Single(u => u.Unit == "App");
        Assert.Empty(complete.DeclaredButUnused);
        Assert.Contains(complete.Unverifiable, u => u.StartsWith("requires Lib — not judged: its use through runtime use", StringComparison.Ordinal));
        Assert.DoesNotContain(complete.Unverifiable, u => u.Contains("were not extracted", StringComparison.Ordinal));
    }

    /// <summary>
    /// A JSON Code node stores its code as an ESCAPED string; the directive is read off the decoded
    /// code (<c>NuGetDirectiveParser</c>), so a package it declares and the compile used is not
    /// "used but undeclared". Negative control: the raw JSON text alone holds no directive a parser
    /// would find.
    /// </summary>
    [Fact]
    public void AJsonCodeNodesNuGetDirective_IsReadFromItsDecodedCode()
    {
        Write("App/index.json", AppIndex.Replace("\"Lib@^1.0.0\"", ""));
        const string json = """
            {"$type":"MeshNode","id":"Connect","namespace":"App/Maps/Source","nodeType":"Code",
             "content":{"$type":"CodeConfiguration","language":"csharp","code":"#r \"nuget:Acme.Json, 1.0.0\"\npublic static class Connect { }"}}
            """;
        Write("App/Maps/Source/Connect.json", json);
        var document = Document([], Set("App/Maps", Edge(DependencyEdgeKinds.Package, "package:acme.json")));

        var app = Assert.Single(DependencyReconciliation.Reconcile(document, _root).Units);
        Assert.DoesNotContain(app.UsedButUndeclared, f => f.Kind == DependencyEdgeKinds.Package);
        Assert.Empty(app.DeclaredButUnused);

        Assert.Empty(MeshWeaver.NuGet.NuGetDirectiveParser.Extract(json).References);
        Assert.Equal("Acme.Json", Assert.Single(MeshWeaver.NuGet.NuGetDirectiveParser.Extract(
            DependencyReconciliation.CodeOf("App/Maps/Source/Connect.json", json)).References).Id);
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

        // An unmeasured (pre-capability) file under an armed baseline is RED, never a pass.
        var old = Path.Combine(_root, "old");
        DependencyEdgesFile.WriteDocument(old, Unmeasured(Document([], Set("App/Maps"))));
        Assert.Equal(1, DependencyReconciliation.Run([old, "--dynamic-baseline", baselinePath], stdout, stderr));
        Assert.Contains("does not exist", stderr.ToString());
    }
}
