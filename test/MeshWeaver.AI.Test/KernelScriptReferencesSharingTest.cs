using System;
using System.Linq;
using MeshWeaver.Kernel.Hub;
using Microsoft.CodeAnalysis;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 Regression guard for the kernel-script metadata leak (~200 MiB of native
/// memory per kernel session, the dominant CI memory-pressure driver).
///
/// <para>The leak mechanism: Roslyn materializes a native metadata block per
/// <c>PortableExecutableReference</c>. Anything that re-creates references per
/// session — passing raw <see cref="System.Reflection.Assembly"/> objects to
/// <c>ScriptOptions.WithReferences</c>, or letting
/// <c>RuntimeMetadataReferenceResolver.ResolveMissingAssembly</c> eagerly
/// materialize the globals type's transitive closure — multiplies that cost by
/// every session and nothing reclaims it (Roslyn's script LoadContext is
/// non-collectible). The fix is instance sharing: ONE
/// <c>PortableExecutableReference</c> per assembly file per process
/// (<see cref="KernelScriptReferences"/>) and a resolver that resolves identities
/// to those shared instances WITHOUT consulting the materializing inner resolver
/// (<see cref="SharedScriptMetadataResolver"/>).</para>
///
/// <para>These tests pin the sharing mechanism itself — reference-equality across
/// calls — which is exactly the property whose loss re-opens the leak.</para>
/// </summary>
public class KernelScriptReferencesSharingTest
{
    [Fact]
    public void GetReferences_ReturnsSameInstances_AcrossSessions()
    {
        var sessionAssemblies = new[] { typeof(KernelScriptReferencesSharingTest).Assembly };

        var first = KernelScriptReferences.GetReferences(sessionAssemblies);
        var second = KernelScriptReferences.GetReferences(sessionAssemblies);

        first.Length.Should().Be(second.Length);
        first.Length.Should().BeGreaterThan(10, "the shared snapshot covers the loaded assembly set");
        for (var i = 0; i < first.Length; i++)
            ReferenceEquals(first[i], second[i]).Should().BeTrue(
                $"reference #{i} must be the SAME materialization across sessions — a fresh instance means a fresh ~native metadata block per kernel session");
    }

    [Fact]
    public void ResolveMissingAssembly_ReturnsSharedInstance_WithoutRematerializing()
    {
        // Use a definitely-loaded assembly's identity — the missing-assembly closure
        // of MeshScriptGlobals is loaded by definition.
        var loaded = typeof(System.Reactive.Linq.Observable).Assembly;
        var identity = AssemblyIdentity.FromAssemblyDefinition(loaded);

        // definition: any shared reference works as the "referencing assembly".
        var definition = KernelScriptReferences.GetReferences([loaded]).OfType<PortableExecutableReference>().First();

        var first = SharedScriptMetadataResolver.Instance.ResolveMissingAssembly(definition, identity);
        var second = SharedScriptMetadataResolver.Instance.ResolveMissingAssembly(definition, identity);

        first.Should().NotBeNull("a loaded assembly must resolve");
        ReferenceEquals(first, second).Should().BeTrue(
            "ResolveMissingAssembly must return the process-shared instance — Roslyn's own resolver eagerly " +
            "materializes a fresh native metadata block per call, which was the per-session leak");
        first!.FilePath.Should().Be(loaded.Location);
    }
}
