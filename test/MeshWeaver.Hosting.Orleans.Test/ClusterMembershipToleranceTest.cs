using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the membership tolerance a MeshWeaver silo actually runs with (issue #6395): the options
/// are resolved from a silo configured by <c>ConfigureMeshWeaverServer</c>, exactly as the
/// production hosts configure theirs. On the code before #6395 the first test fails, because the
/// silo ran on Orleans' defaults.
/// </summary>
public class ClusterMembershipToleranceTest
{
    [Fact]
    public void MeshWeaverSilo_RunsWithWidenedProbeTolerance()
    {
        var options = ResolveMembershipOptions(silo => silo.ConfigureMeshWeaverServer());

        Assert.Equal(TimeSpan.FromSeconds(10), options.ProbeTimeout);
        Assert.Equal(3, options.NumMissedProbesLimit);
        Assert.Equal(2, options.NumVotesForDeathDeclaration);

        var orleansDefaults = new ClusterMembershipOptions();
        Assert.True(
            options.ProbeTimeout * options.NumMissedProbesLimit
                > orleansDefaults.ProbeTimeout * orleansDefaults.NumMissedProbesLimit,
            "The silo's probe window must be wider than Orleans' default window.");
    }

    [Fact]
    public void ExplicitMembershipConfiguration_RegisteredFirst_StillWins()
    {
        var options = ResolveMembershipOptions(silo =>
        {
            silo.Services.Configure<ClusterMembershipOptions>(o =>
            {
                o.ProbeTimeout = TimeSpan.FromSeconds(1);
                o.NumMissedProbesLimit = 2;
                o.NumVotesForDeathDeclaration = 1;
            });
            silo.ConfigureMeshWeaverServer();
        });

        Assert.Equal(TimeSpan.FromSeconds(1), options.ProbeTimeout);
        Assert.Equal(2, options.NumMissedProbesLimit);
        Assert.Equal(1, options.NumVotesForDeathDeclaration);
    }

    private static ClusterMembershipOptions ResolveMembershipOptions(Action<ISiloBuilder> configureSilo)
    {
        var builder = global::Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings());
        builder.UseOrleans(configureSilo);
        using var provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ClusterMembershipOptions>>().Value;
    }
}