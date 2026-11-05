using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A module that configures LOGGING — <c>services.PostConfigure&lt;LoggerFilterOptions&gt;(…)</c>, the registration
/// MeshWeaver.Plugins#4955 added to the Mcp module — must not cycle the root <see cref="ILoggerFactory"/>.
///
/// <para>The cycle it closed (MeshWeaver.Plugins#4964): building the root logger factory builds
/// <see cref="LoggerFilterOptions"/>, whose setups include the module's post-configure, which the root holds
/// behind a forwarder that resolves <see cref="ModuleContexts"/>. The <see cref="ModuleContexts"/> factory in
/// <see cref="MeshBuilder"/> resolved <see cref="ILoggerFactory"/> to name its logger — the service being built —
/// so every boot that loaded the module died in the container with "A circular dependency was detected for the
/// service of type 'Microsoft.Extensions.Logging.ILoggerFactory'". On the fleet the module set kept pinning that
/// generation, so every pod that booted crash-looped and no roll could converge.</para>
///
/// <para>Negative control: with the <see cref="ModuleContexts"/> factory resolving <see cref="ILoggerFactory"/>
/// eagerly again, the mesh in this test cannot be built.</para>
/// </summary>
public sealed class AModuleLoggingOptionsRegistrationDoesNotCycleTheRootLoggerFactoryTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Module = "MeshWeaver.Test.LoggingOptions";
    private const string Category = "MeshWeaver.Test.LoggingOptions.Ceiling";

    private readonly string root = Path.Combine(Path.GetTempPath(), "logging-options-" + Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(Write(ModuleSource));

    [Fact]
    public void TheRootLoggerFactory_ResolvesAndCarriesTheModulesFilterRule()
    {
        var factory = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>();
        factory.Should().NotBeNull("the root logger factory must build with a module's logging setup installed");

        var filter = Mesh.ServiceProvider.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>().CurrentValue;
        filter.Rules.Should().Contain(r => r.CategoryName == Category && r.LogLevel == LogLevel.Warning,
            "the module's post-configure must still reach the root's logger filter — breaking the cycle must not "
            + "drop the module's logging setup");
    }

    /// <summary>
    /// The same module's shared instance that takes a logger must still be built and served: the module set
    /// is built before it is published, and a build that ran INSIDE the root logger factory's construction
    /// would fault on the same cycle and withdraw the module (reported as an incompatible module).
    /// </summary>
    [Fact]
    public void TheModulesLoggingSingleton_IsServedAndTheModuleIsNotWithdrawn()
    {
        Mesh.ServiceProvider.GetServices<IncompatibleModule>().Should().NotContain(m => m.Name == Module,
            "a module whose logging setup closes over the root logger factory must not be withdrawn at boot");
        var probeType = Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().Current(Module)!
            .Assembly.GetType("MeshWeaver.Test.LoggingOptions.LoggingProbe")!;
        Mesh.ServiceProvider.GetService(probeType).Should().NotBeNull(
            "the module's singleton that takes an ILogger must be built and served from the root");
    }

    private const string ModuleSource = $$"""
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Logging;
        [assembly: MeshWeaver.Test.LoggingOptions.Module]
        namespace MeshWeaver.Test.LoggingOptions;
        public static class Ceiling
        {
            public static void Apply(LoggerFilterOptions options) =>
                options.Rules.Add(new LoggerFilterRule(null, "{{Category}}", LogLevel.Warning, null));
        }
        public sealed class LoggingProbe(ILogger<LoggingProbe> logger) { public ILogger Logger { get; } = logger; }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("LoggingOptionsProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.PostConfigure<LoggerFilterOptions>(Ceiling.Apply);
                        services.AddSingleton<LoggingProbe>();
                        return services;
                    })];
        }
        """;

    private string Write(string source)
    {
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)],
            PlatformReferences.Platform(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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
