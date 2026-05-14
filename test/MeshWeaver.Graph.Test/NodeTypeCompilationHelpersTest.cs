using System;
using System.IO;
using FluentAssertions;
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
/// <para>Regression cover for the "stale Ok" class of bug: <c>CompilationStatus</c>,
/// <c>MeshNode.AssemblyLocation</c> and <c>CompiledFrameworkVersion</c> are all
/// persisted into the NodeType MeshNode's JSON, so a <c>CompilationStatus = Ok</c>
/// can outlive — and lie about — the assembly that produced it:</para>
/// <list type="bullet">
///   <item>seed-data pollution: a prior run stamps <c>Ok</c> into sample data
///     that a later run (or CI checkout) reads back;</item>
///   <item>cleaned-up caches: the temp / <c>.mesh-cache</c> DLL the <c>Ok</c>
///     points at has since been deleted;</item>
///   <item>framework redeploy: MeshWeaver ships a new version and the cached
///     DLL is now ABI-stale.</item>
/// </list>
/// In every case the kickoff must NOT trust the bare <c>Ok</c> — it must
/// recompile. <see cref="NodeTypeCompilationHelpers.HasUsableBuild"/> returns
/// <c>true</c> only when the build is genuinely usable.
/// </summary>
public class NodeTypeCompilationHelpersTest
{
    // A real, guaranteed-present file to stand in for a compiled assembly.
    private static readonly string ExistingAssembly =
        typeof(NodeTypeCompilationHelpersTest).Assembly.Location;

    private static readonly string MissingAssembly =
        Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.dll");

    private static MeshNode TypeNode(NodeTypeDefinition def, string? assemblyLocation) =>
        new("MyType", "type")
        {
            NodeType = MeshNode.NodeTypePath,
            Content = def,
            AssemblyLocation = assemblyLocation
        };

    [Fact]
    public void HasUsableBuild_Ok_AssemblyPresent_FrameworkMatches_IsTrue()
    {
        var def = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def, ExistingAssembly), def)
            .Should().BeTrue("Ok + assembly on disk + current framework = the build is genuinely usable");
    }

    [Fact]
    public void HasUsableBuild_Ok_AssemblyMissing_IsFalse()
    {
        // The "stale Ok" bug: status says Ok, but the DLL it points at is gone
        // (cleaned-up temp cache, or seed pollution from another machine).
        var def = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def, MissingAssembly), def)
            .Should().BeFalse("a missing assembly must force a recompile, not a skip");
    }

    [Fact]
    public void HasUsableBuild_Ok_AssemblyLocationEmpty_IsFalse()
    {
        var def = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def, assemblyLocation: null), def)
            .Should().BeFalse("Ok with no assembly path is not a usable build");
    }

    [Fact]
    public void HasUsableBuild_Ok_AssemblyPresent_FrameworkMismatch_IsFalse()
    {
        // The redeploy case: the assembly is on disk, but it was compiled
        // against a previous MeshWeaver version. The cached DLL bound against
        // framework assemblies that have since changed — recompile.
        var def = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            CompiledFrameworkVersion = "0.0.0-some-other-build"
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def, ExistingAssembly), def)
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
            CompiledFrameworkVersion = null
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def, ExistingAssembly), def)
            .Should().BeFalse("a release with no recorded framework version cannot be proven current");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CompilationStatus.Unknown)]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    [InlineData(CompilationStatus.Error)]
    public void HasUsableBuild_NonOkStatus_IsFalse(CompilationStatus? status)
    {
        // Only Ok can possibly be a usable build. Everything else — never
        // compiled, queued, mid-compile (interrupted on a prior process),
        // or failed — must (re)compile. Note even a present assembly +
        // matching framework cannot rescue a non-Ok status.
        var def = new NodeTypeDefinition
        {
            CompilationStatus = status,
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion
        };
        NodeTypeCompilationHelpers.HasUsableBuild(TypeNode(def, ExistingAssembly), def)
            .Should().BeFalse($"status {status?.ToString() ?? "(null)"} is not a usable build");
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
