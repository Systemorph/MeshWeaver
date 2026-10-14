using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The NESTED half of <see cref="ASingletonOverAnotherModulesScopedTypeIsNotBuiltAtBootTest"/>: the
/// boot-time scoped-dependency walk enters ANOTHER module's container and from there must still see what
/// the FIRST module registers. Module B declares <c>IDialog</c> and a singleton <c>Mid(IDialog)</c>;
/// module A CONTRIBUTES <c>IDialog</c> SCOPED over circuit state, and registers a singleton
/// <c>Holder(Mid)</c>. At runtime B's fallback resolves <c>IDialog</c> from A's contribution, so building
/// <c>Holder</c> at boot builds A's scoped service with no caller. The walk used to exclude the ORIGINAL
/// module's registrations for the whole recursion, so from inside B it saw no <c>IDialog</c>, judged
/// <c>Mid</c> — and so <c>Holder</c> — sound, built it, and withdrew module A.
/// Two real modules compiled here, a real mesh; nothing is mocked.
/// </summary>
public sealed class ANestedWalkBackIntoTheFirstModuleSeesItsScopedContributionTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string ModuleA = "MeshWeaver.Test.NestedScopedA";
    private const string ModuleB = "MeshWeaver.Test.NestedScopedB";

    private readonly string root = Path.Combine(Path.GetTempPath(), "nested-scoped-" + Guid.NewGuid().ToString("N"));

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
            "a singleton whose graph runs through another module back into its own module's SCOPED contribution belongs to a circuit — it is left to its first resolve, never built at boot");
        Contexts.Current(ModuleA).Should().NotBeNull("module A is published");
        Contexts.Current(ModuleB).Should().NotBeNull("module B is published");
    }

    private const string ModuleBSource = """
        [assembly: MeshWeaver.Test.NestedScopedB.Module]
        namespace MeshWeaver.Test.NestedScopedB;
        public interface IDialog { }
        public sealed class Mid
        {
            public Mid(IDialog dialog) { }
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("NestedScopedB") { Name = "b", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Mid>(services))];
        }
        """;

    private const string ModuleASource = """
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.NestedScopedA.Module]
        namespace MeshWeaver.Test.NestedScopedA;
        public sealed class Dialog : MeshWeaver.Test.NestedScopedB.IDialog
        {
            public Dialog(MeshWeaver.Graph.Test.CircuitBoundState state) => state.Subscribe();
        }
        public sealed class Holder
        {
            public Holder(MeshWeaver.Test.NestedScopedB.Mid mid) { }
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("NestedScopedA") { Name = "a", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services => services
                        .AddScoped<MeshWeaver.Test.NestedScopedB.IDialog, Dialog>()
                        .AddSingleton<Holder>())];
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
