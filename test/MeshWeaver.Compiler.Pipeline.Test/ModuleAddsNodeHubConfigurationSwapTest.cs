using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A held module whose BOOT generation contributes no every-per-node-hub configuration, and whose next
/// generation ADDS one (#6128 review). The swap recycles every per-node hub for it, so the indirection
/// that reads the CURRENT generation must have been registered at boot regardless — otherwise every
/// recycled hub rebuilds without the new configuration while the swap reports Live.
/// </summary>
public sealed class ModuleAddsNodeHubConfigurationSwapTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.LiveNodeHubConfig";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "live-nodehub-" + Guid.NewGuid().ToString("N"));
    private string? v1Path;
    private string V1 => v1Path ??= Write("g1", ModuleSource(addsNodeHubConfiguration: false));

    /// <summary>Creates the test over the shared monolith mesh base.</summary>
    public ModuleAddsNodeHubConfigurationSwapTest(ITestOutputHelper output) : base(output)
    {
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(V1);

    /// <summary>The new generation's per-node-hub configuration reaches a hub built after the swap.</summary>
    [Fact(Timeout = 180_000)]
    public async Task AGenerationThatAddsPerNodeHubConfiguration_ReachesHubsBuiltAfterTheSwap()
    {
        var ct = TestContext.Current.CancellationToken;
        PerNodeType().Should().BeNull("the boot generation contributes no per-node-hub configuration");

        var outcome = await Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>()
            .Swap(Write("g2", ModuleSource(addsNodeHubConfiguration: true)), "test: adds per-node-hub configuration")
            .Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        PerNodeType().Should().NotBeNull(
            "a per-node hub built after the swap must carry the configuration the new generation added");
    }

    private Type? PerNodeType()
    {
        var configure = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>().DefaultNodeHubConfiguration
            ?? throw new InvalidOperationException("the mesh carries no default per-node hub configuration");
        var configuration = configure(new MessageHubConfiguration(null, new Address("probe", "live-nodehub")));
        return configuration.TypeRegistry.GetType("LiveNodeHubMarker");
    }

    private static string ModuleSource(bool addsNodeHubConfiguration) => $$"""
        [assembly: MeshWeaver.Test.LiveNodeHubConfig.Module]
        namespace MeshWeaver.Test.LiveNodeHubConfig;
        public sealed record LiveNodeHubMarker(string Text);
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<System.Func<MeshWeaver.Mesh.MeshBuilder, MeshWeaver.Mesh.MeshBuilder>> BuilderConfigurations =>
            [
                builder => builder
                    .AddMeshNodes(new MeshWeaver.Mesh.MeshNode("LiveNodeHubProbe") { Name = "probe", NodeType = "Markdown" })
                    {{(addsNodeHubConfiguration
                        ? ".ConfigureDefaultNodeHub(config => { config.TypeRegistry.WithType(typeof(LiveNodeHubMarker), \"LiveNodeHubMarker\"); return config; })"
                        : "")}}
            ];
        }
        """;

    private string Write(string generation, string source)
    {
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)], PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Module}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
