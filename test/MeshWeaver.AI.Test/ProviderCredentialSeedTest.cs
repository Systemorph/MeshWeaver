#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Shared fixture for the seed tests: a DB-synced <c>Provider</c> partition (the ONLY shape where a
/// provider node can outlive the configuration that created it), one catalog source, and a
/// configuration whose values the test MUTATES at runtime — which is the whole point, because the
/// case being pinned is "the key was configured AFTER the node existed".
/// </summary>
public abstract class ProviderCredentialSeedTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Config section + provider name of the seeded provider.</summary>
    protected const string Section = "SeedProbe";

    /// <summary>The model id the catalog source lists — a child of <c>Provider/SeedProbe</c>.</summary>
    protected const string ModelId = "seed-probe-model";

    protected const string ConfiguredEndpoint = "https://seed.example/v1/messages";

    /// <summary>Node path of the provider the seed converges.</summary>
    protected static string ProviderPath => $"{ModelProviderNodeType.RootNamespace}/{Section}";

    /// <summary>Node path of its single model child.</summary>
    protected static string ModelPath => $"{ProviderPath}/{ModelId}";

    /// <summary>
    /// The live DEPLOYMENT configuration root. Mutable on purpose: <c>root[key] = value</c> writes through to the
    /// in-memory provider, which is how a test reproduces "the deployment gained a key at 14:00 on a
    /// node created on the 14th".
    /// </summary>
    protected IConfigurationRoot DeploymentConfiguration { get; } = new ConfigurationBuilder()
        .Add(new MemoryConfigurationSource())
        .Build();

    /// <summary>Whether this fixture configures <c>Ai:KeyProtection:MasterKey</c>.</summary>
    protected abstract bool WithMasterKey { get; }

    protected override bool ShareMeshAcrossTests => false;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // The provider's models are configured; its KEY deliberately is NOT — that is the memex
        // shape, where Provider/Anthropic was created keyless and the key arrived days later.
        DeploymentConfiguration[$"{Section}:Models:0"] = ModelId;
        DeploymentConfiguration[$"{Section}:Endpoint"] = ConfiguredEndpoint;
        if (WithMasterKey)
            DeploymentConfiguration["Ai:KeyProtection:MasterKey"] = "test-master-key-provider-seed-do-not-use";

        return base.ConfigureMesh(builder)
            // DB-synced Provider partition, exactly as the portal wires it.
            .AddAI(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ModelProviderNodeType.RootNamespace })
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: Section, ProviderName: Section, Order: 1,
                DisplayLabel: "Seed probe", DefaultEndpoint: ConfiguredEndpoint,
                DefaultModelIds: ImmutableArray<string>.Empty, RequiresApiKey: true))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(DeploymentConfiguration);
                services.AddSingleton<IStaticRepoSource>(sp =>
                    new ModelStaticRepoSource(sp.GetRequiredService<BuiltInLanguageModelProvider>()));
                return services;
            });
    }

    protected CancellationToken Ct => TestContext.Current.CancellationToken;

    protected ChatClientCredentialResolver Resolver =>
        Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();

    /// <summary>Materialises the catalog into the partition — the boot import.</summary>
    protected async Task ImportAsync()
    {
        var results = await StaticRepoImporter.ImportAll(Mesh).ToList().FirstAsync().ToTask(Ct);
        foreach (var r in results)
            Output.WriteLine($"import: partition={r.Partition} outcome={r.Outcome} count={r.Count}");
    }

    /// <summary>Runs the seed and returns every result it emitted.</summary>
    protected async Task<IReadOnlyList<ProviderCredentialSeedResult>> SeedAsync()
    {
        var results = await ProviderCredentialSeed.Run(Mesh).ToList().FirstAsync().ToTask(Ct);
        foreach (var r in results)
            Output.WriteLine($"seed: path={r.ProviderPath} section={r.Section} outcome={r.Outcome} detail={r.Detail}");
        return results.ToList();
    }

    /// <summary>
    /// The provider node's STORED content, read through a query (never a point stream that could
    /// block on an absent node).
    /// </summary>
    protected async Task<ModelProviderConfiguration?> ReadProviderAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var items = await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{ProviderPath}"))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select(c => (IReadOnlyList<MeshNode>)(c.Items ?? []))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(Ct);

        var node = items.FirstOrDefault(n =>
            string.Equals(n.Path, ProviderPath, StringComparison.OrdinalIgnoreCase));
        return node.ContentAs<ModelProviderConfiguration>(Mesh.JsonSerializerOptions);
    }

    /// <summary>Waits until the provider node exists in the partition (the import landed it).</summary>
    protected async Task<ModelProviderConfiguration> WaitForProviderNodeAsync()
    {
        var cfg = await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => Observable.FromAsync(ReadProviderAsync))
            .Where(c => c is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(60))
            .FirstAsync()
            .ToTask(Ct);
        return cfg!;
    }
}

/// <summary>
/// 🔑 The seed, on a deployment that HAS a key-protection master key.
///
/// <para>What it pins is MeshWeaver#1982's whole premise: a provider node created before its key was
/// configured must CONVERGE, the value that lands must be encrypted at rest, and an administered key
/// must survive every later boot untouched. The measured incident is the fixture: on
/// <c>memex.systemorph.com</c> <c>Provider/Anthropic</c> was created keyless on 2026-08-14, the
/// deployment gained <c>Anthropic__ApiKey</c> afterwards, and the node stayed keyless for a week
/// because the seeder only ever ran at node CREATION.</para>
/// </summary>
public class ProviderCredentialSeedTest(ITestOutputHelper output) : ProviderCredentialSeedTestBase(output)
{
    protected override bool WithMasterKey => true;

    /// <summary>
    /// 🚨 Never written to a log, an assertion message or a test name — a key that has been echoed is
    /// a key that must be rotated, and a fake one teaches the habit that leaks a real one.
    /// </summary>
    private const string ConfiguredKey = "sk-seed-probe-configured-after-the-node-existed";

    [Fact(Timeout = 180000)]
    public async Task KeyConfiguredAfterTheNodeExists_ConvergesOntoTheNode_Encrypted()
    {
        // 1. Boot ONE: the catalog imports with no key configured. This is the create-if-absent node
        //    that the old seeder could never revisit.
        await ImportAsync();
        var beforeKey = await WaitForProviderNodeAsync();
        beforeKey.ApiKey.Should().BeNullOrEmpty(
            "the import must never persist a credential — ModelStaticRepoSource strips it");
        Resolver.EnsureSubscription();

        // 2. The deployment gains the key AFTER the node exists (the memex sequence).
        DeploymentConfiguration[$"{Section}:ApiKey"] = ConfiguredKey;

        // 3. Boot TWO: the seed converges it.
        var results = await SeedAsync();
        results.Should().ContainSingle(r => r.ProviderPath == ProviderPath)
            .Which.Outcome.Should().Be(ProviderSeedOutcome.Seeded,
                "a key configured after the node existed must reach the node — that is the whole issue");

        // 4. AT REST: encrypted, and provably not the literal.
        var afterKey = await WaitForKeyOnNodeAsync();
        afterKey.ApiKey.Should().StartWith("enc:v1:", "the seed must never persist a plaintext credential");
        afterKey.ApiKey.Should().NotContain(ConfiguredKey);
        afterKey.Endpoint.Should().Be(ConfiguredEndpoint);

        // 5. AT USE: the resolver — which reads the NODE and nothing else now — serves it again.
        var resolution = await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .Select(_ => Resolver.Resolve(ModelId))
            .Should().Within(30.Seconds())
            .Match(r => !string.IsNullOrEmpty(r.ApiKey),
                "the seeded node key must resolve for the model");
        resolution.ApiKey.Should().Be(ConfiguredKey, "the stored key decrypts back to what was configured");
        resolution.Source.Should().StartWith("providerRef:",
            "the NODE answered — configuration is a seed, not a resolution rung (#1982)");
        Resolver.HasUsableCredential(ModelId).Should().BeTrue(
            "a model the deployment can serve must never be reported unusable (#1965)");

        // 6. IDEMPOTENT: a third boot leaves the administered value exactly as it is. A seed that is
        //    allowed to run twice is the requirement; one that rewrites on every boot is a write storm.
        var again = await SeedAsync();
        again.Should().ContainSingle(r => r.ProviderPath == ProviderPath)
            .Which.Outcome.Should().Be(ProviderSeedOutcome.AlreadyAdministered);
        var third = await ReadProviderAsync();
        third!.ApiKey.Should().Be(afterKey.ApiKey,
            "re-seeding must not even re-encrypt — a fresh nonce would be a new value on every boot");
    }

    [Fact(Timeout = 180000)]
    public async Task NodeKeyDiffersFromConfiguredKey_TheNodeWins()
    {
        const string administeredKey = "sk-seed-probe-pasted-by-an-admin";

        await ImportAsync();
        await WaitForProviderNodeAsync();

        // An admin rotates the key in the GUI (the write ModelProviderService.RotateKey performs).
        var protector = Mesh.ServiceProvider.GetRequiredService<IProviderKeyProtector>();
        await Mesh.GetWorkspace().GetMeshNodeStream(ProviderPath)
            .Update<ModelProviderConfiguration>(c => c with { ApiKey = protector.Protect(administeredKey) })
            .Take(1)
            .Should().Within(30.Seconds()).Emit();
        Resolver.EnsureSubscription();

        // …and the deployment configures a DIFFERENT (stale) key — the 2026-08-21 OpenRouter shape,
        // where the funded key was on the node and Key Vault still held an unfunded one.
        DeploymentConfiguration[$"{Section}:ApiKey"] = "sk-seed-probe-stale-deployment-value";

        var results = await SeedAsync();
        results.Should().ContainSingle(r => r.ProviderPath == ProviderPath)
            .Which.Outcome.Should().Be(ProviderSeedOutcome.AlreadyAdministered,
                "administered node data always wins — the seed fills an EMPTY field, it never overwrites");

        var resolution = await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .Select(_ => Resolver.Resolve(ModelId))
            .Should().Within(30.Seconds())
            .Match(r => !string.IsNullOrEmpty(r.ApiKey), "the administered key resolves");
        resolution.ApiKey.Should().Be(administeredKey,
            "the node is the administered home; a stale deployment value must not be able to shadow it");
    }

    /// <summary>Waits until the provider node carries a key (the seed's write has landed).</summary>
    private Task<ModelProviderConfiguration> WaitForKeyOnNodeAsync() =>
        Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => Observable.FromAsync(ReadProviderAsync))
            .Where(c => !string.IsNullOrEmpty(c?.ApiKey))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(60))
            .FirstAsync()
            .ToTask(Ct)!;
}

/// <summary>
/// 🚨 The seed on a deployment with NO <c>Ai:KeyProtection:MasterKey</c>.
///
/// <para><see cref="ProviderKeyProtector"/> is a silent plaintext PASSTHROUGH without a master key —
/// so a seed that wrote anyway would put a live credential into Postgres in the clear, and nothing
/// would say so. It must fail LOUDLY instead: refuse the write, leave the node keyless, and log the
/// refusal at Error naming the configuration key to set.</para>
/// </summary>
public class ProviderCredentialSeedWithoutMasterKeyTest(ITestOutputHelper output)
    : ProviderCredentialSeedTestBase(output)
{
    protected override bool WithMasterKey => false;

    private const string ConfiguredKey = "sk-seed-probe-must-never-be-persisted";

    [Fact(Timeout = 180000)]
    public async Task NoMasterKey_RefusesToSeed_RatherThanPersistingPlaintext()
    {
        await ImportAsync();
        await WaitForProviderNodeAsync();

        DeploymentConfiguration[$"{Section}:ApiKey"] = ConfiguredKey;

        var results = await SeedAsync();
        results.Should().ContainSingle(r => r.ProviderPath == ProviderPath)
            .Which.Outcome.Should().Be(ProviderSeedOutcome.RefusedUnprotected,
                "no master key means Protect() is a passthrough — writing would store the credential "
                + "in the clear, which must fail loudly rather than downgrade quietly");

        var stored = await ReadProviderAsync();
        stored!.ApiKey.Should().BeNullOrEmpty("the refusal must leave nothing behind at rest");

        Resolver.EnsureSubscription();
        Resolver.HasUsableCredential(ModelId).Should().BeFalse(
            "a refused seed leaves the model honestly unusable — the operator sets a master key, and "
            + "the next boot converges");
    }
}
