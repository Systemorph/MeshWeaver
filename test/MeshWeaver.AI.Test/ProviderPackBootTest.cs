using System;
using System.Linq;
using System.Reflection;
using MeshWeaver.AI.AzureFoundry;
using MeshWeaver.AI.ClaudeCode;
using MeshWeaver.AI.Connect;
using MeshWeaver.AI.Copilot;
using MeshWeaver.AI.OpenAI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the boot-pack contract the AI-provider extraction rests on: each provider assembly
/// carries a <see cref="MeshNodeProviderAttribute"/> whose global service configurations register
/// everything the old builder calls did — so a portal whose composition root holds NO provider
/// type references gets its factories/harnesses purely from <c>Modules:Assemblies</c> →
/// <c>MeshBuilder.InstallAssemblies</c>.
/// </summary>
public class ProviderPackBootTest
{
    private static IServiceCollection ApplyPacks(params Assembly[] assemblies)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        foreach (var configure in assemblies
                     .SelectMany(a => a.GetCustomAttributes<MeshNodeProviderAttribute>())
                     .SelectMany(a => a.Nodes)
                     .SelectMany(n => n.GlobalServiceConfigurations))
            configure(services);
        return services;
    }

    [Fact]
    public void EveryProviderAssembly_CarriesABootAttribute()
    {
        foreach (var assembly in new[]
                 {
                     typeof(OpenAIChatClientAgentFactory).Assembly,
                     typeof(AzureClaudeChatClientAgentFactory).Assembly,
                     typeof(ClaudeCodeHarness).Assembly,
                     typeof(CopilotHarness).Assembly,
                 })
            Assert.NotEmpty(assembly.GetCustomAttributes<MeshNodeProviderAttribute>());
    }

    [Fact]
    public void ModelProviderPacks_RegisterFactoriesAndCatalogSources()
    {
        var services = ApplyPacks(
            typeof(OpenAIChatClientAgentFactory).Assembly,
            typeof(AzureClaudeChatClientAgentFactory).Assembly);

        // Four factories: OpenAI-wire (OpenAI/AzureOpenAI dedupe by impl type with
        // Compatible/OpenRouter riding the same factory), Azure Claude, Azure Foundry.
        var factories = services
            .Where(d => d.ServiceType == typeof(IChatClientFactory))
            .Select(d => d.ImplementationType)
            .ToList();
        Assert.Contains(typeof(OpenAIChatClientAgentFactory), factories);
        Assert.Contains(typeof(AzureOpenAIChatClientAgentFactory), factories);
        Assert.Contains(typeof(AzureClaudeChatClientAgentFactory), factories);
        Assert.Contains(typeof(AzureFoundryChatClientAgentFactory), factories);

        // Six catalog sources — the whole declarative provider roster.
        var catalog = services
            .Single(d => d.ServiceType == typeof(LanguageModelCatalogOptions)
                && d.ImplementationInstance is LanguageModelCatalogOptions)
            .ImplementationInstance as LanguageModelCatalogOptions;
        Assert.NotNull(catalog);
        var providers = catalog!.Sources.Select(s => s.ProviderName).ToHashSet();
        foreach (var expected in new[]
                 { "OpenAI", "AzureOpenAI", "OpenAICompatible", "OpenRouter", "Anthropic", "AzureFoundry" })
            Assert.Contains(expected, providers);
    }

    [Fact]
    public void CliPacks_RegisterHarnessesAndConnectStrategy()
    {
        var services = ApplyPacks(
            typeof(ClaudeCodeHarness).Assembly,
            typeof(CopilotHarness).Assembly);

        var harnesses = services
            .Where(d => d.ServiceType == typeof(IHarness))
            .Select(d => d.ImplementationType)
            .ToList();
        Assert.Contains(typeof(ClaudeCodeHarness), harnesses);
        Assert.Contains(typeof(CopilotHarness), harnesses);

        // The Copilot connect strategy travels with its pack — previously a direct
        // type reference in the portal composition.
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IConnectStrategy)
            && d.ImplementationType == typeof(CopilotConnectStrategy));
    }

    [Fact]
    public void SkillsDirectory_DerivesFromClaudeRoot_WhenNotConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string?>(
                    "ClaudeCode:ConfigDirRoot", "/mnt/users/"),
            })
            .Build();
        Assert.Equal("/mnt/users/_skills", ClaudeCodePackAttribute.DeriveSkillsDirectory(configuration));
        Assert.Equal("/mnt/users/_skills", CopilotPackAttribute.DeriveSkillsDirectory(configuration));
    }
}
