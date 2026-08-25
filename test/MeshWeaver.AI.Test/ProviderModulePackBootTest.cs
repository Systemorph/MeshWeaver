#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.AI.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

[assembly: ProbeProviderPack]

namespace MeshWeaver.AI.Test;

/// <summary>
/// A language-model provider that ships as a Store-landed MODULE (its DLL is listed in
/// <c>Modules:Assemblies</c> / the activation sidecar rather than compiled into the portal) must
/// arrive in DI complete: its catalog source AND its <see cref="IChatClientFactory"/>. That is the
/// shape <c>MeshWeaver.AI.Anthropic</c>'s <c>AnthropicProvidersAttribute</c> carries, and the shape
/// MeshWeaver#1965 reported as broken — a mesh serving <c>claude-*</c> rounds through another
/// provider's factory because no Anthropic factory was in DI.
///
/// <para>The cause there turned out to be upstream (#1949/#1954: a generation-landed module never
/// loaded at all, so its attribute was never read). This suite pins the half the platform owns:
/// once the DLL IS loaded, <see cref="MeshBuilder.InstallAssemblies"/> must fold the pack's
/// registrations into the mesh's service collection. The pack below is a stand-in, deliberately
/// modelled on the real one — the provider modules live in the MeshWeaver.Plugins repo now, so the
/// platform can only pin the CONTRACT between them.</para>
/// </summary>
public class ProviderModulePackBootTest
{
    [Fact]
    public void ProviderPack_CarriesAnAssemblyAttribute_RegisteringCatalogSourceAndFactory()
    {
        var attributes = typeof(ProbeProviderPackAttribute).Assembly
            .GetCustomAttributes<MeshNodeProviderAttribute>()
            .OfType<ProbeProviderPackAttribute>()
            .ToList();
        Assert.NotEmpty(attributes);

        var services = attributes
            .SelectMany(a => a.Nodes)
            .SelectMany(n => n.GlobalServiceConfigurations)
            .Aggregate((IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        AssertFactoryAndCatalogSourceLanded(services);
    }

    [Fact]
    public void InstallAssemblies_FoldsTheProviderPackIntoTheMeshServiceCollection()
    {
        // The SAME fold the portal's boot performs for every Modules:Assemblies entry —
        // Assembly.LoadFrom + attribute discovery + GlobalServiceConfigurations included.
        var configurations = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => configurations.Add(configure),
            AddressExtensions.CreateMeshAddress());

        builder.InstallAssemblies(typeof(ProbeProviderPackAttribute).Assembly.Location);

        var services = configurations.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        AssertFactoryAndCatalogSourceLanded(services);
    }

    /// <summary>
    /// The partner to the fold above, and the property that would have kept memex-cloud serving on
    /// 2026-08-25 (#2234): a module this build CANNOT install must cost its own contribution and
    /// nothing else. It used to cost the process — a landed AzureFoundry built against a
    /// 9-parameter record ctor met the 8-parameter image, the MissingMethodException escaped
    /// InstallAssemblies, and every replacement pod aborted ~2 s into boot with no application
    /// logging, for ~90 minutes.
    ///
    /// <para>🚨 The assertion that matters is not "it did not throw" — it is that the GOOD pack
    /// beside it still landed. A fix that swallowed the failure and also dropped the healthy
    /// module would pass a no-throw test while leaving the portal just as broken.</para>
    /// </summary>
    [Fact]
    public void InstallAssemblies_WithAModuleItCannotLoad_KeepsTheGoodOne_AndDoesNotAbort()
    {
        var configurations = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => configurations.Add(configure),
            AddressExtensions.CreateMeshAddress());

        var unloadable = Path.Combine(AppContext.BaseDirectory, "MeshWeaver.ThisModuleCannotLoad.dll");

        var exception = Record.Exception(() => builder.InstallAssemblies(
            unloadable,
            typeof(ProbeProviderPackAttribute).Assembly.Location));

        Assert.Null(exception);

        var services = configurations.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        // The healthy pack is unaffected by its neighbour's failure.
        AssertFactoryAndCatalogSourceLanded(services);

        // And the failure is RECORDED rather than swallowed — a skip nobody can see is the shape
        // that forges correct-looking bugs, so the host must be able to surface it.
        var recorded = services
            .Where(d => d.ServiceType == typeof(IncompatibleModule))
            .Select(d => d.ImplementationInstance)
            .OfType<IncompatibleModule>()
            .ToList();
        var broken = Assert.Single(recorded);
        Assert.Equal("MeshWeaver.ThisModuleCannotLoad", broken.Name);
        Assert.Contains("CONTRIBUTING NOTHING", broken.Report());
    }

    /// <summary>
    /// The other direction: with every module loadable, nothing is reported as incompatible. A
    /// recorder that always fired would make the assertion above meaningless.
    /// </summary>
    [Fact]
    public void InstallAssemblies_WithEveryModuleLoadable_RecordsNoIncompatibility()
    {
        var configurations = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => configurations.Add(configure),
            AddressExtensions.CreateMeshAddress());

        builder.InstallAssemblies(typeof(ProbeProviderPackAttribute).Assembly.Location);

        var services = configurations.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IncompatibleModule));
    }

    [Fact]
    public void TheFactoryClaimsItsOwnModels_AndNobodyElses()
    {
        var factory = new ProbeClaudeChatClientFactory();
        Assert.True(factory.Supports("claude-sonnet-4-6"));
        Assert.True(factory.Supports("CLAUDE-opus-4-8"));
        Assert.False(factory.Supports("moonshotai/kimi-k3"));
        Assert.False(factory.Supports(""));
        // Order 0 — a claude-shaped id must reach this factory before any catch-all gateway
        // (AgentChatClient.GetFactoryForModel takes the lowest Order among factories that Support it).
        Assert.Equal(0, factory.Order);
    }

    private static void AssertFactoryAndCatalogSourceLanded(IServiceCollection services)
    {
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IChatClientFactory)
            && d.ImplementationType == typeof(ProbeClaudeChatClientFactory));

        var catalog = services
            .Where(d => d.ServiceType == typeof(LanguageModelCatalogOptions))
            .Select(d => d.ImplementationInstance)
            .OfType<LanguageModelCatalogOptions>()
            .SingleOrDefault();
        Assert.NotNull(catalog);
        Assert.Contains(catalog!.Sources, s => s.ProviderName == ProbeProviderPackAttribute.ProviderName);
    }
}

/// <summary>
/// Stand-in for a provider module's boot attribute — the same three registrations
/// <c>AnthropicProvidersAttribute</c> makes: the catalog source, the options binding, and the
/// <see cref="IChatClientFactory"/> as an enumerable singleton.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ProbeProviderPackAttribute : MeshNodeProviderAttribute
{
    public const string ProviderName = "ProbeClaude";

    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.AI.ProbeClaude")
        {
            Name = "Probe Claude language-model provider",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services =>
        {
            services.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: ProviderName, ProviderName: ProviderName, Order: 1,
                DisplayLabel: "Probe Claude", DefaultEndpoint: "https://probe.example/v1/messages",
                DefaultModelIds: ImmutableArray<string>.Empty, RequiresApiKey: true));
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IChatClientFactory, ProbeClaudeChatClientFactory>());
            return services;
        }),
    ];
}

/// <summary>
/// Minimal <see cref="IChatClientFactory"/> with the real Anthropic factory's routing predicate.
/// Agent creation is out of scope here — the pin is registration + routing.
/// </summary>
public sealed class ProbeClaudeChatClientFactory : IChatClientFactory
{
    /// <inheritdoc />
    public string Name => "Probe Claude";

    /// <inheritdoc />
    public IReadOnlyList<string> Models => [];

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public bool Supports(string modelName) =>
        !string.IsNullOrEmpty(modelName)
        && modelName.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null) =>
        throw new NotSupportedException("Registration probe — never creates an agent.");

    /// <inheritdoc />
    public ChatClientAgent CreateAgent(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null) =>
        throw new NotSupportedException("Registration probe — never creates an agent.");
}
