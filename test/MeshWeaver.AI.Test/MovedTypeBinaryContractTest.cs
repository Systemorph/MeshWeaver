using System;
using System.Linq;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The binary contract <c>MeshWeaver.AI</c> owes every module that was compiled against an earlier
/// platform — pinned as a test because no repo-local build can see it.
///
/// <para><b>What went wrong (#2370).</b> #2283 moved <c>MeshOperations</c>,
/// <c>MeshExportManifest</c>, <c>MeshExportFileEntry</c> and <c>NodeReadOutcome</c> out of
/// <c>MeshWeaver.AI</c> into <c>MeshWeaver.Mesh.Operations</c>, renaming their namespace on the way.
/// It was verified by BUILDING the plugins repo's <c>MeshWeaver.Mcp</c> against the branch — which
/// answers "does the module's SOURCE still compile", a different question from the one that
/// mattered. The module that was already published holds
/// <c>TypeRef MeshWeaver.AI.MeshOperations</c> scoped to the <c>MeshWeaver.AI</c> AssemblyRef, and
/// the MCP SDK constructs its tool target per invocation, so when the platform rolled, EVERY MCP
/// tool call — <c>get</c>, <c>search</c>, <c>create</c>, … — died in the <c>McpMeshPlugin</c>
/// constructor with:
/// <code>
/// System.TypeLoadException: Could not load type 'MeshWeaver.AI.MeshOperations'
///     from assembly 'MeshWeaver.AI, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null'.
/// </code>
/// The whole <c>/mcp</c> surface, for every external client of the deployment.
/// </para>
///
/// <para><b>Why a test and not more care.</b> The module lane's gate is a SEMVER FLOOR, never MVID
/// equality, precisely so "a landed module keeps loading across ordinary platform updates"
/// (Doc/Architecture/Modules). That is a promise about BINARY compatibility, and a semver floor
/// cannot see a type-level break — nor can <c>landed-modules-gate</c>, which compiles the plugins
/// repo's module SOURCE against the PR and therefore goes green on exactly this change.</para>
///
/// <para><b>What this pins.</b> Each moved type must still resolve <i>through</i>
/// <c>MeshWeaver.AI</c> under its ORIGINAL full name, and must resolve to the type that now lives
/// in <c>MeshWeaver.Mesh.Operations</c> — i.e. a real <c>[TypeForwardedTo]</c> yielding ONE type
/// identity, not a shim duplicating the surface. A forwarder cannot rename, so the full name
/// (namespace included) is frozen: this test is what fails if someone tidies
/// <c>namespace MeshWeaver.AI</c> in <c>MeshWeaver.Mesh.Operations</c>.</para>
/// </summary>
public class MovedTypeBinaryContractTest
{
    /// <summary>The assembly a module's TypeRef names — the simple name it binds by at runtime.</summary>
    private const string BoundAssembly = "MeshWeaver.AI";

    /// <summary>Where the types actually live since #2283.</summary>
    private const string HostingAssembly = "MeshWeaver.Mesh.Operations";

    /// <summary>
    /// The frozen names. A module compiled before #2283 holds exactly these strings in its
    /// metadata; changing one is a break no consumer's compiler will ever warn about.
    /// </summary>
    private static readonly string[] FrozenNames =
    [
        "MeshWeaver.AI.MeshOperations",
        "MeshWeaver.AI.MeshExportManifest",
        "MeshWeaver.AI.MeshExportFileEntry",
        "MeshWeaver.AI.NodeReadOutcome",
    ];

    /// <summary>The same list, as theory rows.</summary>
    public static TheoryData<string> ForwardedTypeNames() => [.. FrozenNames];

    [Theory]
    [MemberData(nameof(ForwardedTypeNames))]
    public void OldName_StillResolves_ThroughTheAssemblyModulesBind(string fullName)
    {
        // Assembly-qualified on purpose: this is the resolution a module's TypeRef performs —
        // "find `fullName` in the assembly named `MeshWeaver.AI`" — and it is the exact step that
        // threw TypeLoadException in production. An unqualified Type.GetType would search only the
        // calling assembly and could never observe the forwarder either way.
        var resolved = Type.GetType($"{fullName}, {BoundAssembly}", throwOnError: false);

        Assert.True(
            resolved is not null,
            $"'{fullName}' no longer resolves from assembly '{BoundAssembly}'. Every module compiled "
            + "before it moved binds that exact name and will die with TypeLoadException on the next "
            + "platform roll — the #2370 outage. Restore the name and its [assembly: TypeForwardedTo] "
            + "in src/MeshWeaver.AI/TypeForwards.cs.");

        Assert.Equal(HostingAssembly, resolved!.Assembly.GetName().Name);
    }

    [Fact]
    public void TheForwardersAreForwarders_NotDuplicatedTypes()
    {
        // A shim class left behind in MeshWeaver.AI would satisfy the resolution above while
        // minting a SECOND type identity — the trap-door AGENTS.md names for `as`/`is`. Reading the
        // ExportedType table proves the CLR is redirecting, not that we re-declared the surface.
        var forwarded = typeof(MeshPlugin).Assembly
            .GetForwardedTypes()
            .Select(t => t.FullName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(FrozenNames, name => Assert.Contains(name, forwarded));
    }

    [Fact]
    public void TheMovedTypes_KeepTheirOriginalFullNames()
    {
        // The same pin from the other side: `typeof` is compiled here, so this fails at BUILD time
        // in any tree where the namespace was changed but the forwarder file was updated to match —
        // the shape that would otherwise make the theory above pass while the contract is gone.
        Assert.Equal("MeshWeaver.AI.MeshOperations", typeof(MeshOperations).FullName);
        Assert.Equal("MeshWeaver.AI.MeshExportManifest", typeof(MeshExportManifest).FullName);
        Assert.Equal("MeshWeaver.AI.MeshExportFileEntry", typeof(MeshExportFileEntry).FullName);
        Assert.Equal("MeshWeaver.AI.NodeReadOutcome", typeof(MeshWeaver.AI.NodeReadOutcome).FullName);

        Assert.Equal(HostingAssembly, typeof(MeshOperations).Assembly.GetName().Name);
    }
}
