using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The REGISTRATION half of an instance singleton (#6092 review): <see cref="HostDrainExtensions.RelocateOnDrain"/>
/// is the opt-in a real hub carries, and it must register that hub's address with the ONE
/// <see cref="HostDrainSignal"/> the process holds. The two-silo test registers its singleton by hand, so
/// a defect in the extension's wiring — or a hosted hub whose container cannot resolve the signal, which the
/// extension silently skips — would pass it while the feature did nothing in production.
/// </summary>
public class RelocateOnDrainRegistersItsHubTest : HubTestBase
{
    /// <summary>Registers the process-wide signal the way the mesh does (one singleton).</summary>
    /// <param name="output">The xUnit output helper.</param>
    public RelocateOnDrainRegistersItsHubTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<HostDrainSignal>();
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).RelocateOnDrain();

    /// <summary>
    /// A hub configured with <c>RelocateOnDrain()</c> is registered with the mesh's signal once it is built;
    /// a hub without it is not, and nothing must leave before drain begins.
    /// </summary>
    [Fact]
    public void AHubThatRelocatesOnDrain_IsRegisteredWithTheProcessSignal_AndOthersAreNot()
    {
        var signal = Mesh.ServiceProvider.GetRequiredService<HostDrainSignal>();
        var host = GetHost();
        var client = GetClient();

        host.ServiceProvider.GetService<HostDrainSignal>().Should().BeSameAs(signal,
            "a hosted hub resolves the process-wide signal — the extension's `is HostDrainSignal` check must not skip");
        signal.Relocates(host.Address).Should().BeTrue("RelocateOnDrain() registered the hub it configures");
        signal.Relocates(client.Address).Should().BeFalse("a hub without the opt-in stays on a draining pod");
        signal.MustLeave(host.Address).Should().BeFalse("nothing leaves before drain begins");

        signal.Begin().Should().BeTrue();
        signal.MustLeave(host.Address).Should().BeTrue("once drain began, the registered singleton must leave");
        signal.MustLeave(client.Address).Should().BeFalse();
    }
}
