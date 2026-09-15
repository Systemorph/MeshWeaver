using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The instance's OWN database release (Doc/Architecture/InClusterDatabases): the record's
/// <see cref="DeploymentContent.InClusterDatabase"/> decides the host and user every renderer emits,
/// through the ONE shared derivation.
/// </summary>
public class InClusterDatabaseTest
{
    private static readonly DeploymentContent Pearl = new DeploymentContent()
        .WithNamespace("pearl").WithDatabase("pearl");

    [Fact]
    public void ARecordWithoutADatabaseReleaseKeepsTheServerDerivation()
    {
        var d = Pearl.WithDatabase("pearl", server: "memexaks-pg", username: "memexadmin");
        Assert.Null(DeploymentPortalConfig.DatabaseRelease(d));
        Assert.Equal("memexaks-pg.postgres.database.azure.com", DeploymentPortalConfig.DatabaseHost(d));
        Assert.Equal("memexadmin", DeploymentPortalConfig.DatabaseUsername(d));
    }

    [Fact]
    public void ABlankReleaseIsNamespaceDashDbAndThePortalConnectsToItsPrimary()
    {
        var d = Pearl.WithInClusterDatabase();
        Assert.Equal("pearl-db", DeploymentPortalConfig.DatabaseRelease(d));
        Assert.Equal("pearl-db-rw", DeploymentPortalConfig.DatabaseHost(d));
        Assert.Equal("memex", DeploymentPortalConfig.DatabaseUsername(d));
        Assert.Equal(2, DeploymentPortalConfig.DatabaseReleaseInstances(d));
        Assert.Equal("32Gi", DeploymentPortalConfig.DatabaseReleaseSize(d));
        Assert.Equal("memex-db-premiumv2", DeploymentPortalConfig.DatabaseReleaseStorageClass(d));
        Assert.Equal("jdbc:postgresql://pearl-db-rw:5432/pearl", DeploymentPortalConfig.JdbcConnectionString(d));
    }

    [Fact]
    public void AStatedReleaseWinsOverAStatedServer()
    {
        // The exclusivity is a SpecProblem in the Hosting renderer; the derivation itself must still
        // answer with the release, never the shared server — the data rule the pattern exists for.
        var d = Pearl.WithDatabase("pearl", server: "memexaks-pg").WithInClusterDatabase("pearl-main", 3, "64Gi", "fast");
        Assert.Equal("pearl-main-rw", DeploymentPortalConfig.DatabaseHost(d));
        Assert.Equal(3, DeploymentPortalConfig.DatabaseReleaseInstances(d));
        Assert.Equal("64Gi", DeploymentPortalConfig.DatabaseReleaseSize(d));
        Assert.Equal("fast", DeploymentPortalConfig.DatabaseReleaseStorageClass(d));
    }

    [Fact]
    public void TheSharedHelmConfigCarriesTheReleaseHost()
    {
        var c = DeploymentPortalConfig.PortalConfig(Pearl.WithInClusterDatabase(), PortalConfigOptions.Helm);
        Assert.Equal("pearl-db-rw", c["MEMEX_HOST"]);
        Assert.Equal("memex", c["MEMEX_USERNAME"]);
    }

    [Fact]
    public void AReleaseWithNoNameAndNoNamespaceIsNotGuessed()
    {
        var d = new DeploymentContent().WithInClusterDatabase();
        Assert.Null(DeploymentPortalConfig.DatabaseRelease(d));
    }
}
