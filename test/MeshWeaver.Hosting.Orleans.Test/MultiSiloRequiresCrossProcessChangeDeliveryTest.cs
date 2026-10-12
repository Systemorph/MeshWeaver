using System;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// #4802 slice 3: a silo configured as one of SEVERAL (it supplied a durable
/// <see cref="StreamProviders.PubSubStore"/>) refuses to START unless its persistence declares
/// <see cref="CrossProcessChangeDelivery"/>. Since the memory-stream change feed was retired, the
/// storage layer's feed is the only carrier of one silo's commit to the others, and a store without
/// one fails silently — every silo but the writer serves stale caches.
///
/// <para>The refusal arm starts a REAL host rather than calling the validator by hand: the claim is
/// that the silo does not come up, and only Orleans' own start path (it runs every
/// <see cref="IConfigurationValidator"/> while constructing the silo's hosted service) can show that.
/// A validator nobody invokes would pass a hand-call and refuse nothing.</para>
/// </summary>
public class MultiSiloRequiresCrossProcessChangeDeliveryTest
{
    private static IHost BuildSilo(Action<ISiloBuilder>? configurePubSubStore, bool declare)
    {
        var builder = Host.CreateApplicationBuilder();
        if (declare)
            builder.Services.DeclareCrossProcessChangeDelivery("test feed");
        builder.UseOrleans(silo => silo
            .UseLocalhostClustering()
            .ConfigureMeshWeaverServer(configurePubSubStore));
        return builder.Build();
    }

    private static void DurableStore(ISiloBuilder silo) =>
        silo.Services.AddKeyedSingleton<IGrainStorage>(StreamProviders.PubSubStore, new NeverCalledStore());

    [Fact(Timeout = 30_000)]
    public async Task ADurablePubSubStoreWithoutTheDeclaration_RefusesToStart()
    {
        using var host = BuildSilo(DurableStore, declare: false);

        Func<Task> start = () => host.StartAsync(TestContext.Current.CancellationToken);

        (await start.Should().ThrowAsync<OrleansConfigurationException>(
                "a multi-silo silo over a store with no cross-process change feed must not come up"))
            .Which.Message.Should().Be(CrossProcessChangeDeliveryValidator.RefusalMessage);
    }

    /// <summary>The control: the SAME durable configuration with the declaration passes the check,
    /// so the refusal above is caused by the missing declaration and nothing else.</summary>
    [Fact]
    public void ADurablePubSubStoreWithTheDeclaration_PassesTheCheck()
    {
        using var host = BuildSilo(DurableStore, declare: true);

        var validators = host.Services.GetServices<IConfigurationValidator>();
        validators.Should().Contain(v => v is CrossProcessChangeDeliveryValidator,
            "a durable PubSubStore must register the check");

        foreach (var validator in validators)
            if (validator is CrossProcessChangeDeliveryValidator)
                validator.ValidateConfiguration();
    }

    /// <summary>A single-process host (no durable store: the Monolith, a localhost silo, the bake
    /// silo, every in-process TestCluster) needs no declaration and must never meet the check.</summary>
    [Fact]
    public void NoDurablePubSubStore_RegistersNoCheck()
    {
        using var host = BuildSilo(configurePubSubStore: null, declare: false);

        host.Services.GetServices<IConfigurationValidator>()
            .Should().NotContain(v => v is CrossProcessChangeDeliveryValidator,
                "a cluster of one has no other process to tell about its commits");
    }

    /// <summary>Registration-only stand-in for a durable provider; nothing here is ever invoked.</summary>
    private sealed class NeverCalledStore : IGrainStorage
    {
        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => throw new NotSupportedException("registration-only stub");

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => throw new NotSupportedException("registration-only stub");

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => throw new NotSupportedException("registration-only stub");
    }
}
