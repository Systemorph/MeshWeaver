using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 Timeout == error (Doc/Architecture/TimeoutIsError) on an Orleans test cluster. A silo builds
/// its own container, so a timeout terminal that fires in a silo-hosted hub never reached the test
/// container's <see cref="TimeoutTerminalRecorder"/>, and the guard under-reported every Orleans
/// test. This registers one recorder per silo — an instance owned by that silo's container, never
/// static — which <see cref="OrleansTestClusterHost.TimeoutTerminalRecorders"/> hands to the test's
/// window (<see cref="ITimeoutTerminalSource"/>).
///
/// <para>Applied centrally by <see cref="OrleansTestCluster.DeployAsync"/>, beside
/// <see cref="TestGrainDirectorySizing"/>, so every cluster deployed on the normal path records.</para>
/// </summary>
public sealed class TimeoutTerminalSiloRecording : ISiloConfigurator
{
    /// <inheritdoc />
    public void Configure(ISiloBuilder siloBuilder) =>
        siloBuilder.ConfigureServices(Register);

    /// <summary>Registers one <see cref="TimeoutTerminalRecorder"/> as an observer in <paramref name="services"/>.</summary>
    /// <param name="services">A silo's or the client host's services.</param>
    internal static void Register(IServiceCollection services)
    {
        var recorder = new TimeoutTerminalRecorder();
        services.AddSingleton(recorder);
        services.AddSingleton<ITimeoutTerminalObserver>(recorder);
    }

    /// <summary>The recorders of every in-process silo of <paramref name="cluster"/> that has one.</summary>
    /// <param name="cluster">The test cluster.</param>
    internal static IEnumerable<TimeoutTerminalRecorder> OfSilos(TestCluster cluster) =>
        cluster.Silos
            // In-process silos only: an out-of-process silo's recorder is in another process and is
            // KNOWN-UNCOVERED here. Every Orleans test in this tree deploys in-process silos today.
            .OfType<InProcessSiloHandle>()
            .Select(silo => silo.SiloHost.Services.GetService<TimeoutTerminalRecorder>())
            .OfType<TimeoutTerminalRecorder>();
}
