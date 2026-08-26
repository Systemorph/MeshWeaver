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
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    /// <summary>
    /// The gap the first isolation pass (#2275) left open. That pass wraps everything an
    /// attribute's <c>Nodes</c>/<c>AddressTypes</c>/<c>HubConfigurations</c> GETTERS can throw
    /// from — but a node's <c>GlobalServiceConfigurations</c> delegate is still just DATA at that
    /// point; <see cref="MeshBuilder"/> invokes it later, against a live <see cref="IServiceCollection"/>,
    /// via the private <c>InstallServices</c> helper. BOTH real #2234 incidents threw from exactly
    /// that later step, not from materialising <c>Nodes</c>: the original report's stack named a
    /// <c>GlobalServiceConfigurations</c> callback (<c>AzureFoundryProvidersAttribute.&lt;get_Nodes&gt;b__1_0(IServiceCollection)</c>)
    /// being CALLED, and the systemorph recurrence named <c>MeshBuilder.InstallServices</c> itself.
    /// A fixture whose <c>Nodes</c> getter succeeds but whose registration delegate throws when
    /// invoked is the one that pins this — <see cref="InstallAssemblies_WithAModuleItCannotLoad_KeepsTheGoodOne_AndDoesNotAbort"/>
    /// above throws from <c>Assembly.LoadFrom</c> itself, which never reaches this code path.
    ///
    /// <para>The broken module has to be a genuinely SEPARATE assembly from the healthy probe pack:
    /// an assembly-level attribute applies to the WHOLE assembly it lives in, so a permanently-
    /// throwing one could not share <see cref="ProbeProviderPackAttribute"/>'s DLL without also
    /// breaking <see cref="InstallAssemblies_WithEveryModuleLoadable_RecordsNoIncompatibility"/>,
    /// which loads that same DLL expecting zero incompatibilities.</para>
    /// </summary>
    [Fact]
    public void InstallAssemblies_WithAModuleWhoseServiceRegistrationThrowsAtInvocation_KeepsTheGoodOne_AndDoesNotAbort()
    {
        var brokenModulePath = CompileBrokenServiceRegistrationModule(out var brokenModuleName);

        // 🚨 Deliberately NOT `configure => configurations.Add(configure)` (the pattern the other
        // tests in this file use) — that defers every delegate to a later, test-owned Aggregate
        // call, so nothing actually RUNS while InstallAssemblies is on the stack, and this test
        // would pass for the wrong reason (or rather: it would fail in the test's own Aggregate
        // call, outside InstallAssemblies' try/catch, exactly as this test did before this line
        // was written this way — confirmed by actually running it). Production's real wiring,
        // MeshHostApplicationBuilder(Host, address) : base(x => x.Invoke(Host.Services), address),
        // invokes the delegate IMMEDIATELY against the live IServiceCollection, which is exactly
        // why both #2234 incidents' stack traces show the throw happening synchronously inside
        // MeshBuilder.InstallAssemblies/InstallServices. Mirroring that wiring here is what makes
        // this test exercise the code path the incidents actually hit.
        var services = (IServiceCollection)new ServiceCollection();
        var builder = new MeshBuilder(configure => configure.Invoke(services),
            AddressExtensions.CreateMeshAddress());

        var exception = Record.Exception(() => builder.InstallAssemblies(
            brokenModulePath,
            typeof(ProbeProviderPackAttribute).Assembly.Location));

        Assert.Null(exception);

        // The healthy pack, in its OWN assembly, is unaffected by its neighbour's failure.
        AssertFactoryAndCatalogSourceLanded(services);

        // And the failure is RECORDED, with the exact missing signature — the sentence an
        // operator acts on — rather than swallowed or left to abort the process.
        var recorded = services
            .Where(d => d.ServiceType == typeof(IncompatibleModule))
            .Select(d => d.ImplementationInstance)
            .OfType<IncompatibleModule>()
            .ToList();
        var broken = Assert.Single(recorded);
        Assert.Equal(brokenModuleName, broken.Name);
        Assert.Contains("CONTRIBUTING NOTHING", broken.Report());
        Assert.Equal("Void MeshWeaver.AI.BrokenProbe.LanguageModelCatalogSource..ctor(System.String, System.String, System.Int32)",
            broken.MissingMember);
    }

    /// <summary>
    /// The Copilot review on this PR flagged a real gap in the first version of this fix:
    /// <c>InstallServices</c> is a generator that YIELDS each node right after ITS OWN
    /// <c>GlobalServiceConfigurations</c> delegate succeeds, so a module with more than one node
    /// could have an EARLIER node's contribution land in <c>MeshNodes</c> even though a LATER node
    /// in the SAME module then throws and the whole module is recorded as
    /// <see cref="IncompatibleModule"/> — a module documented as "contributing nothing" that, in
    /// fact, contributed one node. This fixture has two nodes under one attribute: the first's
    /// registration succeeds, the second's throws.
    /// </summary>
    [Fact]
    public void InstallAssemblies_WithAModuleWhoseSecondNodeThrows_ContributesNoNodesAtAll()
    {
        var brokenModulePath = CompileTwoNodeModuleWhereTheSecondNodeThrows(
            out var firstNodePath, out var secondNodePath);

        var services = (IServiceCollection)new ServiceCollection();
        var builder = new MeshBuilder(configure => configure.Invoke(services),
            AddressExtensions.CreateMeshAddress());

        var exception = Record.Exception(() => builder.InstallAssemblies(brokenModulePath));

        Assert.Null(exception);

        var nodePaths = services.BuildServiceProvider()
            .EnumerateStaticNodes()
            .Select(n => n.Path)
            .ToList();

        // NEITHER node from the broken module reaches MeshNodes — not even the one whose own
        // ConfigureServices delegate ran successfully before its sibling threw. The module
        // "contributes nothing", exactly as IncompatibleModule.Report() claims.
        Assert.DoesNotContain(firstNodePath, nodePaths);
        Assert.DoesNotContain(secondNodePath, nodePaths);

        var recorded = services
            .Where(d => d.ServiceType == typeof(IncompatibleModule))
            .Select(d => d.ImplementationInstance)
            .OfType<IncompatibleModule>()
            .ToList();
        Assert.Single(recorded);
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

    /// <summary>
    /// Compiles and emits a REAL, standalone assembly (never <see cref="ProbeProviderPackAttribute"/>'s)
    /// carrying one <see cref="MeshNodeProviderAttribute"/> whose sole node's
    /// <c>GlobalServiceConfigurations</c> delegate throws a <see cref="MissingMethodException"/>
    /// when INVOKED — reproducing a binary-incompatible record ctor call against a live
    /// <see cref="IServiceCollection"/>, not a load-time failure. References the process's own
    /// TRUSTED_PLATFORM_ASSEMBLIES set, which in a test host already includes every assembly this
    /// fixture needs (MeshWeaver.Mesh.Contract for <see cref="MeshNodeProviderAttribute"/>/
    /// <see cref="MeshNode"/>, Microsoft.Extensions.DependencyInjection.Abstractions for
    /// <see cref="IServiceCollection"/>) — the same pattern <c>EmitToDiskWithRetryTest.RealAssemblyBytes</c>
    /// (MeshWeaver.Graph.Test) uses to emit a real fixture assembly for a test.
    /// </summary>
    private static string CompileBrokenServiceRegistrationModule(out string assemblyName)
    {
        assemblyName = $"MeshWeaver.AI.BrokenProbe.{Guid.NewGuid():N}";
        var tree = CSharpSyntaxTree.ParseText(
            """
            using System;
            using System.Collections.Generic;
            using MeshWeaver.Mesh;

            [assembly: MeshWeaver.AI.BrokenProbe.BrokenServiceRegistrationAttribute]

            namespace MeshWeaver.AI.BrokenProbe;

            [AttributeUsage(AttributeTargets.Assembly)]
            public sealed class BrokenServiceRegistrationAttribute : MeshNodeProviderAttribute
            {
                public override IEnumerable<MeshNode> Nodes =>
                [
                    new MeshNode("MeshWeaver.AI.BrokenProbe")
                    {
                        Name = "Probe module whose service registration throws",
                        NodeType = "ModuleDefinition",
                    }
                    .WithGlobalServiceRegistry(services =>
                        throw new MissingMethodException(
                            "Method not found: 'Void MeshWeaver.AI.BrokenProbe.LanguageModelCatalogSource..ctor"
                            + "(System.String, System.String, System.Int32)'.")),
                ];
            }
            """);

        var references = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName, [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var path = Path.Combine(Path.GetTempPath(), $"{assemblyName}.dll");
        using var stream = File.Create(path);
        var result = compilation.Emit(stream);
        if (!result.Success)
            throw new InvalidOperationException(
                "the broken-service-registration fixture assembly must compile: "
                + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }

    /// <summary>
    /// As <see cref="CompileBrokenServiceRegistrationModule"/>, but with TWO nodes under one
    /// attribute: the first's <c>GlobalServiceConfigurations</c> delegate succeeds (it registers a
    /// harmless marker service), the second's throws. Pins that a module's node-list contribution
    /// is all-or-nothing, not "whichever nodes got yielded before the throw".
    /// </summary>
    private static string CompileTwoNodeModuleWhereTheSecondNodeThrows(
        out string firstNodePath, out string secondNodePath)
    {
        var assemblyName = $"MeshWeaver.AI.BrokenProbeMulti.{Guid.NewGuid():N}";
        firstNodePath = "MeshWeaver.AI.BrokenProbeMulti.First";
        secondNodePath = "MeshWeaver.AI.BrokenProbeMulti.Second";
        var tree = CSharpSyntaxTree.ParseText(
            """
            using System;
            using System.Collections.Generic;
            using MeshWeaver.Mesh;
            using Microsoft.Extensions.DependencyInjection;

            [assembly: MeshWeaver.AI.BrokenProbeMulti.TwoNodeSecondThrowsAttribute]

            namespace MeshWeaver.AI.BrokenProbeMulti;

            [AttributeUsage(AttributeTargets.Assembly)]
            public sealed class TwoNodeSecondThrowsAttribute : MeshNodeProviderAttribute
            {
                public override IEnumerable<MeshNode> Nodes =>
                [
                    new MeshNode("MeshWeaver.AI.BrokenProbeMulti.First")
                    {
                        Name = "First node — its registration succeeds",
                        NodeType = "ModuleDefinition",
                    }
                    .WithGlobalServiceRegistry(services =>
                        services.AddSingleton(new FirstNodeMarker())),
                    new MeshNode("MeshWeaver.AI.BrokenProbeMulti.Second")
                    {
                        Name = "Second node — its registration throws",
                        NodeType = "ModuleDefinition",
                    }
                    .WithGlobalServiceRegistry(services =>
                        throw new MissingMethodException(
                            "Method not found: 'Void MeshWeaver.AI.BrokenProbeMulti.LanguageModelCatalogSource"
                            + "..ctor(System.String)'.")),
                ];
            }

            public sealed class FirstNodeMarker;
            """);

        var references = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName, [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var path = Path.Combine(Path.GetTempPath(), $"{assemblyName}.dll");
        using var stream = File.Create(path);
        var result = compilation.Emit(stream);
        if (!result.Success)
            throw new InvalidOperationException(
                "the two-node fixture assembly must compile: "
                + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
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
