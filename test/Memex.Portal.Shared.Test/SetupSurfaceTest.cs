using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Memex.Portal.Shared.Setup;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The first-run wizard over a REAL HTTP pipeline — the production reader of
/// <c>MeshBuilder.IsAwaitingSetup</c>, whose own doc comment has always demanded that <i>"a host
/// that reads this true must serve the SETUP surface and nothing else"</i>.
///
/// <para>These drive the surface the way an operator does: load it, submit it, and check what
/// landed on disk. The properties that matter are the ones no unit test can see — that an
/// unconfigured instance does not serve its ordinary routes, that the token actually gates the
/// form, and that a configured instance is left completely alone.</para>
/// </summary>
public class SetupSurfaceTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-surface-" + Guid.NewGuid().ToString("N"));

    public SetupSurfaceTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private static readonly SetupCatalog Catalog = new(
        Storage: [new SetupStorageOption("Sqlite", "SQLite")],
        SignIn:
        [
            new SetupSignInOption("Dev", "Developer login", "Authentication", IsSwitch: true),
            new SetupSignInOption("Microsoft", "Microsoft", "Authentication:Microsoft", HasTenant: true),
        ],
        Ai: [new SetupAiOption("Anthropic", "Anthropic", "Anthropic")],
        Modules: [new SetupModuleOption("MeshWeaver.Hosting.Grpc.dll", "gRPC", PreSelected: true)]);

    /// <summary>
    /// A host in the state this surface exists for: awaiting setup, with an ordinary route mapped
    /// so that "and nothing else" is proved against something that WOULD answer.
    /// </summary>
    private WebApplication BuildApp(bool awaitingSetup = true, SetupCatalog? catalog = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ModuleRoot.ConfigKey] = root,
        });
        builder.Services.AddSingleton(new InstanceSetupStatusAccessor(() => awaitingSetup));
        builder.Services.AddSingleton<SetupAccessToken>();
        builder.Services.AddSingleton(new StorageBackendCatalog(["Sqlite"]));
        builder.Services.AddSingleton<ISetupCatalogProvider>(new StubCatalog(catalog ?? Catalog));

        var app = builder.Build();
        app.MapInstanceSetup();
        // The negative control: without it, "everything redirects" would be indistinguishable from
        // "nothing is mapped".
        app.MapGet("/ordinary", () => "the portal");
        app.MapGet("/healthz", () => "ok");
        app.Start();
        return app;
    }

    private static Dictionary<string, string> Answers(string token) => new()
    {
        ["token"] = token,
        ["storage.type"] = "Sqlite",
        ["signin.dev"] = "on",
        ["signin.devAdmins"] = "roland",
        ["embedding.endpoint"] = "http://localhost:11434/v1",
        ["embedding.model"] = "bge-m3",
        ["modules"] = "MeshWeaver.Hosting.Grpc.dll",
        ["packages"] = "Plugins/*",
    };

    [Fact]
    public async Task AnInstanceAwaitingSetup_ServesTheWizardAndNothingElse()
    {
        using var app = BuildApp();
        var client = app.GetTestClient();

        var wizard = await client.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.OK, wizard.StatusCode);
        var html = await wizard.Content.ReadAsStringAsync();
        Assert.Contains("Set up this instance", html);
        // The offered choices are the ones the catalog contributed — not a hard-coded menu.
        Assert.Contains("value=\"Sqlite\"", html);
        Assert.Contains("Microsoft", html);

        // …and the ordinary route, which answers on a configured host, does not.
        var ordinary = await client.GetAsync("/ordinary");
        Assert.Equal(HttpStatusCode.Redirect, ordinary.StatusCode);
        Assert.Equal("/setup", ordinary.Headers.Location?.ToString());
    }

    [Fact]
    public async Task TheLivenessProbe_KeepsAnswering_SoTheInstanceIsNotRestartedMidSetup()
    {
        // 🚨 An instance awaiting setup is not FAILING — it is waiting for a person. A probe that
        // reported it unhealthy would restart it in a loop, minting a new setup token every time
        // and making the wizard unreachable in practice.
        using var app = BuildApp();

        var probe = await app.GetTestClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
    }

    [Fact]
    public async Task AConfiguredInstance_IsLeftEntirelyAlone()
    {
        // Every deployment that exists today is configured through appsettings. If this surface
        // touched one of them at all, it would be a regression for all of them at once.
        using var app = BuildApp(awaitingSetup: false);
        var client = app.GetTestClient();

        Assert.Equal("the portal", await client.GetStringAsync("/ordinary"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/setup")).StatusCode);
    }

    [Fact]
    public async Task WithoutTheToken_TheFormIsRefused_AndNoManifestIsWritten()
    {
        // The surface is unauthenticated by construction — there is no user store yet — and what it
        // collects is a connection string, provider API keys, and the list of ids that become
        // platform administrators. The token is the only thing standing in front of that.
        using var app = BuildApp();

        var response = await app.GetTestClient()
            .PostAsync("/setup", new FormUrlEncodedContent(Answers("not-the-token")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(InstanceManifest.Read(root));
    }

    [Fact]
    public async Task WithTheToken_TheAnswersLandOnDisk_AndSecretsAreEncrypted()
    {
        using var app = BuildApp();
        var token = app.Services.GetRequiredService<SetupAccessToken>().Value;

        var form = Answers(token);
        form["signin.Microsoft.clientId"] = "m-client";
        form["signin.Microsoft.clientSecret"] = "m-secret";
        form["ai.Anthropic.apiKey"] = "sk-ant-xyz";

        var response = await app.GetTestClient().PostAsync("/setup", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("restart", (await response.Content.ReadAsStringAsync()).ToLowerInvariant());

        var manifest = InstanceManifest.Read(root);
        Assert.NotNull(manifest);
        Assert.Equal(InstanceSetupState.Complete, manifest!.State);
        Assert.Equal("Sqlite", manifest.Storage!.Type);
        Assert.True(manifest.SignIn!.EnableDevLogin);
        Assert.Equal("roland", manifest.SignIn.DevAdminUsers);
        Assert.Equal("http://localhost:11434/v1", manifest.Ai!.Embeddings!.Endpoint);
        Assert.Equal(["MeshWeaver.Hosting.Grpc.dll"], manifest.BootModules);

        // 🚨 Nothing the operator typed as a secret is on disk in the clear. The manifest gets
        // copied onto new volumes, backed up, and pasted into issues when an instance will not boot.
        var raw = File.ReadAllText(InstanceManifest.PathFor(root));
        Assert.DoesNotContain("m-secret", raw);
        Assert.DoesNotContain("sk-ant-xyz", raw);
        Assert.Contains("enc:v1:", raw);

        // …and the master key is a SEPARATE file, so the manifest can travel without it.
        Assert.True(File.Exists(InstanceMasterKey.PathFor(root)));
        Assert.DoesNotContain(
            InstanceMasterKey.Resolve(root, null)!, raw);
    }

    [Fact]
    public async Task TheWrittenManifest_IsWhatTheNextBootReadsAsConfiguration()
    {
        // The join that makes the wizard mean anything: what it writes, the next process reads —
        // through the ordinary configuration pipeline, with no wizard involved.
        using var app = BuildApp();
        var token = app.Services.GetRequiredService<SetupAccessToken>().Value;
        var form = Answers(token);
        form["ai.Anthropic.apiKey"] = "sk-ant-xyz";
        await app.GetTestClient().PostAsync("/setup", new FormUrlEncodedContent(form));

        var next = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInstanceManifest(root)
            .Build();

        Assert.Equal("Sqlite", next["Graph:Storage:Type"]);
        Assert.Equal("true", next["Authentication:EnableDevLogin"]);
        Assert.Equal("sk-ant-xyz", next["Anthropic:ApiKey"]);
        Assert.Equal("http://localhost:11434/v1", next["Embedding:Endpoint"]);
    }

    [Fact]
    public async Task AnAnswerTheImageCannotHonour_ComesBackAsAFormError_NotAWrittenManifest()
    {
        using var app = BuildApp();
        var token = app.Services.GetRequiredService<SetupAccessToken>().Value;
        var form = Answers(token);
        form["storage.type"] = "Cosmos";

        var response = await app.GetTestClient().PostAsync("/setup", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Cosmos", await response.Content.ReadAsStringAsync());
        Assert.Null(InstanceManifest.Read(root));
    }

    [Fact]
    public async Task TheSubmittedSecret_IsNeverEchoedBackIntoTheForm()
    {
        // 🚨 Re-filling a password field after a failed submit is convenient and is also how a
        // credential ends up in a browser cache, a screenshot and a page source.
        using var app = BuildApp();
        var token = app.Services.GetRequiredService<SetupAccessToken>().Value;
        var form = Answers(token);
        form["storage.type"] = "Cosmos";                       // force a re-render
        form["signin.Microsoft.clientId"] = "m-client";
        form["signin.Microsoft.clientSecret"] = "super-secret";

        var response = await app.GetTestClient().PostAsync("/setup", new FormUrlEncodedContent(form));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("m-client", html);                     // the public half comes back
        Assert.DoesNotContain("super-secret", html);           // the secret half does not
    }

    [Fact]
    public async Task TheConnectionString_IsNeverEchoedBackIntoTheForm()
    {
        // 🚨 A connection string CARRIES A PASSWORD, so it gets exactly the treatment the sign-in
        // secrets already had. It did not: the field was re-rendered with value="…" after a failed
        // submit, putting the password in the page source, the browser cache and any screenshot of
        // the form (Copilot review on #3220 — an inconsistency inside one file).
        using var app = BuildApp();
        var token = app.Services.GetRequiredService<SetupAccessToken>().Value;
        var form = Answers(token);
        form["storage.type"] = "Cosmos";                       // force a re-render
        form["storage.connectionString"] = "Host=db;Password=hunter2";

        var response = await app.GetTestClient().PostAsync("/setup", new FormUrlEncodedContent(form));
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("hunter2", html);
        Assert.DoesNotContain("Host=db", html);
    }

    [Fact]
    public async Task ARouteTheDeploymentAlreadyAnswers_IsReported_NotOfferedForEditing()
    {
        // 🚨 The manifest is layered UNDER the host's own configuration, so anything typed for an
        // already-configured route would be accepted, stored, and then silently outranked at the
        // next boot. SetupSignInOption.AlreadyConfigured documented exactly this behaviour and the
        // markup did not implement it (Copilot review on #3220). Disabled, not readonly: a disabled
        // control is not submitted at all.
        using var app = BuildApp(catalog: Catalog with
        {
            SignIn =
            [
                new SetupSignInOption("Dev", "Developer login", "Authentication", IsSwitch: true),
                new SetupSignInOption("Microsoft", "Microsoft", "Authentication:Microsoft",
                    HasTenant: true, AlreadyConfigured: true),
            ],
        });
        var token = app.Services.GetRequiredService<SetupAccessToken>().Value;
        var client = app.GetTestClient();

        var html = await client.GetStringAsync("/setup");
        Assert.Contains("already configured by this deployment", html);
        // The client-id field for that route carries `disabled`, so nothing is posted for it.
        Assert.Matches(@"name=""signin\.Microsoft\.clientId""[^>]*\bdisabled\b", html);

        // …and the endpoint does not depend on the markup: a hand-crafted POST is ignored too,
        // because a manifest claiming a provider the instance does not run on is worse than silence.
        var form = Answers(token);
        form["signin.Microsoft.clientId"] = "smuggled-in";
        form["signin.Microsoft.clientSecret"] = "smuggled-secret";
        await client.PostAsync("/setup", new FormUrlEncodedContent(form));

        var manifest = InstanceManifest.Read(root);
        Assert.NotNull(manifest);
        Assert.Empty(manifest!.SignIn!.Providers);
    }

    [Fact]
    public async Task TheGermanHeader_GetsTheGermanPage()
    {
        // The wizard runs pre-mesh, so there is no AccessContext to read a locale from — but that
        // is a reason to read Accept-Language explicitly, not a licence to hard-code English.
        using var app = BuildApp();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Language", "de-CH,de;q=0.9,en;q=0.8");

        var html = await client.GetStringAsync("/setup");

        Assert.Contains("Diese Instanz einrichten", html);
        Assert.Contains("lang=\"de\"", html);
        Assert.DoesNotContain("Set up this instance", html);
    }

    [Fact]
    public async Task TheDeveloperLogin_IsOnByDefault()
    {
        // 🚨 The property LocalOverlayDefaultsToDevLoginTest used to pin on the Homebrew overlay,
        // followed to where the default now lives. Signing in to your own laptop must not require
        // an Entra tenant, an app registration, two redirect URIs and a real client secret on disk;
        // DevAuthController self-provisions the user on first sign-in and needs none of it. The
        // overlay stopped stating this so the operator's answer is not overridden — which makes the
        // WIZARD's pre-selection the thing that has to stay right.
        using var app = BuildApp();

        var html = await app.GetTestClient().GetStringAsync("/setup");

        Assert.Matches(@"name=""signin\.dev""[^>]*\bchecked\b", html);
    }

    [Fact]
    public async Task AnUntouchedProviderRow_ConfiguresNothing()
    {
        // 🚨 Found by driving the real form in a browser: the "local / OpenAI-compatible" provider
        // rendered its DefaultEndpoint as the field's VALUE, so the row submitted itself whether or
        // not anyone looked at it — and a provider nobody chose arrived configured. The default is
        // a placeholder now, and this is what holds it there. The composition's
        // "no key and no endpoint means not chosen" rule is only true if an untouched field
        // submits nothing.
        using var app = BuildAppWithEndpointProvider();
        var token = app.Services.GetRequiredService<SetupAccessToken>().Value;
        var client = app.GetTestClient();

        var html = await client.GetStringAsync("/setup");
        Assert.Contains("placeholder=\"http://localhost:11434/v1\"", html);
        Assert.DoesNotContain("value=\"http://localhost:11434/v1\"", html);

        await client.PostAsync("/setup", new FormUrlEncodedContent(Answers(token)));

        var manifest = InstanceManifest.Read(root);
        Assert.NotNull(manifest);
        Assert.Empty(manifest!.Ai!.Providers);
    }

    /// <summary>A catalog whose model provider takes an endpoint and needs no key — the shape that
    /// exposed the pre-filled-value defect.</summary>
    private WebApplication BuildAppWithEndpointProvider()
    {
        var app = BuildApp(catalog: Catalog with
        {
            Ai =
            [
                new SetupAiOption("OpenAICompatible", "Local", "OpenAICompatible",
                    RequiresApiKey: false, DefaultEndpoint: "http://localhost:11434/v1",
                    TakesEndpoint: true),
            ],
        });
        return app;
    }

    private sealed class StubCatalog(SetupCatalog catalog) : ISetupCatalogProvider
    {
        public SetupCatalog Describe() => catalog;
    }
}
