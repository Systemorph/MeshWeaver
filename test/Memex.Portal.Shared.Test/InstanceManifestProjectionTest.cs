using System.Collections.Immutable;
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
