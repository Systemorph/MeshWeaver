using System.Collections.Immutable;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins what invalidates the language service's per-NodeType workspace cache.
///
/// <para>🚨 <b>Source versions alone are not the identity of a compilation.</b> The cache reused a
/// workspace whenever <c>SourceVersions</c> matched, so a NodeType whose sources had not changed but
/// whose REFERENCE set had went on answering diagnostics computed against the old references —
/// permanently, since nothing else could evict it. On memex-cloud that surfaced as a byte-identical
/// 403,625-byte <c>Error</c> payload nine minutes apart for a node whose own
/// <c>compilationStatus</c> was <c>Ok</c> (#3396): a false positive from the tool AGENTS.md points
/// at for the mandated pre-deploy sweep. The dangerous direction is the other one — the same
/// staleness can answer clean for a type that has since broken.</para>
///
/// <para>The reference set really does move under unchanged sources: #3395 measured two NodeType
/// compiles in ONE boot resolving different module sets 15 seconds apart.</para>
/// </summary>
public class DiagnosticsWorkspaceCacheKeyTest
{
    private static readonly MetadataReference CoreLib =
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

    private static readonly MetadataReference Linq =
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location);

    private static CompilationInputs Inputs(
        ImmutableArray<MetadataReference>? references = null,
        string skeleton = "// skeleton",
        string usings = "global using System;",
        string assemblyName = "Widget.Thing",
        ImmutableArray<(string Path, string Code)>? sources = null,
        ImmutableDictionary<string, long>? versions = null)
        => new(
            AssemblyName: assemblyName,
            Sources: sources ?? [("Thing.cs", "public class Thing { }")],
            SkeletonSource: skeleton,
            GlobalUsingsSource: usings,
            References: references ?? [CoreLib],
            ParseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            CompilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            SourceVersions: versions ?? ImmutableDictionary<string, long>.Empty.Add("Thing.cs", 1));

    // ───────────────────────────── the reuse DECISION, not just the key ─────────────────────────

    /// <summary>
    /// 🚨 The assertion that actually fails if the fix is undone. A cache entry built from one
    /// reference set must NOT be reused for inputs carrying another, even though every source and
    /// every source version is identical — which is #3396 exactly.
    /// </summary>
    [Fact]
    public void ACachedWorkspace_IsNotReused_WhenOnlyTheReferenceSetChanged()
    {
        var built = Inputs(references: [CoreLib]);
        var now = Inputs(references: [CoreLib, Linq]);

        Assert.Equal(built.SourceVersions, now.SourceVersions);   // the sources did not move…
        Assert.False(MeshNodeLanguageService.CanReuseWorkspace(
            built.SourceVersions, MeshNodeLanguageService.ShapeKeyOf(built), now));  // …the workspace still must not be reused
    }

    /// <summary>The other half: unchanged inputs DO reuse, or every keystroke rebuilds Roslyn.</summary>
    [Fact]
    public void ACachedWorkspace_IsReused_WhenNothingMoved()
    {
        var built = Inputs();
        Assert.True(MeshNodeLanguageService.CanReuseWorkspace(
            built.SourceVersions, MeshNodeLanguageService.ShapeKeyOf(built), Inputs()));
    }

    /// <summary>And the original half still holds: a source EDIT invalidates via SourceVersions.</summary>
    [Fact]
    public void ACachedWorkspace_IsNotReused_WhenASourceVersionMoved()
    {
        var built = Inputs();
        var now = Inputs(versions: ImmutableDictionary<string, long>.Empty.Add("Thing.cs", 2));

        Assert.Equal(MeshNodeLanguageService.ShapeKeyOf(built), MeshNodeLanguageService.ShapeKeyOf(now));
        Assert.False(MeshNodeLanguageService.CanReuseWorkspace(
            built.SourceVersions, MeshNodeLanguageService.ShapeKeyOf(built), now));
    }

    /// <summary>Identical inputs reuse the workspace — without this the fix would rebuild Roslyn
    /// state on every hover and completion keystroke.</summary>
    [Fact]
    public void IdenticalInputs_ShareAShapeKey()
        => Assert.Equal(
            MeshNodeLanguageService.ShapeKeyOf(Inputs()),
            MeshNodeLanguageService.ShapeKeyOf(Inputs()));

    /// <summary>🚨 The defect: same sources, different references, and the cache could not tell.</summary>
    [Fact]
    public void AChangedReferenceSet_ChangesTheShapeKey()
        => Assert.NotEqual(
            MeshNodeLanguageService.ShapeKeyOf(Inputs(references: [CoreLib])),
            MeshNodeLanguageService.ShapeKeyOf(Inputs(references: [CoreLib, Linq])));

    /// <summary>Order is part of the identity — a reorder can change how a name resolves.</summary>
    [Fact]
    public void AReorderedReferenceSet_ChangesTheShapeKey()
        => Assert.NotEqual(
            MeshNodeLanguageService.ShapeKeyOf(Inputs(references: [CoreLib, Linq])),
            MeshNodeLanguageService.ShapeKeyOf(Inputs(references: [Linq, CoreLib])));

    [Fact]
    public void AChangedSkeleton_ChangesTheShapeKey()
        => Assert.NotEqual(
            MeshNodeLanguageService.ShapeKeyOf(Inputs(skeleton: "// skeleton")),
            MeshNodeLanguageService.ShapeKeyOf(Inputs(skeleton: "// skeleton v2")));

    [Fact]
    public void ChangedGlobalUsings_ChangeTheShapeKey()
        => Assert.NotEqual(
            MeshNodeLanguageService.ShapeKeyOf(Inputs(usings: "global using System;")),
            MeshNodeLanguageService.ShapeKeyOf(Inputs(usings: "global using System;\nglobal using System.Linq;")));

    [Fact]
    public void AChangedAssemblyName_ChangesTheShapeKey()
        => Assert.NotEqual(
            MeshNodeLanguageService.ShapeKeyOf(Inputs(assemblyName: "Widget.Thing")),
            MeshNodeLanguageService.ShapeKeyOf(Inputs(assemblyName: "Widget.Other")));

    /// <summary>
    /// 🚨 The non-vacuity half. The shape key must deliberately IGNORE the source texts, because
    /// <c>SourceVersions</c> is what carries their identity and the two are checked together. A key
    /// that hashed everything would pass every assertion above while telling us nothing about which
    /// half covers what — and would rebuild the workspace on each keystroke, since the edited buffer
    /// changes before its version does.
    /// </summary>
    [Fact]
    public void ChangedSourceTEXT_DoesNotChangeTheShapeKey_ThatIsSourceVersionsJob()
        => Assert.Equal(
            MeshNodeLanguageService.ShapeKeyOf(Inputs(sources: [("Thing.cs", "public class Thing { }")])),
            MeshNodeLanguageService.ShapeKeyOf(Inputs(sources: [("Thing.cs", "public class Thing { int x; }")])));
}
