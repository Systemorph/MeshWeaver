using System.Collections.Immutable;
using Memex.Portal.Shared.Setup;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The first-run wizard's decision: answers in, instance manifest out — or a refusal.
///
/// <para>Every refusal asserted here is one the operator can still fix while the mistake is cheap.
/// After this the answer is DURABLE and the process restarts, so the same mistake surfaces as a
/// boot failure whose message names a symptom (<c>Unknown storage type</c>, a 400 at a token
/// endpoint, a blank model picker) rather than the choice that caused it.</para>
/// </summary>
public class SetupCompositionTest
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-03T10:00:00Z");
    private static readonly SetupStrings Strings = new("en");

    private static readonly IProviderKeyProtector Protector =
        new ProviderKeyProtector(new LiteralMasterKeyProvider(Convert.ToBase64String(new byte[32])));

    private static readonly StorageBackendCatalog Backends =
        new(["Sqlite", "PostgreSql", "FileSystem"]);

    private static readonly SetupCatalog Catalog = new(
        Storage:
        [
            new SetupStorageOption("Sqlite", "SQLite"),
            new SetupStorageOption("PostgreSql", "PostgreSQL", NeedsConnectionString: true),
            new SetupStorageOption("FileSystem", "Files", NeedsBasePath: true),
        ],
        SignIn:
        [
            new SetupSignInOption("Dev", "Developer login", "Authentication", IsSwitch: true),
            new SetupSignInOption("Microsoft", "Microsoft", "Authentication:Microsoft", HasTenant: true),
            new SetupSignInOption("GitHub", "GitHub", "GitHub:OAuth"),
        ],
        Ai:
        [
            new SetupAiOption("Anthropic", "Anthropic", "Anthropic"),
            new SetupAiOption("OpenAICompatible", "Local / OpenAI-compatible", "OpenAICompatible",
                RequiresApiKey: false, TakesEndpoint: true),
        ],
        Modules:
        [
            new SetupModuleOption("MeshWeaver.Hosting.Grpc.dll", "gRPC", PreSelected: true),
            new SetupModuleOption("MeshWeaver.Speech.dll", "Speech"),
        ]);

    private static SetupPlan Compose(SetupAnswers answers)
        => SetupComposition.Compose(answers, Catalog, Backends, Protector, Strings, Now);

    /// <summary>Composes with NO protector — the state of an install that could neither be given a
    /// master key nor mint one. Separate from <see cref="Compose"/> because a nullable parameter
    /// defaulting to the real protector cannot express "explicitly none", which is the whole case
    /// under test.</summary>
    private static SetupPlan ComposeUnprotected(SetupAnswers answers)
        => SetupComposition.Compose(answers, Catalog, Backends, protector: null, Strings, Now);

    private static SetupAnswers Minimal() => new()
    {
        StorageType = "Sqlite",
        EnableDevLogin = true,
        DevAdminUsers = "roland",
        EmbeddingEndpoint = "http://localhost:11434/v1",
    };

    [Fact]
    public void TheDefaultAnswers_ProduceACompleteManifest()
    {
        var plan = Compose(Minimal());

        Assert.Empty(plan.Problems);
        Assert.NotNull(plan.Manifest);
        Assert.Equal(InstanceSetupState.Complete, plan.Manifest!.State);
        Assert.Equal("Sqlite", plan.Manifest.Storage!.Type);
        Assert.True(plan.Manifest.SignIn!.EnableDevLogin);
        Assert.Equal(Now, plan.Manifest.SetUpAt);
    }

    [Fact]
    public void ABackendTheImageDoesNotShip_IsRefusedHere_NotAtTheNextBoot()
    {
        // 🚨 Recording an unshipped backend is a DURABLE answer that fails after the wizard is
        // gone, with "Unknown storage type: 'Cosmos'" and no surface left to correct it on.
        var plan = Compose(Minimal() with { StorageType = "Cosmos" });

        Assert.Null(plan.Manifest);
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("Cosmos", problem);
        // Naming what IS available turns a dead end into a next step.
        Assert.Contains("Sqlite", problem);
    }

    [Fact]
    public void ABackendThatNeedsAConnectionString_IsRefusedWithout()
    {
        var plan = Compose(Minimal() with { StorageType = "PostgreSql" });

        Assert.Null(plan.Manifest);
        Assert.Contains(plan.Problems, p => p.Contains("PostgreSQL"));
    }

    [Fact]
    public void NoSignInRouteAtAll_IsRefused_BecauseNobodyCouldEnter()
    {
        var plan = Compose(Minimal() with { EnableDevLogin = false, DevAdminUsers = null });

        Assert.Null(plan.Manifest);
        Assert.Contains(plan.Problems, p => p.Contains("sign in"));
    }

    [Fact]
    public void AProviderWithAClientIdButNoSecret_IsRefused()
    {
        // 🚨 Strictly worse than an absent provider: the button renders, the operator believes
        // sign-in works, and it fails at the token exchange the first time somebody uses it.
        var plan = Compose(Minimal() with
        {
            SignIn = [new SetupSignInAnswer("Microsoft", "client-id", null, null)],
        });

        Assert.Null(plan.Manifest);
        Assert.Contains(plan.Problems, p => p.Contains("Microsoft") && p.Contains("secret"));
    }

    [Fact]
    public void AConfiguredProvider_IsStoredEncrypted_WithTheCatalogsSection()
    {
        var plan = Compose(Minimal() with
        {
            SignIn = [new SetupSignInAnswer("GitHub", "gh-client", null, "gh-secret")],
        });

        Assert.Empty(plan.Problems);
        var provider = Assert.Single(plan.Manifest!.SignIn!.Providers);
        Assert.Equal("GitHub", provider.Name);
        // The section comes from the catalog, so GitHub's hand-rolled OAuth keys land on
        // GitHub:OAuth rather than a composed-from-the-name Authentication:GitHub that nothing reads.
        Assert.Equal("GitHub:OAuth", provider.Section);
        // 🚨 Never the plaintext. A manifest is copied, backed up and pasted into issues.
        Assert.StartsWith("enc:v1:", provider.ClientSecret);
        Assert.DoesNotContain("gh-secret", provider.ClientSecret);
    }

    [Fact]
    public void WithNoMasterKey_ASecretIsRefused_NeverStoredInTheClear()
    {
        // The 2026-08-24 shape: an unconfigured deployment persisted a live provider key in
        // cleartext with nothing failing and nothing logged. Refusing loudly is the cure.
        var plan = ComposeUnprotected(
            Minimal() with { SignIn = [new SetupSignInAnswer("GitHub", "gh", null, "gh-secret")] });

        Assert.Null(plan.Manifest);
        Assert.Contains(plan.Problems, p => p.Contains("MasterKey"));
    }

    [Fact]
    public void AModelProviderRequiringAKey_IsRefusedWithout_ButAnUntouchedRowIsSimplyNotChosen()
    {
        // An untouched row must not become an empty provider that then looks configured-but-broken
        // in the model picker — so "no key AND no endpoint" is "not chosen", not an error.
        var untouched = Compose(Minimal() with
        {
            Ai = [new SetupAiAnswer("Anthropic", null, null)],
        });
        Assert.Empty(untouched.Problems);
        Assert.Empty(untouched.Manifest!.Ai!.Providers);

        // …but an endpoint with no key on a provider that requires one is a half-answer.
        var half = Compose(Minimal() with
        {
            Ai = [new SetupAiAnswer("Anthropic", null, "https://api.anthropic.com/v1/messages")],
        });
        Assert.Null(half.Manifest);
        Assert.Contains(half.Problems, p => p.Contains("Anthropic"));
    }

    [Fact]
    public void AKeylessProvider_IsAcceptedOnItsEndpointAlone()
    {
        // A local Ollama has no API key by design; demanding one would make the offline
        // configuration impossible to express.
        var plan = Compose(Minimal() with
        {
            Ai = [new SetupAiAnswer("OpenAICompatible", null, "http://localhost:11434/v1")],
        });

        Assert.Empty(plan.Problems);
        var provider = Assert.Single(plan.Manifest!.Ai!.Providers);
        Assert.Null(provider.ApiKey);
        Assert.Equal("http://localhost:11434/v1", provider.Endpoint);
    }

    [Fact]
    public void NoEmbeddingsEndpoint_Warns_BecauseSearchWouldDegradeSilently()
    {
        // 🚨 This warning is the whole reason the embeddings question is asked. Without an embedder
        // every write stores a NULL embedding and the vector provider contributes nothing — search
        // falls back to lexical with no error, no log line, and no way to tell from the outside.
        var plan = Compose(Minimal() with { EmbeddingEndpoint = null });

        Assert.Empty(plan.Problems);
        Assert.NotNull(plan.Manifest);
        Assert.Contains(plan.Warnings, w => w.Contains("meaning"));
        Assert.Null(plan.Manifest!.Ai!.Embeddings);
    }

    [Fact]
    public void AnEmbeddingsEndpointWithNoModel_TakesTheSameDefaultTheCodeDoes()
    {
        var plan = Compose(Minimal() with { EmbeddingModel = null });

        Assert.Equal(
            InstanceEmbeddingsSelection.DefaultModel,
            plan.Manifest!.Ai!.Embeddings!.Model);
    }

    [Fact]
    public void DevLoginWithNoAdministrator_Warns_ButIsAllowed()
    {
        // Legal — someone else may hold Auth:GlobalAdmins — so a warning, not a refusal. But it is
        // the "portal you can log into and administer nothing on" state, worth saying out loud.
        var plan = Compose(Minimal() with { DevAdminUsers = null });

        Assert.NotNull(plan.Manifest);
        Assert.Contains(plan.Warnings, w => w.Contains("administrator"));
    }

    [Fact]
    public void AModuleTheImageDoesNotShip_IsRefused()
    {
        var plan = Compose(Minimal() with { BootModules = ["Nope.dll"] });

        Assert.Null(plan.Manifest);
        Assert.Contains(plan.Problems, p => p.Contains("Nope.dll"));
    }

    [Fact]
    public void EveryProblemIsReported_NotJustTheFirst()
    {
        // An operator who fixes one refusal and resubmits into the next is being made to walk a
        // list one item at a time — on a form that restarts the instance when it finally passes.
        var plan = Compose(new SetupAnswers { StorageType = "Cosmos", BootModules = ["Nope.dll"] });

        Assert.Null(plan.Manifest);
        Assert.True(plan.Problems.Count >= 3, $"expected storage + sign-in + module, got: {string.Join(" | ", plan.Problems)}");
    }

    [Fact]
    public void NoPackagesChosen_FallsBackToTheDefaultPattern_NotToNothing()
    {
        // 🚨 A hand-typed package list is stale the moment the next package ships, and its symptom
        // lands nowhere near its cause: the new package simply is not there.
        var plan = Compose(Minimal());

        Assert.Equal(InstanceSetupDefaults.ProvisionPackages, plan.Manifest!.ProvisionPackages);
    }

    [Fact]
    public void TheComposedManifest_RoundTripsThroughDiskAndProjectsBack()
    {
        // The two halves joined: what the wizard writes is what the next boot reads as configuration.
        var plan = Compose(Minimal() with
        {
            SignIn = [new SetupSignInAnswer("Microsoft", "m-client", null, "m-secret")],
            Ai = [new SetupAiAnswer("Anthropic", "sk-ant-xyz", null)],
        });
        Assert.Empty(plan.Problems);

        var root = Path.Combine(Path.GetTempPath(), "mw-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            plan.Manifest!.Write(root);
            var entries = InstanceManifestProjection.ToConfiguration(
                InstanceManifest.Read(root), Protector);

            Assert.Equal("Sqlite", entries["Graph:Storage:Type"]);
            Assert.Equal("true", entries["Authentication:EnableDevLogin"]);
            Assert.Equal("m-client", entries["Authentication:Microsoft:ClientId"]);
            Assert.Equal("m-secret", entries["Authentication:Microsoft:ClientSecret"]);
            Assert.Equal("common", entries["Authentication:Microsoft:TenantId"]);
            Assert.Equal("sk-ant-xyz", entries["Anthropic:ApiKey"]);
            Assert.Equal("http://localhost:11434/v1", entries["Embedding:Endpoint"]);

            // …and the file on disk holds no plaintext secret anywhere.
            var raw = File.ReadAllText(InstanceManifest.PathFor(root));
            Assert.DoesNotContain("m-secret", raw);
            Assert.DoesNotContain("sk-ant-xyz", raw);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
