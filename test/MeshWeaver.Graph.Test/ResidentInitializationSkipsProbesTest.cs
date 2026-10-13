using System;
using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b><c>WithResidentInitialization</c> runs on a resident hub and NEVER on a transient node
/// probe</b> (Systemorph/MeshWeaver#6641).
///
/// <para>A NodeType's synchronous initializers run inside <c>MessageHubConfiguration.Build</c>, and
/// the registration probe (<see cref="ContentTypeRegistration.ProbeRegister"/>) builds the type's full
/// instance configuration and disposes it on the next line. An always-on hub that armed a sweep in a
/// plain <c>WithInitialization</c> therefore armed it on every probe too, leaving orphaned records
/// behind each one. The resident seam is the one place the probe check lives.</para>
///
/// <para>Every assertion here is synchronous by construction: <c>Build</c> runs the synchronous
/// initializers before it returns the hub, so by the time <c>TryGetHostedHub</c> / <c>ProbeRegister</c>
/// returns, every initializer that was going to run has run. Nothing is waited for.</para>
///
/// <para><b>Negative control.</b> The probe test registers a plain <c>WithInitialization</c> beside
/// the resident one and asserts it DID run on the probe — proving the probe really executed the
/// type's initializers, so the resident one's silence is the seam's doing and not a probe that never
/// built.</para>
/// </summary>
public class ResidentInitializationSkipsProbesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private ImmutableList<string> residentRuns = ImmutableList<string>.Empty;
    private ImmutableList<string> plainRuns = ImmutableList<string>.Empty;

    private void RecordResident(IMessageHub hub) =>
        ImmutableInterlocked.Update(ref residentRuns, l => l.Add(hub.Address.ToString()));

    private void RecordPlain(IMessageHub hub) =>
        ImmutableInterlocked.Update(ref plainRuns, l => l.Add(hub.Address.ToString()));

    [Fact]
    public void ResidentInitialization_RunsOnAResidentHub()
    {
        var address = new Address($"resident-init/{Guid.NewGuid():N}");
        Action<IMessageHub> resident = RecordResident;

        var result = Mesh.TryGetHostedHub(
            address,
            c => c.WithResidentInitialization(resident),
            HostedHubCreation.Always);

        try
        {
            result.Error.Should().BeNull("the resident hub must build");
            result.Hub.Should().NotBeNull();
            residentRuns.Should().ContainSingle()
                .Which.Should().Be(address.ToString(),
                    "a resident hub runs its resident initialization exactly like WithInitialization");
        }
        finally
        {
            result.Hub?.Dispose();
        }
    }

    [Fact]
    public void ResidentInitialization_RegisteredTwice_RunsOnce()
    {
        var address = new Address($"resident-init/{Guid.NewGuid():N}");
        Action<IMessageHub> resident = RecordResident;

        var result = Mesh.TryGetHostedHub(
            address,
            c => c.WithResidentInitialization(resident).WithResidentInitialization(resident),
            HostedHubCreation.Always);

        try
        {
            result.Hub.Should().NotBeNull();
            residentRuns.Should().ContainSingle(
                "the seam is idempotent on the caller's delegate, as WithInitialization is — a "
                + "configurator composed through several layers must not stack the arm");
        }
        finally
        {
            result.Hub?.Dispose();
        }
    }

    [Fact]
    public void ResidentInitialization_NeverRunsOnTheRegistrationProbe()
    {
        Action<IMessageHub> resident = RecordResident;
        Action<IMessageHub> plain = RecordPlain;

        var fault = ContentTypeRegistration.ProbeRegister(
            Mesh,
            "ResidentInitProbeType",
            c => c.WithResidentInitialization(resident).WithInitialization(plain),
            logger: null);

        fault.Should().BeNull("the registration probe must build for this test to mean anything");

        // Negative control: the probe DID run the type's synchronous initializers.
        plainRuns.Should().ContainSingle(
                "the probe builds the type's full configuration, so a plain WithInitialization runs "
                + "on it — which is exactly the hazard the resident seam exists for")
            .Which.Should().StartWith(TransientProbeAddresses.ContentTypeRegistrationProbePrefix);

        residentRuns.Should().BeEmpty(
            "a hub carrying the TransientNodeProbe marker never runs resident initialization (#6641)");
    }
}
