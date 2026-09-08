using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The manifest read as configuration. Every assertion here is a rule whose violation is SILENT —
/// a provider that registers and then fails at the token exchange, a search that quietly matches
/// words instead of meaning, an authority Entra never serves — so none of them would surface as a
/// test failure anywhere else.
/// </summary>
public class InstanceManifestProjectionTest
{
    private static readonly IProviderKeyProtector Protector =
        new ProviderKeyProtector(new LiteralMasterKeyProvider(
            Convert.ToBase64String(new byte[32])));

    [Fact]
    public void AnIncompleteManifest_ProjectsNothing()
    {
        // The wizard's own starting point is AwaitingStorage with a PRE-FILLED backend. Projecting
        // that would boot the instance past its own setup surface on an answer nobody gave.
        var manifest = InstanceSetupDefaults.Manifest();
        Assert.Equal(InstanceSetupState.AwaitingStorage, manifest.State);

        Assert.Empty(InstanceManifestProjection.ToConfiguration(manifest, Protector));
    }

    [Fact]
    public void AnUnreadableManifest_ProjectsNothing()
        => Assert.Empty(InstanceManifestProjection.ToConfiguration(InstanceManifest.Unreadable, Protector));

    [Fact]
    public void NoManifestAtAll_ProjectsNothing()
        => Assert.Empty(InstanceManifestProjection.ToConfiguration(null, Protector));

    [Fact]
    public void Storage_LandsOnTheKeysTheHostAlreadyReads()
    {
        var entries = InstanceManifestProjection.ToConfiguration(
            Complete() with
            {
                Storage = new InstanceStorageSelection
                {
                    Type = "Sqlite",
                    ConnectionString = "Data Source=/data/memex.db",
                },
            },
            Protector);

        Assert.Equal("Sqlite", entries["Graph:Storage:Type"]);
        Assert.Equal("Data Source=/data/memex.db", entries["Graph:Storage:ConnectionString"]);
    }

    [Fact]
    public void TheConnectionString_IsStoredEncrypted_AndRevealedForUse()
    {
        // 🚨 It carries a PASSWORD and lives in the same file as the sign-in secrets and provider
        // keys. Leaving it readable while protecting those was an inconsistency, not a decision: a
        // manifest gets copied to a new volume, backed up, and pasted into an issue when an
        // instance will not boot.
        var stored = Protector.Protect("Host=db;Username=postgres;Password=hunter2");
        Assert.StartsWith("enc:v1:", stored);

        var entries = InstanceManifestProjection.ToConfiguration(
            Complete() with
            {
                Storage = new InstanceStorageSelection { Type = "PostgreSql", ConnectionString = stored },
            },
            Protector);

        // Revealed for the host that must actually open the database…
        Assert.Equal("Host=db;Username=postgres;Password=hunter2", entries["Graph:Storage:ConnectionString"]);
    }

    [Fact]
    public void APlaintextConnectionString_FromAnOlderOrHandWrittenManifest_StillWorks()
        // Reveal() passes an untagged value through unchanged, so this is not a breaking change for
        // a manifest written before the encryption, or one an operator authored by hand.
        => Assert.Equal("Host=db;Database=memex",
            InstanceManifestProjection.ToConfiguration(
                Complete() with
                {
                    Storage = new InstanceStorageSelection
                    {
                        Type = "PostgreSql", ConnectionString = "Host=db;Database=memex",
                    },
                },
                Protector)["Graph:Storage:ConnectionString"]);

    [Fact]
    public void ABlankMicrosoftTenant_BecomesTheWordCommon_NeverEmpty()
    {
        // 🚨 An empty Authentication__Microsoft__TenantId composes the authority
        // login.microsoftonline.com//v2.0 — a URL Entra never serves — and 500-ed every Microsoft
        // sign-in on 2026-08-28. "" and "common" are one character apart in a values file and a
        // whole outage apart in behaviour.
        var entries = InstanceManifestProjection.ToConfiguration(
            WithProvider(new InstanceSignInProvider
            {
                Name = "Microsoft",
                Section = "Authentication:Microsoft",
                ClientId = "abc",
                TenantId = "   ",
                ClientSecret = Protector.Protect("shh"),
            }),
            Protector);

        Assert.Equal("common", entries["Authentication:Microsoft:TenantId"]);
    }

    [Fact]
    public void AProviderWithNoClientId_ProjectsAnEmptyKey_NotAnAbsentOne()
    {
        // 🚨 An absent key means "not stated", which on a fleet record inherits the template's
        // value and quietly turns the provider back ON. "" is the deployed shape of off, and every
        // handler's own IsNullOrEmpty gate reads it that way.
        var entries = InstanceManifestProjection.ToConfiguration(
            WithProvider(new InstanceSignInProvider
            {
                Name = "Google", Section = "Authentication:Google", ClientId = "",
            }),
            Protector);

        Assert.True(entries.ContainsKey("Authentication:Google:ClientId"));
        Assert.Equal("", entries["Authentication:Google:ClientId"]);
        // …and nothing else for that provider: an off provider has no secret to reveal.
        Assert.False(entries.ContainsKey("Authentication:Google:ClientSecret"));
    }

    [Fact]
    public void TheSectionIsTakenFromTheManifest_SoGitHubLandsOnGitHubOAuth()
    {
        // GitHub's hand-rolled OAuth endpoints read GitHub:OAuth, NOT Authentication:GitHub. A rule
        // that composed the section from the name would put its credentials where nothing looks.
        var entries = InstanceManifestProjection.ToConfiguration(
            WithProvider(new InstanceSignInProvider
            {
                Name = "GitHub",
                Section = "GitHub:OAuth",
                ClientId = "gh-id",
                ClientSecret = Protector.Protect("gh-secret"),
            }),
            Protector);

        Assert.Equal("gh-id", entries["GitHub:OAuth:ClientId"]);
        Assert.Equal("gh-secret", entries["GitHub:OAuth:ClientSecret"]);
        Assert.False(entries.ContainsKey("Authentication:GitHub:ClientId"));
    }

    [Fact]
    public void AnEncryptedSecretWithNoProtector_IsDropped_NeverPassedThrough()
    {
        // 🚨 Emitting ciphertext as a client secret produces a provider that registers, renders its
        // button, and fails at the token exchange with an error naming the endpoint — the failure
        // landing furthest from the missing master key that caused it. Absent is strictly better:
        // the provider does not register, and the operator sees no button to click.
        var entries = InstanceManifestProjection.ToConfiguration(
            WithProvider(new InstanceSignInProvider
            {
                Name = "Microsoft",
                Section = "Authentication:Microsoft",
                ClientId = "abc",
                ClientSecret = Protector.Protect("shh"),
            }),
            protector: null);

        Assert.Equal("abc", entries["Authentication:Microsoft:ClientId"]);
        Assert.False(entries.ContainsKey("Authentication:Microsoft:ClientSecret"));
    }

    [Fact]
    public void ASecretEncryptedUnderADifferentKey_IsDropped_NotEmittedAsCiphertext()
    {
        // A rotated or truncated master key: Unprotect answers the input unchanged, which is still
        // ciphertext, which is still unusable. Same reasoning as the no-protector case.
        var other = new ProviderKeyProtector(new LiteralMasterKeyProvider("a-different-key"));
        var entries = InstanceManifestProjection.ToConfiguration(
            WithProvider(new InstanceSignInProvider
            {
                Name = "Microsoft",
                Section = "Authentication:Microsoft",
                ClientId = "abc",
                ClientSecret = other.Protect("shh"),
            }),
            Protector);

        Assert.False(entries.ContainsKey("Authentication:Microsoft:ClientSecret"));
    }

    [Fact]
    public void ModelProviderKeys_LandOnTheSectionTheProviderPackageBinds()
    {
        var entries = InstanceManifestProjection.ToConfiguration(
            Complete() with
            {
                Ai = new InstanceAiSelection
                {
                    Providers =
                    [
                        new InstanceAiProvider
                        {
                            Name = "Anthropic",
                            Section = "Anthropic",
                            ApiKey = Protector.Protect("sk-ant-xyz"),
                        },
                        new InstanceAiProvider
                        {
                            Name = "OpenAICompatible",
                            Section = "OpenAICompatible",
                            Endpoint = "http://ollama:11434/v1",
                            Models = ["qwen3.6-code"],
                        },
                    ],
                },
            },
            Protector);

        Assert.Equal("sk-ant-xyz", entries["Anthropic:ApiKey"]);
        Assert.Equal("http://ollama:11434/v1", entries["OpenAICompatible:Endpoint"]);
        // Models bind as an array; the indexed colon form IS an IConfiguration array.
        Assert.Equal("qwen3.6-code", entries["OpenAICompatible:Models:0"]);
    }

    [Fact]
    public void Embeddings_ProjectTheOpenAiCompatibleBackend_SoVectorSearchIsNotDark()
    {
        // Without these three keys SqliteStorageAdapter writes NULL embeddings and
        // SqliteVectorMeshQuery.Matches answers false — search degrades to lexical with no error
        // and no log line. This projection is what turns the wizard's answer into a live embedder.
        var entries = InstanceManifestProjection.ToConfiguration(
            Complete() with
            {
                Ai = new InstanceAiSelection
                {
                    Embeddings = new InstanceEmbeddingsSelection
                    {
                        Endpoint = "http://localhost:11434/v1",
                        Model = "bge-m3",
                    },
                },
            },
            Protector);

        Assert.Equal("OpenAICompatible", entries["Embedding:Provider"]);
        Assert.Equal("http://localhost:11434/v1", entries["Embedding:Endpoint"]);
        Assert.Equal("bge-m3", entries["Embedding:Model"]);
    }

    [Fact]
    public void AnEndpointlessEmbeddingSelection_IsNotProjectedAsAConfiguredEmbedder()
    {
        var entries = InstanceManifestProjection.ToConfiguration(
            Complete() with
            {
                Ai = new InstanceAiSelection { Embeddings = new InstanceEmbeddingsSelection() },
            },
            Protector);

        Assert.False(entries.ContainsKey("Embedding:Provider"));
    }

    [Fact]
    public void TheRegisteredIdentity_ProjectsTheTokenThatSTOPSASecondRegistration()
    {
        // 🚨 The whole point. InstanceAutoRegistrationService runs on the configured boot and
        // registers whenever it sees an instance id and NO token — so without this projection an
        // instance that had just registered through the wizard registers a SECOND time, under
        // whatever id the deployment happens to carry, and claims another id permanently. Ids are
        // global and never re-issued. With the token present that service takes its own Skip branch
        // ("a registry token is already configured — the explicit token wins").
        var entries = InstanceManifestProjection.ToConfiguration(
            Complete() with
            {
                Identity = new InstanceIdentitySelection
                {
                    Id = "0f8fad5b-d9cb-469f-a165-70867728950e",
                    Name = "Roland laptop",
                    RegistryUrl = "https://memex.meshweaver.cloud",
                    InstanceKey = Protector.Protect("mwi_realkey"),
                    Plan = "free",
                },
            },
            Protector);

        Assert.Equal("https://memex.meshweaver.cloud", entries["PluginCatalog:RegistryUrl"]);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", entries["PluginCatalog:InstanceId"]);
        Assert.Equal("mwi_realkey", entries["PluginCatalog:RegistryToken"]);
    }

    [Fact]
    public void TheSameKey_AlsoBecomesADockerConfigForTheFleetRegistry()
    {
        // One credential, written once. The fleet's OCI registry authenticates an instance as
        // Basic `instance:<key>`, so an install that must pull images — and, once bundles move
        // there, plugin bytes — needs a docker config, and this is the only key it has. Deriving it
        // beside the registry token is what stops a second copy being kept in step by hand.
        var entries = InstanceManifestProjection.ToConfiguration(
            Complete() with
            {
                Identity = new InstanceIdentitySelection
                {
                    Id = "0f8fad5b-d9cb-469f-a165-70867728950e",
                    Name = "Roland laptop",
                    RegistryUrl = "https://memex.meshweaver.cloud",
                    InstanceKey = Protector.Protect("mwi_realkey"),
                },
            },
            Protector);

        var config = entries["ContainerRegistry:DockerConfigJson"];
        Assert.NotNull(config);

        using var parsed = System.Text.Json.JsonDocument.Parse(config!);
        var auth = parsed.RootElement
            .GetProperty("auths").GetProperty("cr.meshweaver.cloud").GetProperty("auth").GetString();
        Assert.Equal("instance:mwi_realkey",
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth!)));
    }

    [Fact]
    public void NoDockerConfig_WithoutADecryptableKey()
        // Same rule as the registry token: a credential that cannot be read is worse than absent —
        // it would authenticate nothing while looking configured.
        => Assert.False(
            InstanceManifestProjection.ToConfiguration(
                Complete() with
                {
                    Identity = new InstanceIdentitySelection
                    {
                        Id = "0f8fad5b-d9cb-469f-a165-70867728950e",
                        Name = "Roland laptop",
                        RegistryUrl = "https://memex.meshweaver.cloud",
                        InstanceKey = new ProviderKeyProtector(
                            new LiteralMasterKeyProvider("a-different-key")).Protect("mwi_realkey"),
                    },
                },
                Protector).ContainsKey("ContainerRegistry:DockerConfigJson"));

    [Fact]
    public void AnIdentityWhoseKeyCannotBeDecrypted_ProjectsNOTHING()
    {
        // 🚨 Not "projects the id without the token" — that is the shape that would register a
        // second time. A registry token that is still ciphertext authenticates nothing either: every
        // fetch 401s, the catalog looks empty, and the instance appears to have been granted
        // nothing while its id sits claimed. Dropping the whole identity leaves auto-registration to
        // report the real problem instead.
        var other = new ProviderKeyProtector(new LiteralMasterKeyProvider("a-different-key"));
        var entries = InstanceManifestProjection.ToConfiguration(
            Complete() with
            {
                Identity = new InstanceIdentitySelection
                {
                    Id = "0f8fad5b-d9cb-469f-a165-70867728950e",
                    Name = "Roland laptop",
                    RegistryUrl = "https://memex.meshweaver.cloud",
                    InstanceKey = other.Protect("mwi_realkey"),
                },
            },
            Protector);

        Assert.False(entries.ContainsKey("PluginCatalog:RegistryToken"));
        Assert.False(entries.ContainsKey("PluginCatalog:InstanceId"));
    }

    [Fact]
    public void NoIdentityAtAll_ProjectsNoPluginCatalogKeys()
        // The ordinary state of every deployment configured through appsettings — it must stay
        // byte-identical, and in particular must not have an empty registry token invented for it.
        => Assert.DoesNotContain(
            InstanceManifestProjection.ToConfiguration(Complete(), Protector).Keys,
            k => k.StartsWith("PluginCatalog:", StringComparison.OrdinalIgnoreCase));

    private static InstanceManifest Complete() => new()
    {
        State = InstanceSetupState.Complete,
        Storage = new InstanceStorageSelection { Type = "Sqlite" },
    };

    private static InstanceManifest WithProvider(InstanceSignInProvider provider) =>
        Complete() with
        {
            SignIn = new InstanceSignInSelection
            {
                EnableDevLogin = false,
                Providers = ImmutableList.Create(provider),
            },
        };
}
