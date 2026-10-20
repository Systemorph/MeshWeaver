using System.Collections.Immutable;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The record's <c>artifactStore</c> block (Doc/Architecture/SharedArtifactStore): the environment's
/// control instance OWNS one share for the content-addressed build output, every other instance
/// mounts it READ-ONLY. Pinned here: the block round-trips the record's JSON, either role renders the
/// bundle root under the store's mount (so every reader of the root reads the store), a consumer's
/// bundle sweep is disarmed, and every shape that would give the store a second writer — or no store
/// at all — is named at plan time.
/// </summary>
public class ArtifactStoreRecordTest
{
    private const string ConsumerJson = """
        {
          "$type": "DeploymentContent",
          "host": "portal.example.com",
          "volumes": [
            { "name": "data", "claimName": "acme-data", "mountPath": "/data" },
            { "name": "artifacts", "claimName": "artifact-store", "mountPath": "/artifacts", "readOnly": true }
          ],
          "artifactStore": { "role": "Consumer", "volume": "artifacts", "sourceNamespace": "control", "sourceClaim": "control-artifacts" }
        }
        """;

    private static DeploymentContent Owner(bool readOnly = false) => new()
    {
        Volumes =
        [
            new VolumeClaim { Name = "data", ClaimName = "control-data", MountPath = "/data" },
            new VolumeClaim { Name = "artifacts", ClaimName = "control-artifacts", MountPath = "/artifacts/", Size = "512Gi", StorageClass = "azurefile-memex", Create = true, ReadOnly = readOnly },
        ],
        ArtifactStore = new ArtifactStoreSpec { Role = "owner", Volume = "artifacts" },
    };

    [Fact]
    public void TheRecordsJson_CarriesTheBlockAndTheReadOnlyVolume_AndWritesThemBack()
    {
        var record = DeploymentRecordJson.Read(ConsumerJson)!;

        Assert.Equal(("Consumer", "artifacts", "control", "control-artifacts"),
            (record.ArtifactStore!.Role, record.ArtifactStore.Volume, record.ArtifactStore.SourceNamespace, record.ArtifactStore.SourceClaim));
        Assert.True(record.Volumes[1].ReadOnly);
        Assert.False(record.Volumes[0].ReadOnly);

        var reread = DeploymentRecordJson.Read(DeploymentRecordJson.Write(record))!;
        Assert.Equal(record.ArtifactStore, reread.ArtifactStore);
        Assert.True(reread.Volumes[1].ReadOnly);
    }

    [Fact]
    public void AConsumer_ReadsTheBundleRootUnderTheStore_AndItsSweepIsDisarmed()
    {
        var record = DeploymentRecordJson.Read(ConsumerJson)!;

        Assert.Empty(DeploymentPortalConfig.ArtifactStoreProblems(record));
        foreach (var options in new[] { PortalConfigOptions.Helm, PortalConfigOptions.Aspire("http://localhost:8080") })
        {
            var config = DeploymentPortalConfig.PortalConfig(record, options);
            Assert.Equal("/artifacts/prebuilt-bundles", config["PreWarm__PrebuiltBundleRoot"]);
            Assert.Equal("false", config["PreWarm__PrebuiltBundleRetention__Delete"]);
            Assert.Equal("false", config["PreWarm__PrebuiltBundleRetention__Enabled"]);
        }
    }

    [Fact]
    public void TheOwner_ReadsTheSameRoot_AndKeepsItsSweep()
    {
        var config = DeploymentPortalConfig.PortalConfig(Owner());

        Assert.Empty(DeploymentPortalConfig.ArtifactStoreProblems(Owner()));
        Assert.Equal("/artifacts/prebuilt-bundles", config["PreWarm__PrebuiltBundleRoot"]);
        Assert.False(config.ContainsKey("PreWarm__PrebuiltBundleRetention__Delete"));
        Assert.False(config.ContainsKey("PreWarm__PrebuiltBundleRetention__Enabled"));
        Assert.Equal(ArtifactStoreSpec.OwnerRole, DeploymentPortalConfig.ArtifactStoreRole(Owner()));
    }

    [Fact]
    public void ARecordWithoutTheBlock_RendersExactlyAsBefore()
    {
        var record = new DeploymentContent { Volumes = [new VolumeClaim { Name = "data", ClaimName = "d", MountPath = "/data" }] };

        Assert.Null(DeploymentPortalConfig.BundleRoot(record));
        Assert.False(DeploymentPortalConfig.PortalConfig(record).ContainsKey("PreWarm__PrebuiltBundleRoot"));
        Assert.Empty(DeploymentPortalConfig.ArtifactStoreProblems(record));
    }

    [Fact]
    public void AWritableConsumer_IsASecondWriter_AndIsNamed()
    {
        var record = DeploymentRecordJson.Read(ConsumerJson)!;
        var writable = record with { Volumes = record.Volumes.SetItem(1, record.Volumes[1] with { ReadOnly = false }) };

        Assert.Contains(DeploymentPortalConfig.ArtifactStoreProblems(writable), p => p.Contains("CONSUMER mounts 'artifacts' writable"));
    }

    [Fact]
    public void EveryOtherBrokenShape_IsNamed_InTheRecordsWords()
    {
        var consumer = DeploymentRecordJson.Read(ConsumerJson)!;
        string Problems(DeploymentContent d) => string.Join(" | ", DeploymentPortalConfig.ArtifactStoreProblems(d));

        Assert.Contains("read-only", Problems(Owner(readOnly: true)));
        Assert.Contains("names no entry of volumes", Problems(consumer with { ArtifactStore = consumer.ArtifactStore! with { Volume = "nope" } }));
        Assert.Contains("neither Owner", Problems(consumer with { ArtifactStore = consumer.ArtifactStore! with { Role = "Reader" } }));
        Assert.Contains("no sourceNamespace and sourceClaim", Problems(consumer with { ArtifactStore = consumer.ArtifactStore! with { SourceClaim = " " } }));
        Assert.Contains("create: true", Problems(consumer with { Volumes = consumer.Volumes.SetItem(1, consumer.Volumes[1] with { Create = true }) }));
        Assert.Contains("mounted at /data", Problems(consumer with { Volumes = consumer.Volumes.SetItem(1, consumer.Volumes[1] with { MountPath = "/data" }) }));
        Assert.Contains("both bundles.registry and artifactStore", Problems(consumer.WithBundles("cr.example.test", ["plugins"])));
        Assert.Contains("drop them", Problems(Owner() with { ArtifactStore = Owner().ArtifactStore! with { SourceClaim = "x" } }));
        foreach (var reserved in new[] { "data", "users", "dumps" })
        {
            var renamed = consumer with
            {
                Volumes = [consumer.Volumes[1] with { Name = reserved }],
                ArtifactStore = consumer.ArtifactStore! with { Volume = reserved },
            };
            Assert.Contains("give it a dedicated name", Problems(renamed));
        }
    }
}
