using System;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the WIRING half of issue #1729 — the half an in-process cluster can actually observe.
///
/// <para>Cross-silo delivery to every <c>StreamRoutedAddressTypes</c> hub (<c>mesh</c>,
/// <c>portal</c>, <c>client</c>, <c>cache</c>, <c>import</c>) — and therefore every REPLY to one —
/// is published to an Orleans memory stream, and whether that publish finds its subscriber is
/// decided entirely by what backs <see cref="StreamProviders.PubSubStore"/>. With Orleans' in-memory
/// default the subscriber list lives in the RAM of whichever silo activated the
/// <c>PubSubRendezvousGrain</c>; when that silo departs — which every rolling deploy guarantees —
/// the list is gone, the consumer's handle stays valid and silent, and every later publish succeeds
/// and is DISCARDED. On memex-cloud that made one replica serve <c>/api/content</c> in 6–57 ms while
/// the other ran out its full 60 s reply budget, deterministically, across several image rolls.</para>
///
/// <para>🚨 <b>The loss itself is NOT testable here and no test in this assembly claims to cover
/// it.</b> Every silo in an <c>Orleans.TestingHost.TestCluster</c> shares one process, one heap and
/// therefore ONE memory grain store, so "silo A departs" never destroys the state silo B's
/// subscription lives in. A two-silo <c>TestCluster</c> assertion for this bug was written and
/// passed identically with and without the fix — worse than no test. Reproducing the loss needs two
/// PROCESSES and a real silo departure; see
/// <c>Doc/Architecture/OrleansStreamPubSubDurability</c> for the live-cluster probe.</para>
///
/// <para>What IS deterministically testable is the seam that decides the store, and that is exactly
/// where the regression can silently return: registering a durable provider IN ADDITION to the
/// memory one leaves the winner decided by registration order, so the deployment would look
/// configured and still lose replies. Hence the assertion is "exactly ONE provider, and it is the
/// caller's" — never merely "a durable one is present".</para>
/// </summary>
public class OrleansPubSubStoreConfigurationTest
{
    /// <summary>
    /// Every grain-storage provider registered under Orleans' well-known <c>PubSubStore</c> name by
    /// <see cref="OrleansServerRegistryExtensions.ConfigureMeshWeaverServer"/>. Inspects the service
    /// DESCRIPTORS rather than resolving them: the question is which providers were registered and
    /// how many, which resolution would hide (the container hands back the last one and says
    /// nothing about the shadowed first).
    /// </summary>
    private static ServiceDescriptor[] PubSubStoreProviders(Action<ISiloBuilder>? configurePubSubStore)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(silo => silo
            .UseLocalhostClustering()
            .ConfigureMeshWeaverServer(configurePubSubStore));

        return builder.Services
            .Where(d => d.ServiceType == typeof(IGrainStorage)
                        && d.ServiceKey as string == StreamProviders.PubSubStore)
            .ToArray();
    }

    [Fact]
    public void NoDurableStoreSupplied_RegistersExactlyOneOrleansProvidedStore()
    {
        var providers = PubSubStoreProviders(configurePubSubStore: null);

        // The single-silo default: Orleans' own in-memory grain storage, registered by
        // AddMemoryGrainStorage — hence created by an Orleans-owned factory, not by us.
        providers.Should().ContainSingle(
            "PubSubStore must have exactly one provider — a second registration makes the "
            + "effective store depend on registration order");
        providers[0].KeyedImplementationInstance.Should().BeNull();
        providers[0].KeyedImplementationFactory.Should().NotBeNull();
        providers[0].KeyedImplementationFactory!.Method.DeclaringType!.Assembly.GetName().Name
            .Should().StartWith("Orleans",
                "the default must be Orleans' AddMemoryGrainStorage, unchanged");
    }

    [Fact]
    public void DurableStoreSupplied_ReplacesTheMemoryStoreInsteadOfShadowingIt()
    {
        var durable = new StubDurablePubSubStore();

        var providers = PubSubStoreProviders(silo =>
            silo.Services.AddKeyedSingleton<IGrainStorage>(StreamProviders.PubSubStore, durable));

        // 🚨 The whole point. If ConfigureMeshWeaverServer ALSO registered the memory store there
        // would be two providers here, the container would silently pick one, and a deployment that
        // had correctly asked for durability could still be running on RAM.
        providers.Should().ContainSingle(
            "the caller's store must REPLACE the memory default, never be registered alongside it");
        providers[0].KeyedImplementationInstance.Should().BeSameAs(durable);
    }

    /// <summary>
    /// Stands in for a real durable provider (<c>AddAdoNetGrainStorage</c> against the cluster's
    /// Postgres). Only its IDENTITY is under test — nothing here is ever invoked, so a call would
    /// mean the test is asserting something other than the registration it claims to.
    /// </summary>
    private sealed class StubDurablePubSubStore : IGrainStorage
    {
        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => throw new NotSupportedException("registration-only stub");

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => throw new NotSupportedException("registration-only stub");

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => throw new NotSupportedException("registration-only stub");
    }
}
