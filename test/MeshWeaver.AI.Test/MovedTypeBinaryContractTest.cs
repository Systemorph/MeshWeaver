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
/// <c>MeshWeaver.AI</c> under its ORIGINAL full name, and must resolve to the assembly that carries
/// it today — i.e. a real <c>[TypeForwardedTo]</c> yielding ONE type identity, not a shim
/// duplicating the surface. A forwarder cannot rename, so the full name (namespace included) is
/// frozen: this test is what fails if someone tidies <c>namespace MeshWeaver.AI</c> in the assembly
/// that now hosts it.</para>
///
/// <para><b>It happened twice.</b> Replaying <c>scripts/check-type-forwards.py</c> across
/// <c>v3.0.0-rc7 → main</c> found 17 unguarded moves; #2370 fixed four of them, and #2398 fixed the
/// six that #2276 made when it moved the credential-protection and MCP-back-connection contracts
/// into <c>MeshWeaver.Mesh.Contract</c> — three with a proven module consumer on
/// Systemorph/MeshWeaver.Plugins. That is why the frozen list below is a map from name to hosting
/// assembly rather than one constant: the two waves landed in different places.</para>
/// </summary>
public class MovedTypeBinaryContractTest
{
    /// <summary>The assembly a module's TypeRef names — the simple name it binds by at runtime.</summary>
    private const string BoundAssembly = "MeshWeaver.AI";

    /// <summary>
    /// The frozen names, each mapped to the assembly that carries it TODAY. A module compiled
    /// before the type moved holds exactly these strings in its metadata; changing one is a break
    /// no consumer's compiler will ever warn about.
    ///
    /// <para>Two waves, two destinations — which is why this is a map and not one constant:</para>
    /// <list type="bullet">
    ///   <item>#2283 moved the <c>MeshOperations</c> family to <c>MeshWeaver.Mesh.Operations</c>
    ///     (the move that caused #2370).</item>
    ///   <item>#2276 moved the credential-protection and MCP-back-connection contracts to
    ///     <c>MeshWeaver.Mesh.Contract</c>. That one shipped WITHOUT forwarders and was found by
    ///     replaying <c>scripts/check-type-forwards.py</c> across <c>v3.0.0-rc7 → main</c> (#2398);
    ///     three of them have a proven module consumer in Systemorph/MeshWeaver.Plugins.</item>
    /// </list>
    /// </summary>
    private static readonly (string Name, string Assembly)[] Frozen =
    [
        ("MeshWeaver.AI.MeshOperations", "MeshWeaver.Mesh.Operations"),
        ("MeshWeaver.AI.MeshExportManifest", "MeshWeaver.Mesh.Operations"),
        ("MeshWeaver.AI.MeshExportFileEntry", "MeshWeaver.Mesh.Operations"),
        ("MeshWeaver.AI.NodeReadOutcome", "MeshWeaver.Mesh.Operations"),
        ("MeshWeaver.AI.IMasterKeyProvider", "MeshWeaver.Mesh.Contract"),
        ("MeshWeaver.AI.ConfigMasterKeyProvider", "MeshWeaver.Mesh.Contract"),
        ("MeshWeaver.AI.IProviderKeyProtector", "MeshWeaver.Mesh.Contract"),
        ("MeshWeaver.AI.ProviderKeyProtector", "MeshWeaver.Mesh.Contract"),
        ("MeshWeaver.AI.Connect.IMcpBackConnection", "MeshWeaver.Mesh.Contract"),
        ("MeshWeaver.AI.Connect.McpConnectionInfo", "MeshWeaver.Mesh.Contract"),
    ];

    /// <summary>The same list, as theory rows.</summary>
    public static TheoryData<string, string> ForwardedTypeNames()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, assembly) in Frozen)
            data.Add(name, assembly);
        return data;
    }

    [Theory]
    [MemberData(nameof(ForwardedTypeNames))]
    public void OldName_StillResolves_ThroughTheAssemblyModulesBind(string fullName, string hostingAssembly)
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

        Assert.Equal(hostingAssembly, resolved!.Assembly.GetName().Name);
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

        Assert.All(Frozen, f => Assert.Contains(f.Name, forwarded));
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

        // #2276 / #2398 — the SAME pin for the wave that landed in MeshWeaver.Mesh.Contract. These
        // four keep `namespace MeshWeaver.AI` and the pair below keeps `namespace
        // MeshWeaver.AI.Connect`, both inside an assembly named neither of those things. That
        // mismatch is the contract, not an oversight.
        Assert.Equal("MeshWeaver.AI.IMasterKeyProvider", typeof(IMasterKeyProvider).FullName);
        Assert.Equal("MeshWeaver.AI.ConfigMasterKeyProvider", typeof(ConfigMasterKeyProvider).FullName);
        Assert.Equal("MeshWeaver.AI.IProviderKeyProtector", typeof(IProviderKeyProtector).FullName);
        Assert.Equal("MeshWeaver.AI.ProviderKeyProtector", typeof(ProviderKeyProtector).FullName);
        Assert.Equal("MeshWeaver.AI.Connect.IMcpBackConnection", typeof(Connect.IMcpBackConnection).FullName);
        Assert.Equal("MeshWeaver.AI.Connect.McpConnectionInfo", typeof(Connect.McpConnectionInfo).FullName);

        Assert.Equal("MeshWeaver.Mesh.Operations", typeof(MeshOperations).Assembly.GetName().Name);
        Assert.Equal("MeshWeaver.Mesh.Contract", typeof(IProviderKeyProtector).Assembly.GetName().Name);
        Assert.Equal("MeshWeaver.Mesh.Contract", typeof(Connect.IMcpBackConnection).Assembly.GetName().Name);
    }
}
