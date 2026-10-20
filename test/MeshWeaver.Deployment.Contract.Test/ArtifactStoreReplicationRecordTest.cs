using System.Collections.Immutable;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The shared artifact store's increments 3–5 on the record (Doc/Architecture/SharedArtifactStore):
/// the shared MODULE tier every role reads and only the owner publishes to; the OWNER's one-way
/// upstream sync (a client environment's control replicating from ours) declared by registry, sources
/// and a pull-Secret NAME; and the OWNER's local-source builds. Every shape that would give a prefix a
/// second producer, put a credential value in a record, or make a consumer replicate is named at
/// plan time.
/// </summary>
public class ArtifactStoreReplicationRecordTest
{
    private const string ClientControlJson = """
        {
          "$type": "DeploymentContent",
          "host": "control.fabrikam.example.com",
          "volumes": [
            { "name": "data", "claimName": "control-data", "mountPath": "/data" },
            { "name": "artifacts", "claimName": "control-artifacts", "mountPath": "/artifacts", "size": "512Gi", "storageClass": "azurefile-memex", "create": true }
          ],
          "artifactStore": {
            "role": "Owner",
            "volume": "artifacts",
            "upstream": { "registry": "cr.example.com", "sources": ["plugins"], "pullSecret": "upstream-pull", "identities": ["c003e002"] },
            "localSources": [ { "source": "fabrikam-models", "repository": "https://git.example.com/fabrikam/models.git", "ref": "main", "credentialSecret": "fabrikam-git" } ],
            "testerImageRepository": "cr.example.com/mw-plugin-test"
          }
        }
        """;

    private static DeploymentContent ClientControl() => DeploymentRecordJson.Read(ClientControlJson)!;

    private static DeploymentContent Consumer() => new()
    {
        Volumes =
        [
            new VolumeClaim { Name = "data", ClaimName = "d", MountPath = "/data" },
            new VolumeClaim { Name = "artifacts", ClaimName = "artifact-store", MountPath = "/artifacts", ReadOnly = true },
        ],
        ArtifactStore = new ArtifactStoreSpec { Role = "Consumer", Volume = "artifacts", SourceNamespace = "control", SourceClaim = "control-artifacts" },
    };

    private static string Problems(DeploymentContent d) => string.Join(" | ", DeploymentPortalConfig.ArtifactStoreProblems(d));

    [Fact]
    public void TheRecordsJson_CarriesUpstreamAndLocalSources_AndWritesThemBack()
    {
        var record = ClientControl();
        var store = record.ArtifactStore!;

        Assert.Equal("cr.example.com", store.Upstream!.Registry);
        Assert.Equal(["plugins"], store.Upstream.Sources);
        Assert.Equal("upstream-pull", store.Upstream.PullSecret);
        Assert.Equal("fabrikam-models", Assert.Single(store.LocalSources).Source);
        var reread = DeploymentRecordJson.Read(DeploymentRecordJson.Write(record))!.ArtifactStore!;
        Assert.Equal(store.Upstream.Sources, reread.Upstream!.Sources);
        Assert.Equal(store.LocalSources.Single(), reread.LocalSources.Single());
        Assert.Empty(DeploymentPortalConfig.ArtifactStoreProblems(record));
    }

    [Fact]
    public void EveryRole_ReadsTheSharedModuleTier_AndOnlyTheOwnerPublishesToIt()
    {
        var owner = DeploymentPortalConfig.PortalConfig(ClientControl());
        Assert.Equal("/artifacts/{rid}/modules", owner["Modules__SharedRoot"]);
        Assert.Equal("true", owner["Modules__PublishToShared"]);

        foreach (var options in new[] { PortalConfigOptions.Helm, PortalConfigOptions.Aspire("http://localhost:8080") })
        {
            var consumer = DeploymentPortalConfig.PortalConfig(Consumer(), options);
            Assert.Equal("/artifacts/{rid}/modules", consumer["Modules__SharedRoot"]);
            Assert.False(consumer.ContainsKey("Modules__PublishToShared"), "a consumer must never publish into a store it mounts read-only");
        }

        var outside = DeploymentPortalConfig.PortalConfig(new DeploymentContent());
        Assert.False(outside.ContainsKey("Modules__SharedRoot"));
        Assert.False(outside.ContainsKey("Modules__PublishToShared"));
    }

    [Fact]
    public void TheUpstreamSync_IsTheOwnersAlone_AndDeclaredByName()
    {
        Assert.Equal("cr.example.com", DeploymentPortalConfig.ArtifactStoreUpstreamRegistry(ClientControl()));
        Assert.Equal(ArtifactStoreSpec.DefaultLocalBuildSchedule, DeploymentPortalConfig.ArtifactStoreUpstreamSchedule(ClientControl()));

        var replicatingConsumer = Consumer() with
        {
            ArtifactStore = Consumer().ArtifactStore! with { Upstream = new ArtifactStoreUpstream { Registry = "cr.example.com", Sources = ["plugins"], PullSecret = "p" } },
        };
        Assert.Null(DeploymentPortalConfig.ArtifactStoreUpstreamRegistry(replicatingConsumer));
        Assert.Contains("only the OWNER", Problems(replicatingConsumer));
        var buildingConsumer = Consumer() with
        {
            ArtifactStore = Consumer().ArtifactStore! with { LocalSources = [new ArtifactStoreLocalSource { Source = "x", Repository = "https://git.example.com/x.git" }] },
        };
        Assert.Contains("built by the environment's control", Problems(buildingConsumer));
    }

    [Fact]
    public void EveryBrokenReplicationShape_IsNamed()
    {
        var c = ClientControl();
        DeploymentContent Up(ArtifactStoreUpstream u) => c with { ArtifactStore = c.ArtifactStore! with { Upstream = u } };
        DeploymentContent Locals(params ArtifactStoreLocalSource[] l) => c with { ArtifactStore = c.ArtifactStore! with { LocalSources = [.. l] } };
        var up = c.ArtifactStore!.Upstream!;
        var local = c.ArtifactStore.LocalSources[0];

        Assert.Contains("names no Secret", Problems(Up(up with { PullSecret = null })));
        Assert.Contains("states no sources", Problems(Up(up with { Sources = [] })));
        Assert.Contains("is not a registry host", Problems(Up(up with { Registry = "https://cr.example.com" })));
        Assert.Contains("not a registry name component", Problems(Up(up with { Sources = ["Plugins/x"] })));
        Assert.Contains("not a framework identity", Problems(Up(up with { Identities = ["c003-e001"] })));
        Assert.Contains("which upstream also replicates", Problems(Locals(local with { Source = "plugins" })));
        Assert.Contains("twice", Problems(Locals(local, local)));
        Assert.Contains("no https repository", Problems(Locals(local with { Repository = "git@git.example.com:x.git" })));
        Assert.Contains("not a branch or tag", Problems(Locals(local with { Ref = "main;rm -rf" })));
        Assert.Contains("not a Secret name", Problems(Locals(local with { CredentialSecret = "Bad_Name" })));
        Assert.Contains("at most 36 characters", Problems(Locals(local with { Source = new string('a', 37) })));
        Assert.Contains("testerImageRepository", Problems(c with { ArtifactStore = c.ArtifactStore with { TesterImageRepository = "cr.example.com/mw-plugin-test:3.1" } }));
        Assert.Contains("testerImageRepository", Problems(c with { ArtifactStore = c.ArtifactStore with { TesterImageRepository = null } }));
        // An upstream carrying only identities is a replication somebody asked for — validated, never silently dropped.
        Assert.Contains("names no Secret", Problems(Up(new ArtifactStoreUpstream { Identities = ["c003e002"] })));
        Assert.Contains("only the OWNER", Problems(Consumer() with { ArtifactStore = Consumer().ArtifactStore! with { Upstream = new ArtifactStoreUpstream { Identities = ["c003e002"] } } }));
        Assert.Contains("localBuildModuleSources entry", Problems(c with { ArtifactStore = c.ArtifactStore with { LocalBuildModuleSources = ["Bad/Name"] } }));
        Assert.Contains("but no localSources", Problems(c with { ArtifactStore = c.ArtifactStore with { LocalSources = [], LocalBuildModuleSources = ["plugins"] } }));
    }

    [Fact]
    public void ALocalBuild_CompilesAgainstTheDeclaredModuleSources_ElseTheUpstreams()
    {
        var c = ClientControl();
        Assert.Equal(["plugins"], DeploymentPortalConfig.ArtifactStoreLocalBuildModuleSources(c));
        // Our own control: no upstream, its store filled by the build server — the module sources are declared.
        var ours = c with { ArtifactStore = c.ArtifactStore! with { Upstream = null, LocalBuildModuleSources = ["plugins", "education"] } };
        Assert.Equal(["plugins", "education"], DeploymentPortalConfig.ArtifactStoreLocalBuildModuleSources(ours));
        Assert.Empty(DeploymentPortalConfig.ArtifactStoreProblems(ours));
    }
}
