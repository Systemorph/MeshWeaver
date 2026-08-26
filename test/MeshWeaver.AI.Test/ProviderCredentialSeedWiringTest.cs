#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 The seed must be STARTED, not merely written. <see cref="ProviderCredentialSeed"/> is what
/// carries <c>{Section}:ApiKey</c> onto the <c>ModelProvider</c> node now that the resolver reads the
/// node and nothing else (MeshWeaver#1982) — so a deployment where nothing invokes it is a
/// deployment whose configured provider keys silently never arrive, which is precisely the
/// week-long outage the issue is about. Nothing else in the suite would notice: the seed's own tests
/// call it directly.
///
/// <para>It is registered on the DB-synced path only. On the in-memory path
/// <c>BuiltInLanguageModelProvider</c> re-projects configuration into the served node on every read,
/// so there is no node to converge and no write to make.</para>
/// </summary>
public class ProviderCredentialSeedWiringTest
{
    /// <summary>
    /// Installs the AI MODULE the way <c>Modules:Assemblies</c> does — through its assembly
    /// attribute, against a deployment configuration — and returns what it registered.
    ///
    /// <para>The wiring moved here with the seed itself (#2276): the portal used to register it on
    /// AI's behalf, which is exactly the reference that stopped the engine shipping from the Store.
    /// The assertion is unchanged, and it must be: WHO registers it changed, whether it is
    /// registered must not.</para>
    /// </summary>
    private static IServiceCollection Wire(params string[] servedPartitions)
    {
        var services = new ServiceCollection();
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "test"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(servedPartitions
                .Select((p, i) => new KeyValuePair<string, string?>(
                    $"Features:StaticRepoSync:Partitions:{i}", p)))
            .Build();
        builder.WithConfiguration(configuration);
        foreach (var configure in new AiMeshModuleAttribute().BuilderConfigurations)
            configure(builder);
        return services;
    }

    private static bool RegistersSeed(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService)
                          && d.ImplementationType?.Name == "ProviderCredentialSeedHostedService");

    [Fact]
    public void DbSyncedProviderPartition_StartsTheCredentialSeed()
    {
        var services = Wire(ModelProviderNodeType.RootNamespace);

        Assert.True(RegistersSeed(services),
            "the DB-synced Provider partition is the one shape where a provider node can outlive the "
            + "configuration that created it — nothing else converges it, so if this hosted service "
            + "is not registered a configured key never reaches the node (MeshWeaver#1982).");
    }

    [Fact]
    public void SyncDisabled_RegistersNothing()
    {
        var services = Wire();

        Assert.False(RegistersSeed(services),
            "with no partition served from the DB there is no import, no node to converge, and the "
            + "module registers no seed.");
    }

    [Fact]
    public void OtherPartitionsOnly_DoesNotStartTheCredentialSeed()
    {
        var services = Wire("Doc");

        Assert.False(RegistersSeed(services),
            "the seed belongs to the Provider partition; a Doc-only sync has no provider catalog.");
    }
}
