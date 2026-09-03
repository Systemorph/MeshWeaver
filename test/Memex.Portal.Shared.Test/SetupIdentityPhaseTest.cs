using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Memex.Portal.Shared.Setup;
using Memex.Portal.Shared.Test.Fakes;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// PHASE ONE of the first-run wizard: name this instance, claim an id, register, and come back
/// entitled to a plugin catalog.
///
/// <para>The order is the point. Registration is what gives the instance a plan, the plan is what
/// lists the plugins, and the plugins are where the remaining answers — the database above all —
/// come from. Asking "which database?" first would be asking before there is anything to choose.</para>
///
/// <para>Every registry call here goes to <see cref="FakeRegistry"/>: a real registration claims an
/// id GLOBALLY and the platform never re-issues one, so a suite pointed at the live registry would
/// burn a permanent id per run on shared infrastructure.</para>
/// </summary>
public class SetupIdentityPhaseTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-identity-" + Guid.NewGuid().ToString("N"));

    private readonly FakeRegistry registry = new();

    public SetupIdentityPhaseTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private static readonly SetupCatalog Catalog = new(
        Storage: [new SetupStorageOption("Sqlite", "SQLite")],
        SignIn: [new SetupSignInOption("Dev", "Developer login", "Authentication", IsSwitch: true)],
        Ai: [],
        Modules: []);

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ModuleRoot.ConfigKey] = root,
            // The image ships Sqlite and PostgreSql; ExoticDb it cannot open.
            ["PluginCatalog:RegistryUrl"] = "https://registry.example",
        });
        builder.Services.AddSingleton(new InstanceSetupStatusAccessor(static () => true));
        builder.Services.AddSingleton<SetupAccessToken>();
        builder.Services.AddSingleton(new StorageBackendCatalog(["Sqlite", "PostgreSql"]));
        builder.Services.AddSingleton<ISetupCatalogProvider>(new StubCatalog(Catalog));
        builder.Services.AddHttpClient<SetupRegistryClient>()
            .ConfigurePrimaryHttpMessageHandler(() => registry);

        var app = builder.Build();
        app.MapInstanceSetup();
        app.Start();
        return app;
    }

    private static string TokenOf(WebApplication app) =>
        app.Services.GetRequiredService<SetupAccessToken>().Value;

    [Fact]
    public async Task AFreshInstance_IsAskedForItsNameFirst_NotItsDatabase()
    {
        using var app = BuildApp();

        var html = await app.GetTestClient().GetStringAsync("/setup");

        Assert.Contains("Welcome", html);
        Assert.Contains("Instance name", html);
        // 🚨 The database question must NOT be on this page. It cannot be answered yet — the options
        // come from what the registration entitles the instance to.
        Assert.DoesNotContain("name=\"storage.type\"", html);
    }

    [Fact]
    public async Task TheIdIsAGuid_MintedForTheOperator_AndNotEditable()
    {
        using var app = BuildApp();

        var html = await app.GetTestClient().GetStringAsync("/setup");

        var match = System.Text.RegularExpressions.Regex.Match(
            html, @"name=""identity\.id""[^>]*value=""(?<id>[^""]+)""");
        Assert.True(match.Success, "the page must show the id it is about to claim");
        var id = match.Groups["id"].Value;

        Assert.True(Guid.TryParse(id, out _), $"expected a guid, got '{id}'");
        Assert.True(InstanceIdRules.IsWellFormed(id), $"the minted id must be registrable: '{id}'");
        // Read-only, because it is claimed permanently: an operator must not be able to type one
        // they might type again on another install.
        Assert.Matches(@"name=""identity\.id""[^>]*\breadonly\b", html);
    }

    [Fact]
    public async Task Registering_ClaimsTheId_AndRecordsTheIssuedKeyEncrypted()
    {
        using var app = BuildApp();
        var client = app.GetTestClient();
        var html = await client.GetStringAsync("/setup");
        var id = System.Text.RegularExpressions.Regex
            .Match(html, @"name=""identity\.id""[^>]*value=""(?<id>[^""]+)""").Groups["id"].Value;

        var response = await client.PostAsync("/setup/identity", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = TokenOf(app),
                ["identity.name"] = "Roland laptop",
                ["identity.id"] = id,
                ["identity.registry"] = "https://registry.example",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var sent = Assert.Single(registry.Registrations);
        Assert.Equal(id, sent.InstanceId);
        Assert.Equal("Roland laptop", sent.DisplayName);
        // An OPEN registration — no key — is the ordinary case and what enrols the free plan.
        Assert.Equal("", sent.BootstrapKey);

        var manifest = InstanceManifest.Read(root);
        Assert.NotNull(manifest);
        Assert.Equal(id, manifest!.Identity!.Id);
        Assert.Equal("Roland laptop", manifest.Identity.Name);
        Assert.Equal("free", manifest.Identity.Plan);
        // 🚨 The registry issues the key ONCE and never again. It must be on disk before anything
        // else can fail — and encrypted, like every other secret the wizard collects.
        Assert.StartsWith("enc:v1:", manifest.Identity.InstanceKey);
        Assert.DoesNotContain("mwi_faketestkey", File.ReadAllText(InstanceManifest.PathFor(root)));
        // Still in setup: identity answers who, not how.
        Assert.Equal(InstanceSetupState.AwaitingModules, manifest.State);
    }

    [Fact]
    public async Task AfterRegistering_ThePluginListIsShown_AndPostgresIsOfferedAsADatabase()
    {
        using var app = BuildApp();
        var client = app.GetTestClient();
        await RegisterAsync(app, client);

        var html = await client.GetStringAsync("/setup");

        // The sections now appear, led by who this instance is.
        Assert.Contains("Roland laptop", html);
        Assert.Contains("Plan: free", html);
        // The plan's plugins.
        Assert.Contains("Store", html);
        Assert.Contains("PostgreSQL", html);
        // 🚨 And Postgres is offered as a DATABASE, because the image can open it — this is the
        // step the whole ordering exists for.
        Assert.Contains("value=\"PostgreSql\"", html);
        // The listing was made with the issued key, not anonymously.
        Assert.Contains("mwi_faketestkey", registry.ListTokens);
    }

    [Fact]
    public async Task AnInstanceNameIsEscapedWhereItIsRendered()
    {
        // The name is operator input echoed back on every page of phase two. It is the one field
        // here a person types freely, so it is the one that must not be able to inject markup.
        using var app = BuildApp();
        var client = app.GetTestClient();
        var page = await client.GetStringAsync("/setup");
        var id = System.Text.RegularExpressions.Regex
            .Match(page, @"name=""identity\.id""[^>]*value=""(?<id>[^""]+)""").Groups["id"].Value;
        await client.PostAsync("/setup/identity", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = TokenOf(app),
                ["identity.name"] = "<script>alert('x')</script>",
                ["identity.id"] = id,
                ["identity.registry"] = "https://registry.example",
            }));

        var html = await client.GetStringAsync("/setup");

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task AStoragePackageTheImageCannotOpen_IsListed_ButNotOfferedAsADatabase()
    {
        // 🚨 The one that would fail at the NEXT boot with the wizard gone. Landing a storage module
        // the image lacks would have to happen before persistence selection reads Graph:Storage, and
        // package provisioning runs after the mesh is up — so offering it would record a backend
        // that never resolves.
        using var app = BuildApp();
        var client = app.GetTestClient();
        await RegisterAsync(app, client);

        var html = await client.GetStringAsync("/setup");

        Assert.Contains("Exotic DB", html);                      // listed as a plugin
        Assert.DoesNotContain("value=\"ExoticDb\"", html);       // never as a database
    }

    [Fact]
    public async Task ChoosingTheDatabaseFromTheList_ProvisionsItsPackageToo()
    {
        using var app = BuildApp();
        var client = app.GetTestClient();
        await RegisterAsync(app, client);

        var response = await client.PostAsync("/setup", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = TokenOf(app),
                ["storage.type"] = "PostgreSql",
                ["storage.connectionString"] = "Host=localhost;Port=5432;Database=memex;Username=postgres;Password=pw",
                ["signin.dev"] = "on",
                ["signin.devAdmins"] = "roland",
                ["embedding.endpoint"] = "http://localhost:11434/v1",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var manifest = InstanceManifest.Read(root);
        Assert.Equal(InstanceSetupState.Complete, manifest!.State);
        Assert.Equal("PostgreSql", manifest.Storage!.Type);
        // An operator who picked a database from the plugin list has chosen that plugin; a manifest
        // naming the backend without its package would configure a store whose plugin never installs.
        Assert.Contains("Plugins/PostgreSql", manifest.ProvisionPackages);
        // …and the identity survives completion — losing it would strand the un-reissuable key.
        Assert.Equal("Roland laptop", manifest.Identity!.Name);
        Assert.StartsWith("enc:v1:", manifest.Identity.InstanceKey);
    }

    [Fact]
    public async Task ATakenId_IsRefusedWithItsOwnRemedy_AndWritesNoIdentity()
    {
        var app = BuildAppWith(new FakeRegistry { RegisterStatus = HttpStatusCode.Conflict });
        using var _ = app;
        var client = app.GetTestClient();

        var response = await client.PostAsync("/setup/identity", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = TokenOf(app),
                ["identity.name"] = "Taken",
                ["identity.id"] = Guid.NewGuid().ToString("d"),
                ["identity.registry"] = "https://registry.example",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("already claimed", html);
        // Nothing durable was written — the operator can try again with a fresh id.
        Assert.Null(InstanceManifest.Read(root)?.Identity);
    }

    [Fact]
    public async Task AnUnreachableRegistry_IsReported_NotSwallowedAsAnEmptyPlan()
    {
        // An instance that registered but cannot see its plan is a state worth naming. An empty
        // list would read as "your plan includes nothing", which is a different and wrong message.
        using var app = BuildApp();
        var client = app.GetTestClient();
        await RegisterAsync(app, client);

        // Now make the listing fail.
        var broken = BuildAppWith(new FakeRegistry { ListStatus = HttpStatusCode.Unauthorized }, keepRoot: true);
        using var _ = broken;
        var html = await broken.GetTestClient().GetStringAsync("/setup");

        Assert.Contains("refused the key", html);
        // …and the rest of the wizard still works: the image's own options are still offered.
        Assert.Contains("value=\"Sqlite\"", html);
    }

    [Fact]
    public async Task WithoutTheToken_NothingIsRegistered()
    {
        using var app = BuildApp();

        var response = await app.GetTestClient().PostAsync("/setup/identity", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = "not-the-token",
                ["identity.name"] = "Nope",
                ["identity.id"] = Guid.NewGuid().ToString("d"),
                ["identity.registry"] = "https://registry.example",
            }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(registry.Registrations);
        Assert.Null(InstanceManifest.Read(root)?.Identity);
    }

    [Fact]
    public async Task ANamelessInstance_IsRefused_BeforeAnyIdIsClaimed()
    {
        using var app = BuildApp();

        var response = await app.GetTestClient().PostAsync("/setup/identity", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = TokenOf(app),
                ["identity.name"] = "",
                ["identity.id"] = Guid.NewGuid().ToString("d"),
                ["identity.registry"] = "https://registry.example",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // 🚨 Refused BEFORE the registry call: an id claimed for a form that was going to be
        // rejected anyway is an id burnt for nothing.
        Assert.Empty(registry.Registrations);
    }

    private async Task RegisterAsync(WebApplication app, HttpClient client)
    {
        var html = await client.GetStringAsync("/setup");
        var id = System.Text.RegularExpressions.Regex
            .Match(html, @"name=""identity\.id""[^>]*value=""(?<id>[^""]+)""").Groups["id"].Value;
        await client.PostAsync("/setup/identity", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = TokenOf(app),
                ["identity.name"] = "Roland laptop",
                ["identity.id"] = id,
                ["identity.registry"] = "https://registry.example",
            }));
    }

    private WebApplication BuildAppWith(FakeRegistry fake, bool keepRoot = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ModuleRoot.ConfigKey] = root,
            ["PluginCatalog:RegistryUrl"] = "https://registry.example",
        });
        builder.Services.AddSingleton(new InstanceSetupStatusAccessor(static () => true));
        builder.Services.AddSingleton<SetupAccessToken>();
        builder.Services.AddSingleton(new StorageBackendCatalog(["Sqlite", "PostgreSql"]));
        builder.Services.AddSingleton<ISetupCatalogProvider>(new StubCatalog(Catalog));
        builder.Services.AddHttpClient<SetupRegistryClient>()
            .ConfigurePrimaryHttpMessageHandler(() => fake);
        var app = builder.Build();
        app.MapInstanceSetup();
        app.Start();
        return app;
    }

    private sealed class StubCatalog(SetupCatalog catalog) : ISetupCatalogProvider
    {
        public SetupCatalog Describe() => catalog;
    }
}
