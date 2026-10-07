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

/// <summary>A PLATFORM interface (default load context) several registrations of which make one list —
/// the <c>McpServerTool</c> shape.</summary>
public interface IListedTool
{
    /// <summary>The tool's name.</summary>
    string Name { get; }
}

/// <summary>A PLATFORM interface a module implements over the list — the <c>McpServerOptionsSetup</c>
/// shape, which takes <c>IEnumerable&lt;McpServerTool&gt;</c> inside the module's container.</summary>
public interface IToolLister
{
    /// <summary>Every tool the lister was given, in order.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>The tool a single-service resolve gave the lister.</summary>
    string Default { get; }
}

/// <summary>A tool the HOST registers in the root before any module installs.</summary>
public sealed class HostListedTool : IListedTool
{
    /// <inheritdoc />
    public string Name => "host";
}

/// <summary>
/// 🚨 <b>A module's service registered more than once is served as ALL of its registrations.</b>
/// Reported 2026-10-06 on memex.meshweaver.cloud ("the chat is not coming on the home page … IHarness")
/// Measured the same day on the three portals running core 7585f2a6 (memex.meshweaver.cloud,
/// memex.systemorph.com, control.systemorph.com): MCP listed ONE tool of 37 (<c>restore_from_point_in_time</c>,
/// the last declared), while pearl on an older image listed all 37. By the same mechanism a per-node hub
/// resolves only the LAST <c>IHarness</c> the AI module registers. Two places collapsed a list to its last
/// element:
/// <list type="number">
///   <item>the module container's fallback to the root answered a platform type with ONE stand-in
///     (<c>root.GetRequiredService(type)</c>), so an <c>IEnumerable&lt;T&gt;</c> a module service takes
///     had one element;</item>
///   <item>the per-node hub's forwarders for a module-owned type were one per TYPE, each answering the
///     last registration, so <c>hub.ServiceProvider.GetServices&lt;IHarness&gt;()</c> had one.</item>
/// </list>
/// Real Roslyn emit, a real mesh, a real per-node hub — nothing mocked.
/// </summary>
public sealed class ModuleServicesKeepEveryRegistrationTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.ListedServices";

    private readonly string root = Path.Combine(Path.GetTempPath(), "listed-services-" + Guid.NewGuid().ToString("N"));

    public ModuleServicesKeepEveryRegistrationTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => s.AddSingleton<IListedTool, HostListedTool>())
            .InstallAssemblies(Write());

    private ModuleGeneration Generation =>
        Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().Current(Module)
        ?? throw new Xunit.Sdk.XunitException($"{Module} is not held by the module registry");

    [Fact]
    public void AModuleServiceTakingAPlatformList_GetsEveryRootRegistration_InOrder()
    {
        Generation.Services.Should().NotBeNull("the module's services must be served from its own container — the shape under test");
        Generation.RootServiceBlockers.Should().BeEmpty();

        var lister = Mesh.ServiceProvider.GetRequiredService<IToolLister>();

        lister.Names.Should().Equal(["host", "alpha", "beta", "gamma"],
            "IEnumerable<IListedTool> inside the module container must be the ROOT's whole list — the host's tool and the "
            + "module's three — never one stand-in answering the root's last (the MCP server listed 1 tool of 37)");
        lister.Default.Should().Be("gamma", "a single-service resolve still answers the LAST registration, as in the root");
        Mesh.ServiceProvider.GetServices<IListedTool>().Select(t => t.Name)
            .Should().Equal(["host", "alpha", "beta", "gamma"], "the root itself holds all four");
    }

    [Fact]
    public void APerNodeHub_SeesEveryRegistrationOfAModuleOwnedType_InOrder()
    {
        var ownedType = Generation.Assembly.GetType($"{Module}.IOwnedHarness")
            ?? throw new Xunit.Sdk.XunitException("the module's IOwnedHarness was not emitted");
        var meshConfiguration = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        var defaults = meshConfiguration.DefaultNodeHubConfiguration
            ?? throw new Xunit.Sdk.XunitException("the mesh declares no default node-hub configuration");
        var hub = Mesh.GetHostedHub(new Address("listed-harness", Guid.NewGuid().ToString("N")), defaults, HostedHubCreation.Always)
            ?? throw new InvalidOperationException("HostedHubCreation.Always always yields a hub");

        var ids = hub.ServiceProvider.GetServices(ownedType)
            .Select(h => (string)ownedType.GetProperty("Id")!.GetValue(h)!)
            .ToArray();

        ids.Should().Equal(["MeshWeaver", "ClaudeCode", "Copilot"],
            "every registration of a module-owned type must reach a per-node hub — one forwarder per TYPE answered the "
            + "last alone, and the chat could not find the harness it was bound to");
        ((string)ownedType.GetProperty("Id")!.GetValue(hub.ServiceProvider.GetRequiredService(ownedType))!)
            .Should().Be("Copilot", "a single-service resolve answers the last registration");
        hub.ServiceProvider.GetServices(ownedType).First()
            .Should().BeSameAs(hub.ServiceProvider.GetServices(ownedType).First(),
                "a forwarder answers the module's singleton, never a fresh instance");
    }

    private const string Source = """
        using System.Collections.Generic;
        using System.Linq;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.DependencyInjection.Extensions;
        [assembly: MeshWeaver.Test.ListedServices.Module]
        namespace MeshWeaver.Test.ListedServices;
        public interface IOwnedHarness { string Id { get; } }
        public sealed class MeshWeaverHarness : IOwnedHarness { public string Id => "MeshWeaver"; }
        public sealed class ClaudeCodeHarness : IOwnedHarness { public string Id => "ClaudeCode"; }
        public sealed class CopilotHarness : IOwnedHarness { public string Id => "Copilot"; }
        public sealed class Alpha : MeshWeaver.Graph.Test.IListedTool { public string Name => "alpha"; }
        public sealed class Beta : MeshWeaver.Graph.Test.IListedTool { public string Name => "beta"; }
        public sealed class Gamma : MeshWeaver.Graph.Test.IListedTool { public string Name => "gamma"; }
        public sealed class Lister(IEnumerable<MeshWeaver.Graph.Test.IListedTool> tools, MeshWeaver.Graph.Test.IListedTool single)
            : MeshWeaver.Graph.Test.IToolLister
        {
            public IReadOnlyList<string> Names { get; } = tools.Select(t => t.Name).ToArray();
            public string Default { get; } = single.Name;
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("ListedServicesProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOwnedHarness, MeshWeaverHarness>());
                        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOwnedHarness, ClaudeCodeHarness>());
                        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOwnedHarness, CopilotHarness>());
                        services.AddSingleton<MeshWeaver.Graph.Test.IListedTool, Alpha>();
                        services.AddSingleton<MeshWeaver.Graph.Test.IListedTool, Beta>();
                        services.AddSingleton<MeshWeaver.Graph.Test.IListedTool, Gamma>();
                        services.AddSingleton<MeshWeaver.Graph.Test.IToolLister, Lister>();
                        return services;
                    })];
        }
        """;

    private string Write()
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(IListedTool).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(Source)], references,
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
