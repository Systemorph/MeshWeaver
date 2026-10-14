using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The store's own bootstrap on an image that ships NONE of the modules the store delivers
/// (policies <c>default-install-modules-first</c> and <c>declared-package-set-is-exact</c>) — the shape of a fresh control instance after
/// MeshWeaver.Plugins#2970 took the modules out of the images.
///
/// <para>Measured there, 2026-10-13: <c>Install of 'Store' failed: NodeType(s) not registered: Skill</c>,
/// <c>Install of 'AI' failed: NodeType 'Store/Plugin' is not registered</c>, <c>0 installed, 0 up to
/// date, 48 failed</c>, and an empty <c>/data/modules</c>. <c>Store</c>'s nodes need <c>Skill</c>, which
/// only the <c>MeshWeaver.AI</c> module (delivered by <c>AI</c>) registers; <c>AI</c>'s root is typed
/// <c>Store/Plugin</c>, which only <c>Store</c> declares. A module landed only after its package's
/// content validated, so nothing could install first and no module ever reached the volume.</para>
///
/// <para>The registry here is in-process and serves the REAL wire shapes (the listing, the package
/// files, the bundle index and a module bundle carrying real assembly bytes) to the REAL
/// <see cref="RegistryPackageSource"/> and <see cref="PluginBundleClient"/>, so the install lane under
/// test is the production one end to end: <see cref="InstanceAutoRegistrationService"/>'s default
/// install, its selection, the install funnel and the module landing.</para>
/// </summary>
public abstract class StoreBootstrapTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The registry's URL — never resolved; the fake handler answers every request.</summary>
    protected const string RegistryUrl = "http://registry.bootstrap.test";

    /// <summary>The registry's source name, as the deployment's patterns name it.</summary>
    protected const string SourceName = "Plugins";

    /// <summary>The module the <c>AI</c> stand-in delivers (the one that registers <c>Skill</c>).</summary>
    protected const string AiModule = "Bootstrap.Test.AI";

    /// <summary>The module the <c>EntityViews</c> stand-in delivers.</summary>
    protected const string ViewsModule = "Bootstrap.Test.EntityViews";

    /// <summary>The module the <c>Chat</c> stand-in delivers.</summary>
    protected const string ChatModule = "Bootstrap.Test.Chat";

    /// <summary>Per-test landing root — the deployment's <c>modules/</c> tree, never the test host's bin.</summary>
    protected readonly string LandingRoot =
        Path.Combine(Path.GetTempPath(), "mw-bootstrap-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The store as the registry serves it: <c>Store</c> (declares <c>Store/Plugin</c>, carries a
    /// <c>Skill</c> node), <c>AI</c> (module, rooted on <c>Store/Plugin</c>), <c>EntityViews</c> and
    /// <c>Chat</c> (modules, rooted on <c>Store/Plugin</c>, Chat requiring AI), and <c>Games</c> — a
    /// <c>preInstalled</c> package nobody names.
    /// </summary>
    protected static readonly ImmutableArray<RepoFile> Repo =
    [
        new("Store/index.json",
            """{"$type":"MeshNode","id":"Store","namespace":"","path":"Store","mainNode":"Store","name":"Store","nodeType":"Space","state":"Active"}"""),
        new("Store/Plugin.json",
            """{"$type":"MeshNode","id":"Plugin","namespace":"Store","path":"Store/Plugin","mainNode":"Store/Plugin","name":"Plugin","nodeType":"NodeType","state":"Active"}"""),
        new("Store/Guide.json",
            """{"$type":"MeshNode","id":"Guide","namespace":"Store","path":"Store/Guide","mainNode":"Store/Guide","name":"Guide","nodeType":"Skill","state":"Active"}"""),
        Root("AI", AiModule, """["Store@^1.0.0"]""", preInstalled: true),
        Page("AI"),
        Root("EntityViews", ViewsModule, """["Store@^1.0.0"]""", preInstalled: true),
        Page("EntityViews"),
        Root("Chat", ChatModule, """["Store@^1.0.0","AI@^1.0.0"]""", preInstalled: true),
        Page("Chat"),
        new("Games/index.json",
            """{"$type":"MeshNode","id":"Games","namespace":"","path":"Games","mainNode":"Games","name":"Games","nodeType":"Space","state":"Active"}"""),
        Page("Games"),
    ];

    private static RepoFile Root(string id, string module, string requires, bool preInstalled) =>
        new($"{id}/index.json",
            "{\"$type\":\"MeshNode\",\"id\":\"" + id + "\",\"namespace\":\"\",\"path\":\"" + id
            + "\",\"mainNode\":\"" + id + "\",\"name\":\"" + id
            + "\",\"nodeType\":\"Store/Plugin\",\"state\":\"Active\",\"content\":{\"$type\":\"PluginContent\",\"module\":\""
            + module + "\",\"requires\":" + requires + ",\"preInstalled\":" + (preInstalled ? "true" : "false") + "}}");

    private static RepoFile Page(string id) =>
        new($"{id}/Guide.json",
            $$"""{"$type":"MeshNode","id":"Guide","namespace":"{{id}}","path":"{{id}}/Guide","mainNode":"{{id}}/Guide","name":"Guide","nodeType":"Markdown","state":"Active","content":"# {{id}}"}""");

    /// <summary>Every content-file fetch and module-bundle download the registry served, in order
    /// (<c>files:{package}</c> / <c>bundle:{package}</c>).</summary>
    protected ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>
    /// The catalog options — the control instance's shape by default: an EXPLICIT set, and nothing
    /// said about the preInstalled baseline. One instance, read by every pass, so a fixture can
    /// change the declared set between passes.
    /// </summary>
    protected PluginCatalogOptions Options { get; } = new()
    {
        Registries = [new PluginRegistryReference { Name = SourceName, Url = RegistryUrl, Token = "mwi_bootstrap" }],
        InstallByDefault = [$"{SourceName}/EntityViews", $"{SourceName}/Chat"],
    };

    /// <summary>What the boot pass is configured to install — the control instance's explicit set.</summary>
    protected virtual IReadOnlyList<string> BootSet => [$"{SourceName}/EntityViews", $"{SourceName}/Chat"];

    /// <summary>What every derived fixture adds to the mesh — the stand-in for "the module loaded".</summary>
    protected virtual MeshBuilder Arrange(MeshBuilder builder) => builder;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(LandingRoot);
        Options.InstallByDefault = [.. BootSet];
        var registry = new BootstrapRegistry(Requests);
        return Arrange(base.ConfigureMesh(builder))
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new ModuleLandingService(baseDirectory: LandingRoot))
                .AddSingleton<IHttpClientFactory>(new HandlerClientFactory(registry))
                .AddSingleton(Options));
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(LandingRoot))
                Directory.Delete(LandingRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp tree.
        }
    }

    /// <summary>The boot's first default-install pass.</summary>
    protected Task<DefaultInstallSummary> FirstPass(CancellationToken cancellationToken) =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>().Completed
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(cancellationToken);

    /// <summary>
    /// The registry, serving the four routes the consumer reads: <c>GET /api/plugins</c>,
    /// <c>POST /api/plugins/files</c>, <c>GET /api/plugins/bundles/index.json</c> and
    /// <c>GET /api/plugins/bundles/{plugin}/{version}</c> — the listing stamped with the source name
    /// the registry stamps, the files exactly as a node-repo source serves them (repo-relative paths).
    /// </summary>
    private sealed class BootstrapRegistry(ConcurrentQueue<string> requests) : HttpMessageHandler
    {
        private static readonly ImmutableDictionary<string, string> Modules = ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            [
                KeyValuePair.Create("AI", AiModule),
                KeyValuePair.Create("EntityViews", ViewsModule),
                KeyValuePair.Create("Chat", ChatModule),
            ]);

        private static PackageManifest Package(string id, bool preInstalled, params string[] requires) => new()
        {
            Id = id,
            Name = id,
            Kind = PackageKind.NodeRepo,
            TargetPartition = id,
            SourceFolder = id,
            Version = "commit-bootstrap",
            Source = SourceName,
            PreInstalled = preInstalled,
            Requires = [.. requires],
            Module = Modules.TryGetValue(id, out var module) ? module : null,
        };

        private static readonly ImmutableArray<PackageManifest> Listing =
        [
            Package("Store", preInstalled: false),
            Package("AI", preInstalled: true, "Store@^1.0.0"),
            Package("EntityViews", preInstalled: true, "Store@^1.0.0"),
            Package("Chat", preInstalled: true, "Store@^1.0.0", "AI@^1.0.0"),
            Package("Games", preInstalled: true),
        ];

        // HttpMessageHandler's contract is Task-shaped; every answer here is computed synchronously.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == RegistryPackageSource.RoutePrefix)
                return Task.FromResult(Json(PluginRegistryPayloads.List(Listing)));
            if (request.Method == HttpMethod.Post && path == RegistryPackageSource.RoutePrefix + "/files")
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStream(cancellationToken));
                var id = body.RootElement.GetProperty("id").GetString();
                requests.Enqueue($"files:{id}");
                var prefix = id + "/";
                return Task.FromResult(Json(PluginRegistryPayloads.Files(Repo
                    .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(f => new PackageFile(f.Path, f.Content))
                    .ToList())));
            }
            if (request.Method == HttpMethod.Get && path == PluginBundleClient.RoutePrefix + "/index.json")
                return Task.FromResult(Json(JsonSerializer.Serialize(
                    new PluginBundleClient.BundleIndex(null, Modules
                        .Select(m => new PluginBundleClient.BundleRef(
                            m.Key, "1.0.0", $"{RegistryUrl}{PluginBundleClient.RoutePrefix}/{m.Key}/1.0.0", m.Value))
                        .ToList()),
                    PluginRegistryPayloads.Json)));
            if (request.Method == HttpMethod.Get && path.StartsWith(PluginBundleClient.RoutePrefix + "/", StringComparison.Ordinal))
            {
                var plugin = path[(PluginBundleClient.RoutePrefix.Length + 1)..].Split('/')[0];
                requests.Enqueue($"bundle:{plugin}");
                if (Modules.TryGetValue(plugin, out var module))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(ModuleBundle(plugin, module)),
                    });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"the bootstrap registry serves no {request.Method} {path}"),
            });
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>A packed module bundle carrying REAL assembly bytes — the landing link-probes them,
    /// so a byte stand-in would be refused as unreadable.</summary>
    private static byte[] ModuleBundle(string plugin, string module)
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin,
            version = "1.0.0",
            module = new { assemblyName = module, assemblies = new[] { module + ".dll" } },
        });
        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(
            buffer,
            new PackagingManifest(plugin, "MeshWeaver.Plugin." + plugin, "1.0.0", plugin, null, []),
            "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.ModuleEntryPathFor(module + ".dll"),
                    () => new MemoryStream(File.ReadAllBytes(typeof(BundleReader).Assembly.Location))),
            ],
            manifestJson);
        return buffer.ToArray();
    }

    private sealed class HandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>
/// 🚨 The FIRST boot of an image that carries none of the store's modules: the module of every
/// selected package lands BEFORE any package's nodes are validated — so the cycle has an entry point —
/// and the selection is exactly the declared set and its <c>requires</c>.
/// </summary>
public class StoreBootstrapLandsModulesFirstTest(ITestOutputHelper output) : StoreBootstrapTestBase(output)
{
    [Fact(Timeout = 180_000)]
    public async Task AFreshImage_LandsEveryModuleFirst_AndSelectsOnlyTheDeclaredSet()
    {
        var pass = await FirstPass(TestContext.Current.CancellationToken);
        Output.WriteLine($"pass 1: {pass}");

        // The selection: the two named packages and what they require — never the preInstalled
        // package nobody named (it used to add every preInstalled package of the registry: 48).
        pass.Packages.Order(StringComparer.Ordinal).Should().Equal(new[] { "Store", "AI", "EntityViews", "Chat" }.Order(StringComparer.Ordinal));
        pass.Packages.Should().NotContain("Games");

        // The cycle, as production saw it: Store's nodes need `Skill`, which no module in this
        // process registers yet. The validation is NOT weakened — the package fails, named.
        pass.Failures.Should().Contain("Store");
        pass.FailureCauses.Single(c => c.Package == "Store").Cause.Should().Contain("Skill");

        // …and yet every module of the selection reached the volume, ahead of the content: the
        // next process start registers their NodeTypes. Before the fix the sidecar stayed EMPTY —
        // a module landed only after its own content validated, and none ever did.
        var activation = ModuleActivationSidecar.Read(LandingRoot);
        activation.Entries.Select(e => e.Name).Order(StringComparer.Ordinal).Should().Equal(new[] { AiModule, ViewsModule, ChatModule }.Order(StringComparer.Ordinal));
        activation.Entries.Should().OnlyContain(e => e.Enabled);
        activation.PendingRestart.Should().BeTrue("restart-as-activation — the self-updater takes the marker");
        // 🚨 The ORDER is the fix, not just the end state: every module of the selection was
        // downloaded before the FIRST package content was fetched. Module-after-its-own-content would
        // land the same end state on a later pass and never break the cycle.
        var log = Requests.ToList();
        Output.WriteLine($"registry requests: {string.Join(" → ", log)}");
        var firstFiles = log.FindIndex(r => r.StartsWith("files:", StringComparison.Ordinal));
        firstFiles.Should().BeGreaterThan(-1, "the content phase ran");
        foreach (var plugin in new[] { "AI", "EntityViews", "Chat" })
            log.FindIndex(r => r == $"bundle:{plugin}").Should().BeInRange(0, firstFiles - 1,
                $"{plugin}'s module must be downloaded before any package content is fetched");
        pass.ModulesLanded.Order(StringComparer.Ordinal).Should().Equal(new[] { AiModule, ViewsModule, ChatModule }.Order(StringComparer.Ordinal));
        pass.ToString().Should().Contain("MODULES LANDED ahead of content");
    }
}

/// <summary>
/// 🚨 The module phase proposes its set BEFORE phase 2 writes any install record, so the selection's
/// own declared floors must bind that proposal: a fresh package requiring <c>AI@^2</c> must not let a
/// set loading AI 1.0.0 be written just because its record does not exist yet.
/// </summary>
public class ModulePhaseProposalHonoursTheSelectionsFloorsTest(ITestOutputHelper output) : StoreBootstrapTestBase(output)
{
    private static PackageManifest Fresh(string id, string? module, params string[] requires) => new()
    {
        Id = id,
        Source = SourceName,
        Kind = PackageKind.NodeRepo,
        Module = module,
        Requires = [.. requires],
    };

    [Fact(Timeout = 180_000)]
    public async Task ASelectedPackagesUnmetFloor_RefusesTheSet_AndAMetOneDoesNot()
    {
        await FirstPass(TestContext.Current.CancellationToken);
        var landing = Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>();

        var refused = await ModuleDependencyFloor
            .ProposeChecked(Mesh, landing, [Fresh("AI", AiModule), Fresh("Chat", ChatModule, "AI@^2.0.0")])
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .LastAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        refused.Should().NotBeNull("Chat requires AI@^2 and the landed AI is 1.0.0 — that set must not be written");

        var accepted = await ModuleDependencyFloor
            .ProposeChecked(Mesh, landing, [Fresh("AI", AiModule), Fresh("Chat", ChatModule, "AI@^1.0.0")])
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .LastOrDefaultAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        accepted.Should().BeNull("a met floor proposes (or finds nothing changed) without a fault");
    }
}

/// <summary>
/// 🚨 A candidate the node install HOLDS (its partition is kept current by another writer — a
/// <c>_GitSync</c> entry; MeshWeaver#4588) adopts nothing, so the module phase lands no module for it
/// either: the ownership decision is the same one for both phases.
/// </summary>
public class ModulePhaseLandsNothingForAHeldCandidateTest(ITestOutputHelper output) : StoreBootstrapTestBase(output)
{
    /// <inheritdoc />
    protected override IReadOnlyList<string> BootSet => [$"{SourceName}/Store"];

    /// <inheritdoc />
    protected override MeshBuilder Arrange(MeshBuilder builder) =>
        builder.AddGitHubSyncTypes().ConfigureServices(services => services.AddGitHubSyncServices());

    [Fact(Timeout = 300_000)]
    public async Task AHeldCarrier_LandsNoModule_AndIsReportedHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        // Booted with a set that carries no module (BootSet); EntityViews' partition is made
        // somebody else's before it is ever selected.
        await FirstPass(ct);
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode(GitHubSyncService.ConfigId, "EntityViews")
            {
                Name = "GitHub Sync",
                NodeType = GitHubSyncService.ConfigNodeType,
                State = MeshNodeState.Active,
                Content = new GitHubSyncConfig { RepositoryUrl = "https://github.com/acme/example", Branch = "main" },
            })
            .Should().Within(TestTimeouts.Convergence)
            .Emit("connecting the partition to a repository is the precondition of the hold", cancellationToken: ct);
        await Observable.Interval(TestTimeouts.Quick / 20).StartWith(0L)
            .SelectMany(_ => PartitionContentOwnership.Observe(Mesh, "EntityViews"))
            .Where(v => !v.InstallerOwnsTheContent)
            .FirstAsync().Timeout(TestTimeouts.CrossSilo).Await(ct);

        Options.InstallByDefault = [$"{SourceName}/Store", $"{SourceName}/EntityViews"];
        var pass = await Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>()
            .RunDefaultInstall().FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"pass: {pass}");

        pass.Held.Select(h => h.Package).Should().Contain("EntityViews");
        Requests.Should().NotContain("bundle:EntityViews", "a held candidate's module bytes are never fetched");
        ModuleActivationSidecar.Read(LandingRoot).Entries.Select(e => e.Name).Should().NotContain(ViewsModule);
    }
}

/// <summary>
/// The boot AFTER that restart: the landed <c>MeshWeaver.AI</c> module now registers <c>Skill</c>
/// (stood in for by a code-registered NodeType), so one pass installs the whole store — <c>Store</c>
/// first, everything rooted on <c>Store/Plugin</c> after it — with the order derived from the declared
/// <c>requires</c>, not maintained by hand.
/// </summary>
public class StoreBootstrapCompletesAfterTheRestartTest(ITestOutputHelper output) : StoreBootstrapTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder Arrange(MeshBuilder builder) =>
        builder.AddMeshNodes(new MeshNode("Skill")
        {
            Name = "Skill",
            NodeType = MeshNode.NodeTypePath,
            IsSatelliteType = false,
        });

    [Fact(Timeout = 180_000)]
    public async Task OnceTheModulesLoad_OnePassInstallsTheStoreThenEverythingRootedOnIt()
    {
        var pass = await FirstPass(TestContext.Current.CancellationToken);
        Output.WriteLine($"pass: {pass}");

        pass.Failures.Should().BeEmpty();
        pass.Delivered.Order(StringComparer.Ordinal).Should().Equal(new[] { "Store", "AI", "EntityViews", "Chat" }.Order(StringComparer.Ordinal));
        var order = pass.Packages.ToList();
        order.IndexOf("Store").Should().BeLessThan(order.IndexOf("AI"));
        order.IndexOf("Store").Should().BeLessThan(order.IndexOf("EntityViews"));
        order.IndexOf("AI").Should().BeLessThan(order.IndexOf("Chat"));
    }
}

/// <summary>The selection rule, pure.</summary>
public class PreInstalledBaselineSelectionTest
{
    private static IReadOnlyList<string> Parse(params string[] patterns) => patterns;

    [Fact]
    public void AnExplicitSet_DropsTheBaseline_UnlessConfiguredOtherwise()
    {
        var named = Parse("Plugins/EntityViews", "Plugins/Chat");
        InstanceAutoRegistrationService.DeclaresExplicitSet(named).Should().BeTrue();
        InstanceAutoRegistrationService.BaselineApplies(null, named).Should().BeFalse(
            "an instance that names its packages gets those and their requires");
        InstanceAutoRegistrationService.BaselineApplies(true, named).Should().BeTrue(
            "an explicit true keeps the baseline beside the set");
        InstanceAutoRegistrationService.BaselineApplies(false, named).Should().BeFalse();
    }

    [Fact]
    public void NoPatterns_OrAWholeSource_KeepsTheBaseline()
    {
        InstanceAutoRegistrationService.BaselineApplies(null, Parse()).Should().BeTrue();
        InstanceAutoRegistrationService.BaselineApplies(null, Parse("Plugins/*")).Should().BeTrue(
            "the chart default names a whole source, never a set");
        InstanceAutoRegistrationService.BaselineApplies(null, Parse("Plugins/Essentials", "Education/*"))
            .Should().BeTrue("one whole-source pattern means the deployment did not name its set");
        InstanceAutoRegistrationService.BaselineApplies(false, Parse()).Should().BeFalse();
    }

    [Fact]
    public void AMalformedEntry_IsNotAnExplicitSet_SoTheBaselineStays()
    {
        var mixed = Parse("Plugins/Store", "Education/");
        InstanceAutoRegistrationService.DeclaresExplicitSet(mixed).Should().BeFalse(
            "one entry that is not a Source/Package means the operator did not state a set");
        InstanceAutoRegistrationService.BaselineApplies(null, mixed).Should().BeTrue();
        InstanceAutoRegistrationService.BaselineApplies(null, Parse("Plugins/Store", "  "))
            .Should().BeFalse("a blank entry is not an entry");
    }
}
