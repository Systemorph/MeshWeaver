using MeshWeaver.Fixture;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🛡️ A satellite type a MODULE contributes is protected by the repoint guard like a core one (review of
/// #7655). The module registers a <see cref="SatelliteAccessRule"/> for a node type that is not one of
/// the standard satellite types. The root holds it as a forwarding proxy, and
/// <see cref="NodeTypeAccessRuleSet"/> hands it out behind a lease wrapper, so a type test on what
/// <see cref="NodeTypeAccessRuleSet.Find"/> returns never sees a <see cref="SatelliteAccessRule"/>.
/// <see cref="SatelliteMainNodeCommitGuard"/> classified that way, so a module satellite could be
/// repointed through any routed update. The classification now reads the leased rule itself
/// (<see cref="NodeTypeAccessRuleSet.IsGovernedBy{TRule}"/>).
/// </summary>
public sealed class ModuleSatelliteMainNodeRepointTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.ModuleSatellite";
    private const string ProbeType = "ModuleSatelliteProbe";
    private readonly string root = Path.Combine(Path.GetTempPath(), "module-satellite-" + Guid.NewGuid().ToString("N"));

    public ModuleSatelliteMainNodeRepointTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(Write());

    [Fact]
    public void AModuleSatelliteRepointedByAnUpdate_IsRefused()
    {
        PartitionDefinition.IsSatelliteNodeType(ProbeType).Should().BeFalse(
            "the probe must not be a standard satellite type, or the module rule would not be what protects it");
        var set = Mesh.ServiceProvider.GetRequiredService<NodeTypeAccessRuleSet>();
        set.Find(ProbeType, NodeOperation.Update).Should().NotBeNull("the module's rule governs its type");
        set.Find(ProbeType, NodeOperation.Update).Should().NotBeOfType<SatelliteAccessRule>(
            "what the set hands out for a module's rule is a lease wrapper, which is the reason the guard asks the set");
        set.IsGovernedBy<SatelliteAccessRule>(ProbeType, NodeOperation.Update).Should().BeTrue(
            "the leased rule itself is the module's SatelliteAccessRule");
        set.IsGovernedBy<SatelliteAccessRule>("Markdown", NodeOperation.Update).Should().BeFalse(
            "the control: a type without a satellite rule is not classified as one");

        var guard = new SatelliteMainNodeCommitGuard(Mesh.ServiceProvider);
        var stored = new MeshNode("p1", "Space/Doc/_Probe") { Name = "p1", NodeType = ProbeType, MainNode = "Space/Doc" };
        var writer = new AccessContext { ObjectId = "module-satellite-writer", Name = "writer" };

        guard.Refusal(nameof(MeshNode), stored, stored with { MainNode = "Space/Elsewhere" }, stored.Path, writer,
                Mesh.JsonSerializerOptions)
            .Should().NotBeNull("an update never chooses a satellite's main node, whichever module contributed the type");
        guard.Refusal(nameof(MeshNode), stored, stored with { Name = "renamed" }, stored.Path, writer,
                Mesh.JsonSerializerOptions)
            .Should().BeNull("the control: an update that keeps the pointer is an ordinary edit");
    }

    private const string Source = """
        using System.Collections.Generic;
        using MeshWeaver.Graph.Security;
        using MeshWeaver.Mesh;
        using MeshWeaver.Mesh.Services;
        using MeshWeaver.Messaging;
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.ModuleSatellite.Module]
        namespace MeshWeaver.Test.ModuleSatellite;
        public sealed class ModuleAttribute : MeshNodeProviderAttribute
        {
            public override IEnumerable<MeshNode> Nodes =>
                [new MeshNode("ModuleSatelliteProbeNode") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton<INodeTypeAccessRule>(sp =>
                            new SatelliteAccessRule("ModuleSatelliteProbe", sp.GetRequiredService<IMessageHub>()));
                        return services;
                    })];
        }
        """;

    private string Write()
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(SatelliteAccessRule).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(Source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Module}@g1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
