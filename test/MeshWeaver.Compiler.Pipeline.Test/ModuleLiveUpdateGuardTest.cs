using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Live update is THE default (policy <c>module-live-update-default</c>), so the exception must
/// be DECLARED: a module whose contributions cannot be re-applied in the running process carries
/// <c>[assembly: ModuleRestartRequired("&lt;why&gt;")]</c>, and <see cref="ModuleLiveUpdateGuard"/>
/// fails one that does not. The second case is the guard's NEGATIVE CONTROL — the same module without
/// the declaration must FAIL, naming the module and what was measured; a guard that never fails
/// enforces nothing. The satellite repositories run this guard over every module they ship.
/// </summary>
public sealed class ModuleLiveUpdateGuardTest
{
    [Fact]
    public void AModuleThatOnlyContributesNodes_IsLive_AndNeedsNoDeclaration()
    {
        var contributions = ModuleContributions.Of(Load("MeshWeaver.Test.GuardNodesOnly", ""));
        contributions.LiveUpdateBlockers().Should().BeEmpty();
        ModuleLiveUpdateGuard.Violation("MeshWeaver.Test.GuardNodesOnly", contributions).Should().BeNull();
    }

    [Fact]
    public void AModuleThatBlocksALiveSwap_WithoutTheDeclaration_FailsTheGuard_NamingWhatWasMeasured()
    {
        var contributions = ModuleContributions.Of(Load("MeshWeaver.Test.GuardUndeclared", MeshHubConfiguration));

        var violation = ModuleLiveUpdateGuard.Violation("MeshWeaver.Test.GuardUndeclared", contributions);

        violation.Should().NotBeNull("an undeclared blocker is exactly what the guard exists to refuse");
        violation!.Should().Contain("MeshWeaver.Test.GuardUndeclared")
            .And.Contain("configures the mesh hub")
            .And.Contain("ModuleRestartRequired");
    }

    [Fact]
    public void AModuleThatBlocksALiveSwap_AndDeclaresIt_PassesTheGuard_AndIsNeverSwappedLive()
    {
        var contributions = ModuleContributions.Of(Load("MeshWeaver.Test.GuardDeclared",
            MeshHubConfiguration, "[assembly: MeshWeaver.Mesh.ModuleRestartRequired(\"the mesh hub is configured once\")]"));

        ModuleLiveUpdateGuard.Violation("MeshWeaver.Test.GuardDeclared", contributions).Should().BeNull();
        contributions.LiveUpdateBlockers().Should().Contain(b => b.Contains("the mesh hub is configured once"),
            "the declared reason is what a refused swap — and the restart it falls back to — is explained by");
    }

    [Fact]
    public void ADeclarationWithABlankReason_FailsTheGuard()
    {
        var contributions = ModuleContributions.Of(Load("MeshWeaver.Test.GuardBlank",
            "", "[assembly: MeshWeaver.Mesh.ModuleRestartRequired(\" \")]"));

        ModuleLiveUpdateGuard.Violation("MeshWeaver.Test.GuardBlank", contributions)
            .Should().NotBeNull().And.Contain("BLANK");
    }

    private const string MeshHubConfiguration =
        "public override System.Collections.Generic.IEnumerable<System.Func<MeshWeaver.Messaging.MessageHubConfiguration, MeshWeaver.Messaging.MessageHubConfiguration>> HubConfigurations => [c => c];";

    private static System.Reflection.Assembly Load(string name, string members, string assemblyAttributes = "")
    {
        var source = $$"""
            {{assemblyAttributes}}
            [assembly: Guard.Module]
            namespace Guard;
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
            {
                public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                    [new MeshWeaver.Mesh.MeshNode("GuardProbe") { Name = "probe" }];
                {{members}}
            }
            """;
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            PlatformReferences.Platform(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        // A collectible context of its own: the guard reads the assembly, nothing else needs it.
        var context = new System.Runtime.Loader.AssemblyLoadContext(name, isCollectible: true);
        buffer.Position = 0;
        return context.LoadFromStream(buffer);
    }
}
