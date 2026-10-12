using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>A PLATFORM interface the boot-built test module implements.</summary>
public interface IBootBuiltProbe
{
    /// <summary>Says which module answered.</summary>
    string Answer();
}

/// <summary>
/// MeshWeaver#6542 — <b>built before published, at boot.</b> Every shared instance of a module's
/// container is built once the root container exists and before the root provider is handed to
/// anyone. A module whose singleton cannot be built is WITHDRAWN — never left half-published — and
/// reported as an <see cref="IncompatibleModule"/>, the record <c>/health</c> and the module-status
/// surfaces read; the mesh boots without it.
/// </summary>
public abstract class ModuleSingletonsBuiltAtBootTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The module's simple name.</summary>
    protected const string Module = "MeshWeaver.Test.BootBuilt";

    private readonly string root = Path.Combine(Path.GetTempPath(), "boot-built-" + Guid.NewGuid().ToString("N"));

    /// <summary>Whether the module carries a singleton whose constructor throws.</summary>
    protected abstract bool Broken { get; }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(Write(Source(Broken)));

    /// <summary>The mesh's module registry.</summary>
    protected ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    /// <summary>The records of modules this replica carries and does not run.</summary>
    protected IncompatibleModule[] Incompatible =>
        Mesh.ServiceProvider.GetServices<IncompatibleModule>().Where(m => m.Name == Module).ToArray();

    private static string Source(bool broken) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.BootBuilt.Module]
        namespace MeshWeaver.Test.BootBuilt;
        public sealed class Probe : MeshWeaver.Graph.Test.IBootBuiltProbe { public string Answer() => "boot-built"; }
        public sealed class Fragile
        {
            public Fragile() { {{(broken ? "throw new System.InvalidOperationException(\"cannot be built by design\");" : "")}} }
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("BootBuiltProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton<MeshWeaver.Graph.Test.IBootBuiltProbe, Probe>();
                        services.AddSingleton<Fragile>();
                        return services;
                    })];
        }
        """;

    private string Write(string source)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(IBootBuiltProbe).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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

/// <summary>A module whose singleton throws at boot is withdrawn and reported; the mesh serves without it.</summary>
public sealed class AModuleWhoseSingletonCannotBeBuiltAtBootTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => true;

    [Fact]
    public void TheModuleIsWithdrawnAndReported_AndTheMeshServesWithoutIt()
    {
        Contexts.Current(Module).Should().BeNull(
            "a module whose shared instances could not be built is never left published — half its singletons built");
        var record = Incompatible.Should().ContainSingle(
            "the withdrawal is reported where every module that failed to install is").Subject;
        record.Error.Should().Contain("could not be built before it was published")
            .And.Contain("cannot be built by design", "the constructor's own sentence is what an operator acts on");
        var probe = Mesh.ServiceProvider.GetRequiredService<IBootBuiltProbe>();
        var refused = Record.Exception(() => probe.Answer());
        refused.Should().BeOfType<InvalidOperationException>(
            "the root's forwarder has no generation to forward to — the feature is absent, said by name");
        refused!.Message.Should().Contain(Module);
    }
}

/// <summary>The negative control: the same module with a singleton that builds is published and serves.</summary>
public sealed class AModuleWhoseSingletonsBuildAtBootTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => false;

    [Fact]
    public void TheModuleIsPublished_AndServes()
    {
        Contexts.Current(Module).Should().NotBeNull();
        Incompatible.Should().BeEmpty();
        Mesh.ServiceProvider.GetRequiredService<IBootBuiltProbe>().Answer().Should().Be("boot-built");
    }
}
