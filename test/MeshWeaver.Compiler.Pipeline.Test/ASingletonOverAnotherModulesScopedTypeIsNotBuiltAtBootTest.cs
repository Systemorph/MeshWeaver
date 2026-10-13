using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The boot-time scoped-dependency check (<c>ScopedDependencyWalk</c>, MeshWeaver#6556) ACROSS modules.
/// Module A registers a SINGLETON whose constructor takes a type module B DECLARES, and module B registers
/// that type SCOPED over circuit state. The walk read A's own registrations and the root's — and a type
/// that names a module's (collectible) assembly is never looked up in the root — so it saw no
/// registration for B's type, judged A's singleton sound, and built it at boot. That built B's scoped
/// service with no caller, against circuit state nothing had initialised: it threw, and module A was
/// WITHDRAWN at boot. The same graph inside ONE module loads (AModuleWithCircuitBoundServicesLoadsAtBootTest),
/// so this is the cross-module half of the same rule: what belongs to a circuit is never built at boot.
/// Two real modules compiled here, a real mesh; nothing is mocked.
/// </summary>
public sealed class ASingletonOverAnotherModulesScopedTypeIsNotBuiltAtBootTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string ModuleA = "MeshWeaver.Test.CrossScopedA";
    private const string ModuleB = "MeshWeaver.Test.CrossScopedB";

    private readonly string root = Path.Combine(Path.GetTempPath(), "cross-scoped-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var moduleB = Write(ModuleB, ModuleBSource, []);
        var moduleA = Write(ModuleA, ModuleASource, [MetadataReference.CreateFromFile(moduleB)]);
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddScoped<CircuitBoundState>())
            .InstallAssemblies(moduleB, moduleA);
    }

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    [Fact]
    public void ModuleA_IsPublishedAtBoot_NotWithdrawn()
    {
        var withdrawn = Mesh.ServiceProvider.GetServices<IncompatibleModule>()
            .Where(m => m.Name is ModuleA or ModuleB)
            .Select(m => $"{m.Name}: {m.Error}")
            .ToArray();
        withdrawn.Should().BeEmpty(
            "a singleton over another module's SCOPED type belongs to a circuit — it is left to its first resolve, never built at boot against state no circuit initialised");
        Contexts.Current(ModuleA).Should().NotBeNull("module A is published");
        Contexts.Current(ModuleB).Should().NotBeNull("module B is published");
    }

    private const string ModuleBSource = """
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.CrossScopedB.Module]
        namespace MeshWeaver.Test.CrossScopedB;
        public sealed class CrossDialog
        {
            public CrossDialog(MeshWeaver.Graph.Test.CircuitBoundState state) => state.Subscribe();
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("CrossScopedB") { Name = "b", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services => services.AddScoped<CrossDialog>())];
        }
        """;

    private const string ModuleASource = """
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.CrossScopedA.Module]
        namespace MeshWeaver.Test.CrossScopedA;
        public sealed class Holder
        {
            public Holder(MeshWeaver.Test.CrossScopedB.CrossDialog dialog) { }
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("CrossScopedA") { Name = "a", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services => services.AddSingleton<Holder>())];
        }
        """;

    private string Write(string module, string source, MetadataReference[] extra)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(CircuitBoundState).Assembly.Location))
            .AddRange(extra);
        var compilation = CSharpCompilation.Create(module, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{module}@g1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
