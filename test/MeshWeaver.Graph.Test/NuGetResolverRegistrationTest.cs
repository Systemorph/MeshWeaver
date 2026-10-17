using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.NuGet;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The resolver a MESH hands out runs its NuGet leaf on that mesh's Http pool. Registration is an
/// explicit factory (<see cref="NuGetServiceCollectionExtensions.AddNuGetResolver"/>), so nothing
/// depends on which constructor the container would have picked; this pins it from the outside.
/// The leaf is observed from inside: the attempt-start seam reads the Http pool's in-flight count
/// while the attempt runs. An unparsable version range ends the attempt there, with no feed call.
/// </summary>
public class NuGetResolverRegistrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task TheMeshsResolver_RunsItsLeafOnTheMeshsHttpPool()
    {
        var resolver = Mesh.ServiceProvider.GetRequiredService<INuGetAssemblyResolver>()
            .Should().BeOfType<NuGetAssemblyResolver>().Subject;
        var httpPool = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.Http);
        var inFlightDuringAttempt = -1;
        resolver.OnResolutionStarting = () => Volatile.Write(ref inFlightDuringAttempt, httpPool.CurrentInFlight);

        var act = () => resolver.ResolveAsync(
            [new NuGetPackageReference("Acme.Registration.Probe", "[1.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentException>();

        Volatile.Read(ref inFlightDuringAttempt).Should().BeGreaterThanOrEqualTo(1,
            "the attempt runs as a leaf of the mesh's Http pool, not on an unbounded fallback");
    }
}
