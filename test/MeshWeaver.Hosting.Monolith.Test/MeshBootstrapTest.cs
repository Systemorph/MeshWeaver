using MeshWeaver.Hosting.Monolith.TestBase;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The fluent bootstrap API, pinned WITHOUT a cluster.
///
/// <para>That is the point of the shape: the description of a mesh — in-process, or Orleans with
/// AdoNet membership and Redis grain storage across two silos — is a pure value, so the rules about
/// what can be stood up are checked here in milliseconds rather than discovered minutes into a silo
/// start-up as a connection error that names nothing about the test that asked for it.</para>
/// </summary>
public class MeshBootstrapTest
{
    /// <summary>The default is the simple in-process mesh — what 23 of the 26 mesh-requiring
    /// suites want, so it must be what you get for free.</summary>
    [Fact]
    public void Monolith_IsTheDefaultAndNeedsNoConfiguration()
    {
        var boot = MeshBootstrap.Monolith();
        Assert.Equal("monolith", boot.Name);
        Assert.Same(MonolithBootstrap.Instance, boot);
    }

    /// <summary>An Orleans bootstrap asked for with no options is the localhost, in-memory cluster
    /// the current test base already builds — so adopting the seam changes nothing by itself.</summary>
    [Fact]
    public void Orleans_WithNoOptions_IsTheSelfContainedCluster()
    {
        var boot = Assert.IsType<OrleansBootstrap>(MeshBootstrap.Orleans());
        Assert.Equal(ClusterProvider.Localhost, boot.Options.Clustering);
        Assert.Equal(StorageProvider.Memory, boot.Options.Storage);
        Assert.Equal((short)1, boot.Options.Silos);
        Assert.Equal("orleans[localhost/memory, 1 silo]", boot.Name);
    }

    /// <summary>🚨 THE POINT OF THE FLUENT API: a suite says how its cluster is provisioned —
    /// AdoNet membership, Redis grain storage, two silos — and the description survives verbatim.</summary>
    [Fact]
    public void Orleans_DescribesAdoNetMembershipAndRedisStorage()
    {
        var boot = Assert.IsType<OrleansBootstrap>(MeshBootstrap.Orleans(o => o
            .WithClustering(ClusterProvider.AdoNet, "Server=localhost;Database=orleans")
            .WithGrainStorage(StorageProvider.Redis, "localhost:6379")
            .WithSilos(2)));

        Assert.Equal(ClusterProvider.AdoNet, boot.Options.Clustering);
        Assert.Equal("Server=localhost;Database=orleans", boot.Options.ClusteringConnectionString);
        Assert.Equal(StorageProvider.Redis, boot.Options.Storage);
        Assert.Equal("localhost:6379", boot.Options.StorageConnectionString);
        Assert.Equal((short)2, boot.Options.Silos);
        Assert.Equal("orleans[adonet/redis, 2 silos]", boot.Name);
    }

    /// <summary>The builder is fluent in both directions — order must not change the result.</summary>
    [Fact]
    public void Orleans_TheBuilderIsOrderIndependent()
    {
        var a = ((OrleansBootstrap)MeshBootstrap.Orleans(o => o
            .WithSilos(3).WithGrainStorage(StorageProvider.AdoNet, "cs").WithClustering(ClusterProvider.Redis, "r"))).Options;
        var b = ((OrleansBootstrap)MeshBootstrap.Orleans(o => o
            .WithClustering(ClusterProvider.Redis, "r").WithGrainStorage(StorageProvider.AdoNet, "cs").WithSilos(3))).Options;
        Assert.Equal(a, b);
    }

    /// <summary>
    /// 🚨 A shape that cannot be stood up is refused WHERE IT IS WRITTEN. An external provider with
    /// no connection string would otherwise reach a silo and fail there, minutes later, as a
    /// connection error that says nothing about the test that asked for it.
    /// </summary>
    [Theory]
    [InlineData(ClusterProvider.AdoNet, "needs a connection string")]
    [InlineData(ClusterProvider.Redis, "needs a connection string")]
    public void Orleans_AnExternalProviderWithoutAConnectionString_IsRefused(
        ClusterProvider provider, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            MeshBootstrap.Orleans(o => o.WithClustering(provider)));
        Assert.Contains(expected, ex.Message);
        Assert.Contains(provider.ToString(), ex.Message);
    }

    /// <summary>
    /// The mirror case, and the one that actually misleads: a connection string handed to the
    /// SELF-CONTAINED provider is silently ignored today, so a suite believes it is exercising a
    /// real membership table and is not.
    /// </summary>
    [Fact]
    public void Orleans_AConnectionStringOnLocalhostClustering_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            MeshBootstrap.Orleans(o => o.WithClustering(ClusterProvider.Localhost, "Server=…")));
        Assert.Contains("silently ignored", ex.Message);
    }

    /// <summary>A cluster of zero silos is not a cluster.</summary>
    [Fact]
    public void Orleans_ZeroSilos_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => MeshBootstrap.Orleans(o => o.WithSilos(0)));
        Assert.Contains("at least one silo", ex.Message);
    }

    /// <summary>
    /// Without an applicator the Orleans bootstrap says SO, by name, instead of failing somewhere
    /// inside a silo start-up. The monolith suites must never take an Orleans dependency, which is
    /// why the cluster is described here and stood up elsewhere.
    /// </summary>
    [Fact]
    public void Orleans_WithNoApplicator_SaysWhatIsMissing()
    {
        var previous = OrleansBootstrap.Applicator;
        OrleansBootstrap.Applicator = null;
        try
        {
            var boot = MeshBootstrap.Orleans(o => o.WithSilos(2));
            var ex = Assert.Throws<InvalidOperationException>(() => boot.Bootstrap(null!));
            Assert.Contains("No Orleans applicator is registered", ex.Message);
            Assert.Contains("2 silos", ex.Message);
        }
        finally
        {
            OrleansBootstrap.Applicator = previous;
        }
    }
}
