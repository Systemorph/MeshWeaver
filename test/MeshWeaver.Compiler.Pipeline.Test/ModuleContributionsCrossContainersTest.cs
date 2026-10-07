using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>A PLATFORM interface the hub module implements over its own lists — the shape of
/// MeshWeaver.AI's model catalog (<c>BuiltInLanguageModelProvider</c>) and
/// <c>DefaultChatClientProvider</c>, which live in the AI module's container.</summary>
public interface ICatalogView
{
    /// <summary>Every catalog source the hub module's service was given, in order.</summary>
    IReadOnlyList<string> Sources { get; }

    /// <summary>Every chat-client factory the hub module's service was given, in order.</summary>
    IReadOnlyList<string> Factories { get; }

    /// <summary>What a single-service resolve of the source type answered.</summary>
    string DefaultSource { get; }
}

/// <summary>A PLATFORM interface a DEPENDENT module implements over a service its dependency declares — the
/// shape of every AI provider's chat-client factory reading MeshWeaver.AI's <c>ChatClientCredentialResolver</c>.</summary>
public interface IDependencyProbe
{
    /// <summary>What the dependency module's service answered.</summary>
    string FromHub { get; }
}

/// <summary>
/// 🚨 <b>A module's lists include what DEPENDENT modules contribute to them.</b> Measured on
/// memex.systemorph.com (the control instance) on the core #6127 images: every AI provider module
/// (MeshWeaver.AI.OpenAI's <c>Provider/OpenRouter</c> and <c>Provider/OpenRouterEU</c>, Anthropic, AzureFoundry,
/// AppleIntelligence) registers MeshWeaver.AI's catalog sources and <c>IChatClientFactory</c>s, but each
/// module's services now live in a container of their own; the provider's registrations reached the root
/// as forwarders and never the AI module's container, whose fallback refused to ask the root about any
/// type the AI module declares. The Provider import listed only the platform nodes,
/// <c>Provider/OpenRouterEU</c> had 0 models, the control logged "No IChatClientFactory is registered" and
/// every pull-request review stopped.
///
/// <para>Two real modules emitted by Roslyn — a HUB that declares the types and consumes their lists, and a
/// PROVIDER that references the hub's assembly and contributes to them — installed into a real mesh, each in
/// its own collectible context and its own service container. Negative control: without
/// <c>ModuleFallbackSource.ContributionsFromOutside</c> the hub sees <c>["hub"]</c> for both lists.</para>
/// </summary>
public sealed class ModuleContributionsCrossContainersTest : MonolithMeshTestBase
{
    private const string Hub = "MeshWeaver.Test.CatalogHub";
    private const string Provider = "MeshWeaver.Test.CatalogProvider";

    private readonly string root = Path.Combine(Path.GetTempPath(), "catalog-modules-" + Guid.NewGuid().ToString("N"));

    public ModuleContributionsCrossContainersTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var hub = Write(Hub, HubSource, []);
        var provider = Write(Provider, ProviderSource, [MetadataReference.CreateFromFile(hub)]);
        return base.ConfigureMesh(builder).InstallAssemblies(hub, provider);
    }

    private ModuleGeneration Generation(string module) =>
        Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().Current(module)
        ?? throw new Xunit.Sdk.XunitException($"{module} is not held by the module registry");

    [Fact]
    public void AModuleService_SeesTheRegistrationsADependentModuleContributes()
    {
        foreach (var module in new[] { Hub, Provider })
        {
            Generation(module).Services.Should().NotBeNull(
                $"{module}'s services must be served from its own container — the shape under test");
            Generation(module).RootServiceBlockers.Should().BeEmpty();
        }

        var view = Mesh.ServiceProvider.GetRequiredService<ICatalogView>();

        view.Sources.Should().Equal(["hub", "OpenRouter", "OpenRouterEU"],
            "the hub module's catalog must list the sources a provider module contributes, after its own — "
            + "on the control instance it listed none and Provider/OpenRouterEU had 0 models");
        view.Factories.Should().Equal(["hub", "openrouter"],
            "the hub module's chat-client provider must see the provider module's factory — the control logged "
            + "\"No IChatClientFactory is registered\"");
        view.DefaultSource.Should().Be("hub",
            "a single-service resolve keeps answering the module's OWN last registration, never a contribution");
    }

    [Fact]
    public void TheMeshHub_SeesAModuleTypesWholeList()
    {
        var factoryType = Generation(Hub).Assembly.GetType($"{Hub}.IChatFactory")
            ?? throw new Xunit.Sdk.XunitException("the hub module's IChatFactory was not emitted");
        string NameOf(object? factory) => (string)factoryType.GetProperty("Name")!.GetValue(factory)!;

        Mesh.ServiceProvider.GetServices(factoryType).Select(NameOf).Should().Equal(["hub", "openrouter"],
            "code reading a module's service off the MESH hub (ProviderCredentialSeed, every chat-client factory) "
            + "must find it — the root held none of a module's own types");
        NameOf(Mesh.ServiceProvider.GetRequiredService(factoryType)).Should().Be("openrouter",
            "with no registration of its own the root answers the LAST of the list, as one collection would");
    }

    [Fact]
    public void ADependentModule_ResolvesItsDependencysService()
    {
        Mesh.ServiceProvider.GetRequiredService<IDependencyProbe>().FromHub.Should().Be("hub-service",
            "a provider module's service must resolve a service its dependency module declares — the provider "
            + "factories read MeshWeaver.AI's ChatClientCredentialResolver, which no container outside the AI "
            + "module could resolve");
    }

    [Fact]
    public void APerNodeHub_SeesTheContributedRegistrations_AndTheDeclaringModulesDefault()
    {
        var factoryType = Generation(Hub).Assembly.GetType($"{Hub}.IChatFactory")
            ?? throw new Xunit.Sdk.XunitException("the hub module's IChatFactory was not emitted");
        var defaults = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>().DefaultNodeHubConfiguration
            ?? throw new Xunit.Sdk.XunitException("the mesh declares no default node-hub configuration");
        var hub = Mesh.GetHostedHub(new Address("catalog-node", Guid.NewGuid().ToString("N")), defaults, HostedHubCreation.Always)
            ?? throw new InvalidOperationException("HostedHubCreation.Always always yields a hub");
        string NameOf(object? factory) => (string)factoryType.GetProperty("Name")!.GetValue(factory)!;

        hub.ServiceProvider.GetServices(factoryType).Select(NameOf).Should().Equal(["hub", "openrouter"],
            "a per-node hub (OpenAIChatClientAgentFactory reads hub.ServiceProvider) must see the provider's factory too");
        NameOf(hub.ServiceProvider.GetRequiredService(factoryType)).Should().Be("openrouter",
            "a per-node hub answers a single resolve as the one collection would: the LAST registration");
    }

    private const string HubSource = """
        using System.Collections.Generic;
        using System.Linq;
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.CatalogHub.Module]
        namespace MeshWeaver.Test.CatalogHub;
        public sealed record CatalogSource(string Name);
        public interface IChatFactory { string Name { get; } }
        public sealed class HubFactory : IChatFactory { public string Name => "hub"; }
        public interface IHubService { string Answer { get; } }
        public sealed class HubService : IHubService { public string Answer => "hub-service"; }
        public sealed class CatalogView(IEnumerable<CatalogSource> sources, IEnumerable<IChatFactory> factories, CatalogSource single)
            : MeshWeaver.Graph.Test.ICatalogView
        {
            public IReadOnlyList<string> Sources { get; } = sources.Select(s => s.Name).ToArray();
            public IReadOnlyList<string> Factories { get; } = factories.Select(f => f.Name).ToArray();
            public string DefaultSource { get; } = single.Name;
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("CatalogHubProbe") { Name = "hub", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton(new CatalogSource("hub"));
                        services.AddSingleton<IChatFactory, HubFactory>();
                        services.AddSingleton<IHubService, HubService>();
                        services.AddSingleton<MeshWeaver.Graph.Test.ICatalogView, CatalogView>();
                        return services;
                    })];
        }
        """;

    private const string ProviderSource = """
        using System.Collections.Generic;
        using Microsoft.Extensions.DependencyInjection;
        using MeshWeaver.Test.CatalogHub;
        [assembly: MeshWeaver.Test.CatalogProvider.Module]
        namespace MeshWeaver.Test.CatalogProvider;
        public sealed class OpenRouterFactory : IChatFactory { public string Name => "openrouter"; }
        public sealed class Probe(IHubService hub) : MeshWeaver.Graph.Test.IDependencyProbe { public string FromHub { get; } = hub.Answer; }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("CatalogProviderProbe") { Name = "provider", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton(new CatalogSource("OpenRouter"));
                        services.AddSingleton(new CatalogSource("OpenRouterEU"));
                        services.AddSingleton<IChatFactory, OpenRouterFactory>();
                        services.AddSingleton<MeshWeaver.Graph.Test.IDependencyProbe, Probe>();
                        return services;
                    })];
        }
        """;

    private string Write(string module, string source, IEnumerable<MetadataReference> extra)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(ICatalogView).Assembly.Location))
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
