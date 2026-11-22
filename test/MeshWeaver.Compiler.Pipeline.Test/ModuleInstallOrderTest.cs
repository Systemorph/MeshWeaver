using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>memex.meshweaver.cloud, 2026-11-21:</b> a dependent module (<c>Mail.Gmail</c>) listed BEFORE its
/// dependency (<c>Mail.MicrosoftGraph</c>) had its builder hook run at install, before any generation of
/// the dependency existed: <c>FileNotFoundException</c>, "its builder hook could not be decomposed", and
/// every later live update of the dependency refused with <c>RestartRequired</c>.
/// <see cref="MeshBuilder.InstallModules"/> now installs dependencies first for every host; the negative
/// control is the same batch through <see cref="MeshBuilder.InstallModulesAsListed"/>.
/// </summary>
public sealed class ModuleInstallOrderTest : IDisposable
{
    private const string Lib = "MeshWeaver.Test.OrderLib";
    private const string Dependent = "MeshWeaver.Test.OrderDependent";
    private readonly string root = Path.Combine(Path.GetTempPath(), "install-order-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ADependentListedBeforeItsDependency_IsInstalledAfterIt_AndItsHookDecomposes()
    {
        var (lib, dependent) = Pair();

        var builder = Boot(b => b.InstallModules([new ModuleInstallCandidate(dependent), new ModuleInstallCandidate(lib)]));

        BlockersOf(builder).Should().BeEmpty(
            "the dependency's generation was installed first, so the hook's type resolved");
        builder.ModuleContexts.DependentsOf(Lib).Select(d => d.Name).Should().Contain(Dependent);
    }

    [Fact]
    public void NegativeControl_TheSameBatchInListedOrder_ReproducesTheIncident()
    {
        var (lib, dependent) = Pair();

        var builder = Boot(b => b.InstallModulesAsListed([new ModuleInstallCandidate(dependent), new ModuleInstallCandidate(lib)]));

        BlockersOf(builder).Should().Contain(
            blocker => blocker.Contains("could not be decomposed", StringComparison.Ordinal),
            "in name order the hook runs before its dependency exists");
    }

    [Fact]
    public void DependenciesFirst_IsStable_PlacesADependencyFirst_AndKeepsUnreadableInOrder()
    {
        var a = "MeshWeaver.Test.SortA";
        var b = "MeshWeaver.Test.SortB";
        var c = "MeshWeaver.Test.SortC";
        var aBytes = Emit(a, "namespace MeshWeaver.Test.SortA { public sealed class Leaf { } }");
        var aPath = Write(a, aBytes);
        var bPath = Write(b, Emit(b, "namespace MeshWeaver.Test.SortB { public sealed class Uses { public MeshWeaver.Test.SortA.Leaf? Leaf; } }", aBytes));
        var cPath = Write(c, Emit(c, "namespace MeshWeaver.Test.SortC { public sealed class Alone { } }"));
        var missing = Path.Combine(root, "no-such-module.dll");

        string[] Sorted(params string[] paths) =>
            [.. ModuleInstallOrder.DependenciesFirst([.. paths.Select(p => new ModuleInstallCandidate(p))]).Select(x => x.Location)];

        Sorted(cPath, bPath, aPath).Should().Equal(cPath, aPath, bPath);
        Sorted(aPath, bPath, cPath).Should().Equal(aPath, bPath, cPath);
        Sorted(missing, aPath, cPath).Should().Equal(missing, aPath, cPath);
    }

    private static IReadOnlyList<string> BlockersOf(MeshBuilder builder)
    {
        var generation = builder.ModuleContexts.Current(Dependent);
        generation.Should().NotBeNull($"{Dependent} must be held in its own load context");
        var contributions = generation?.Contributions;
        contributions.Should().NotBeNull("its contributions must have been recorded");
        return contributions?.LiveUpdateBlockers() ?? [];
    }

    private (string Lib, string Dependent) Pair()
    {
        var libBytes = Emit(Lib, $$"""
            [assembly: {{Lib}}.Module]
            namespace {{Lib}};
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute { }
            public interface ISwitch { }
            """);
        var dependentBytes = Emit(Dependent, $$"""
            [assembly: {{Dependent}}.Module]
            namespace {{Dependent}};
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
            {
                public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                    [new MeshWeaver.Mesh.MeshNode("OrderDependentProbe") { Name = "probe", NodeType = "Markdown" }];
                // A builder hook that names the dependency's type, as the Gmail registrations do.
                public override System.Collections.Generic.IEnumerable<System.Func<MeshWeaver.Mesh.MeshBuilder, MeshWeaver.Mesh.MeshBuilder>> BuilderConfigurations =>
                    [builder => { System.GC.KeepAlive(typeof({{Lib}}.ISwitch)); return builder; }];
            }
            """, libBytes);
        // Landed layout: each module in its own generation directory; the dependent does NOT carry the
        // dependency's assembly (as the live Gmail bundle did not carry Mail.MicrosoftGraph).
        return (Write(Lib, libBytes), Write(Dependent, dependentBytes));
    }

    private static MeshBuilder Boot(Action<MeshBuilder> install)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddOptions();
        var builder = new MeshBuilder(configure => configure(services), AddressExtensions.CreateMeshAddress());
        builder.WithConfiguration(configuration);
        install(builder);
        return builder;
    }

    private static byte[] Emit(string assembly, string source, params byte[][] references)
    {
        var metadata = PlatformReferences.Platform()
            .Concat(references.Select(bytes => (MetadataReference)MetadataReference.CreateFromImage(bytes)));
        var compilation = CSharpCompilation.Create(assembly, [CSharpSyntaxTree.ParseText(source)], metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }

    private string Write(string module, byte[] bytes)
    {
        var directory = Path.Combine(root, "modules", $"{module}@g1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, module + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
