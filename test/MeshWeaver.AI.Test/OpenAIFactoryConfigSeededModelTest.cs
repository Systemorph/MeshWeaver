#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.AI.OpenAI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the e2e-portal 2026-07-01 factory-resolution defect: a deployment whose ONLY provider is the
/// config-seeded generic <c>OpenAICompatible</c> gateway (<c>OpenAICompatible__Models__0=qwen-small</c>
/// + Endpoint + ApiKey — exactly what memex-local/e2e seeds) must resolve
/// <see cref="OpenAIChatClientAgentFactory"/> for that model and build a chat client from the SAME
/// config section <see cref="BuiltInLanguageModelProvider"/> seeds the catalog from.
///
/// <para>Before the fix, <c>AddOpenAICompatible</c> deliberately skipped binding its config section to
/// <c>IOptions&lt;OpenAIConfiguration&gt;</c> (to avoid clobbering a coexisting direct-OpenAI binding),
/// so the factory's <c>Models</c> list AND its endpoint/key fallback were EMPTY — everything hinged on
/// the mesh catalog snapshot being warm. Any cold-snapshot window ⇒ <c>Supports()</c> false +
/// "ApiKey is missing" for EVERY agent ⇒ empty agents dict ⇒ (previously masked) "agent not found".
/// The fix reads the owned catalog sources' config sections directly — one source of truth with the
/// catalog seeder, no IOptions collision, no snapshot dependency.</para>
/// </summary>
public class OpenAIFactoryConfigSeededModelTest : AITestBase
{
    public OpenAIFactoryConfigSeededModelTest(ITestOutputHelper output) : base(output) { }

    private const string ModelId = "qwen-small";
    private const string Endpoint = "http://ollama.example:11434/v1";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            // Registers the OpenAICompatible catalog source + OpenAIChatClientAgentFactory —
            // the exact wiring MemexConfiguration enables via Features:Ai:Providers:OpenAICompatible.
            .AddOpenAICompatible()
            .ConfigureServices(services =>
            {
                var dict = new Dictionary<string, string?>
                {
                    // The memex-local / e2e seeding shape (env: OpenAICompatible__Models__0 etc.).
                    ["OpenAICompatible:Models:0"] = ModelId,
                    ["OpenAICompatible:Endpoint"] = Endpoint,
                    ["OpenAICompatible:ApiKey"] = "ollama",
                };
                var config = new ConfigurationBuilder()
                    .Add(new MemoryConfigurationSource { InitialData = dict! })
                    .Build();
                services.AddSingleton<IConfiguration>(config);
                return services;
            });

    private OpenAIChatClientAgentFactory Factory =>
        Mesh.ServiceProvider.GetServices<IChatClientFactory>()
            .OfType<OpenAIChatClientAgentFactory>()
            .First();

    /// <summary>
    /// The config-seeded model resolves the factory IMMEDIATELY — no dependency on the mesh
    /// catalog snapshot being warm (the cold-boot window that failed every agent build).
    /// </summary>
    [Fact]
    public void Supports_ConfigSeededModel_WithoutWarmCatalogSnapshot()
    {
        Factory.Supports(ModelId).Should().BeTrue(
            "OpenAICompatible:Models lists the model — the factory must resolve it from config alone");
    }

    /// <summary>
    /// Building an agent for the config-seeded model succeeds using the section's Endpoint/ApiKey
    /// (previously: "ApiKey is missing for model 'qwen-small'" because the unbound IOptions fallback
    /// was empty and the resolver snapshot was cold).
    /// </summary>
    [Fact]
    public void CreateAgent_ConfigSeededModel_ResolvesEndpointAndKeyFromTheSeededSection()
    {
        var chat = new AgentChatClient(Mesh.ServiceProvider);
        var config = new AgentConfiguration { Id = "Assistant", Instructions = "assist" };

        var agent = Factory.CreateAgent(
            config, chat,
            ImmutableDictionary<string, Microsoft.Agents.AI.ChatClientAgent>.Empty,
            [config],
            modelName: ModelId);

        agent.Should().NotBeNull(
            "the seeded OpenAICompatible section supplies endpoint + key — creation must not throw 'ApiKey is missing'");
    }

    /// <summary>
    /// The composer's persisted form — the full LanguageModel node PATH — must also resolve the
    /// factory once the catalog (seeded from the same config section) is visible to the resolver.
    /// </summary>
    [Fact]
    public async Task Supports_FullNodePathForm_ResolvesViaProviderStamp()
    {
        var modelPath = $"{ModelProviderNodeType.RootNamespace}/OpenAICompatible/{ModelId}";
        Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>().EnsureSubscription();

        // The catalog nodes are static (BuiltInLanguageModelProvider) but surface through the
        // resolver's synced query — poll its public surface until warm, like the resolver tests do.
        var supported = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => Factory.Supports(modelPath))
            .Should().Within(15.Seconds()).Match(s => s);
        supported.Should().BeTrue(
            "the composer persists the model node PATH — path→provider→factory resolution must hold");
    }
}
