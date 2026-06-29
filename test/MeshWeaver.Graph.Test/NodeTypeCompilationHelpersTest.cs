using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for <see cref="NodeTypeCompilationHelpers.HasUsableBuild"/> — the
/// verify-before-skip gate that decides whether the per-NodeType compile
/// kickoff may skip a (re)compile.
///
/// <para>Regression cover for the "stale Ok" class of bug:
/// <c>CompilationStatus</c>, <c>LatestAssemblyCollection</c>,
/// <c>LatestAssemblyPath</c> and <c>CompiledFrameworkVersion</c> are all
/// persisted into the NodeType MeshNode's JSON, so a <c>CompilationStatus = Ok</c>
/// can outlive — and lie about — the assembly that produced it:</para>
/// <list type="bullet">
///   <item>seed-data pollution: a prior run stamps <c>Ok</c> into sample data
///     that a later run (or CI checkout) reads back;</item>
///   <item>cleaned-up caches: the assembly the <c>Ok</c> points at has since
///     been deleted (the store miss is caught at activation time, not here —
///     the metadata-only kickoff predicate prefers a redundant compile over a
///     blocking store round-trip on every stream emission);</item>
///   <item>framework redeploy: MeshWeaver ships a new version and the cached
///     DLL is now ABI-stale.</item>
/// </list>
/// In every case the kickoff must NOT trust the bare <c>Ok</c> — it must
/// recompile. <see cref="NodeTypeCompilationHelpers.HasUsableBuild"/> returns
/// <c>true</c> only when all four metadata fields agree.
/// </summary>
public class NodeTypeCompilationHelpersTest
{
    private static MeshNode TypeNode(NodeTypeDefinition def) =>
        new("MyType", "type")
        {
            NodeType = MeshNode.NodeTypePath,
            Content = def,
        };

    [Fact]
    public void HasUsableBuild_Ok_LatestAssemblyPopulated_FrameworkMatches_IsTrue()
    {
        var def = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "nodetype-cache",
            LatestAssemblyPath = "type/MyType/v1.dll",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def), def)
            .Should().BeTrue("Ok + LatestAssembly{Collection,Path} populated + current framework = the build is genuinely usable");
    }

    [Fact]
    public void HasUsableBuild_Ok_LatestAssemblyPathMissing_IsFalse()
    {
        // The "stale Ok" bug: status says Ok, but no LatestAssemblyPath was
        // ever stamped — the producer didn't go through the new upload path.
        // Without a durable reference, the activation chain can't hydrate the
        // bytes from any silo, so this build is not usable.
        var def = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "nodetype-cache",
            LatestAssemblyPath = null,
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def), def)
            .Should().BeFalse("Ok without a durable LatestAssemblyPath must force a recompile, not a skip");
    }

    [Fact]
    public void HasUsableBuild_Ok_LatestAssemblyCollectionMissing_IsFalse()
    {
        var def = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = null,
            LatestAssemblyPath = "type/MyType/v1.dll",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def), def)
            .Should().BeFalse("Ok without a LatestAssemblyCollection cannot dispatch to a store");
    }

    [Fact]
    public void HasUsableBuild_Ok_FrameworkMismatch_IsFalse()
    {
        // The redeploy case: the assembly fields are populated, but the bytes
        // were emitted against a previous MeshWeaver version. Recompile.
        var def = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "nodetype-cache",
            LatestAssemblyPath = "type/MyType/v1.dll",
            CompiledFrameworkVersion = "0.0.0-some-other-build"
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def), def)
            .Should().BeFalse("a release compiled against a different framework version must be rebuilt");
    }

    [Fact]
    public void HasUsableBuild_Ok_FrameworkVersionNull_IsFalse()
    {
        // Pre-feature releases (or hand-authored seed data) carry no
        // CompiledFrameworkVersion — treat as un-verifiable → recompile.
        var def = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "nodetype-cache",
            LatestAssemblyPath = "type/MyType/v1.dll",
            CompiledFrameworkVersion = null
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def), def)
            .Should().BeFalse("a release with no recorded framework version cannot be proven current");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CompilationStatus.Unknown)]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    [InlineData(CompilationStatus.Ok)]
    [InlineData(CompilationStatus.Error)]
    public void HasUsableBuild_AssemblyFieldsPopulated_IsTrue_RegardlessOfStatus(CompilationStatus? status)
    {
        // Design (6e909188f): HasUsableBuild ignores CompilationStatus.
        // LatestAssembly{Collection,Path} + CompiledFrameworkVersion are
        // ONLY stamped by a successful compile write-back, so all three
        // matching the current framework means the bytes referenced are
        // reusable — even if a subsequent compile failed (ALC lock during
        // cross-test re-write) and left Status=Error in the persisted JSON.
        // Activation hydrates via IAssemblyStore; a true store miss is
        // self-healed there, not here.
        var def = new NodeTypeDefinition
        {
            CompilationStatus = status,
            LatestAssemblyCollection = "nodetype-cache",
            LatestAssemblyPath = "type/MyType/v1.dll",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def), def)
            .Should().BeTrue($"all assembly fields populated + framework match → usable (status {status?.ToString() ?? "(null)"} ignored by design)");
    }

    [Fact]
    public void FrameworkVersion_IsStableWithinProcess()
    {
        // The kickoff stamps this on compile and compares it on the next cold
        // start; it must be deterministic for the lifetime of the process.
        NodeTypeCompilationHelpers.FrameworkVersion
            .Should().NotBeNullOrWhiteSpace()
            .And.Be(NodeTypeCompilationHelpers.FrameworkVersion);
    }
}
