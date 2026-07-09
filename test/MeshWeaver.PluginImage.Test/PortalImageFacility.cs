using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;
using Xunit;

namespace MeshWeaver.PluginImage.Test;

/// <summary>
/// Container facility that INJECTS a MeshWeaver portal image (env <c>MW_TEST_IMAGE</c>) and proves,
/// as a black box, that the image is deployable and ships the mesh + plugin-registry surface — a
/// smoke gate the plugins repo (or a release check) can run against the newest image with nothing
/// but Docker. It runs the migration image (<c>MW_MIGRATION_IMAGE</c>, defaulted from the portal
/// tag) against a throwaway Postgres to create the schema exactly as the <c>memex-migration</c>
/// deployment does in prod, then boots the portal on that database.
///
/// <para><b>Skips cleanly</b> when <c>MW_TEST_IMAGE</c> is unset or Docker/registry is unreachable,
/// so a normal <c>dotnet test</c> run is unaffected. To run it:</para>
/// <code>
///   az acr login -n meshweaver
///   MW_TEST_IMAGE=meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.749 \
///     dotnet test test/MeshWeaver.PluginImage.Test
/// </code>
///
/// <para><b>Next layer — plugin compile-in-image.</b> Importing each plugin node repo and asserting
/// <c>CompilationStatus.Ok</c> inside the injected image needs a HEADLESS import path in the image
/// (DevLogin is forced off in the prod image and the REST surface requires an <c>mw_</c> token, so a
/// fresh container can't be driven by the API alone). The clean unlock is a GitSync boot-import
/// config: the container gets a plugin-partition list + GitHub App creds, imports at boot, and this
/// facility then asserts <c>Successfully compiled {plugin}</c> from the container logs. That mesh-
/// side mechanism is tracked separately; this facility is the reusable image harness it plugs into.</para>
/// </summary>
public sealed class PortalImageFacility : IAsyncLifetime
{
    // 32 bytes, base64 — the envelope key for stored provider credentials. Any fixed value boots.
    private const string TestMasterKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const ushort PortalHttpPort = 8080;

    private static string? Image => Environment.GetEnvironmentVariable("MW_TEST_IMAGE");

    private INetwork? _network;
    private PostgreSqlContainer? _pg;
    private IContainer? _portal;
    private HttpClient? _http;
    private string? _skip;

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Image))
        {
            _skip = "MW_TEST_IMAGE not set — inject a portal image tag to run this facility " +
                    "(e.g. MW_TEST_IMAGE=meshweaver.azurecr.io/memex-portal-ai:<tag>).";
            return;
        }

        // Docker-availability probe: bring up the network + Postgres. A failure HERE is an infra
        // issue (no Docker socket / can't pull the base image) → skip so the normal suite is fine.
        // A failure LATER (migration / portal) means the INJECTED image is broken or under-configured
        // — that must FAIL (with the container logs), not silently skip, or the facility is useless.
        try
        {
            _network = new NetworkBuilder().Build();
            _pg = new PostgreSqlBuilder("pgvector/pgvector:pg17")
                .WithNetwork(_network)
                .WithNetworkAliases("pg")
                .WithDatabase("memex")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _pg.StartAsync();
        }
        catch (Exception ex)
        {
            _skip = $"container infrastructure unavailable ({ex.GetType().Name}): {ex.Message}";
            await SafeDisposeAsync();
            return;
        }

        try
        {
            const string conn = "Host=pg;Port=5432;Database=memex;Username=postgres;Password=postgres";

            // Migration: create schema + matview, then exit 0 — the portal expects it present.
            // It's a ONE-SHOT job: Testcontainers' StartAsync wait treats a container that exits
            // during startup as an error (ContainerNotRunningException), so we start it, tolerate the
            // exit, and verify success via the exit code + completion log.
            var migration = new ContainerBuilder(MigrationImage(Image!))
                .WithNetwork(_network)
                .WithEnvironment("ConnectionStrings__memex", conn)
                .Build();
            try { await migration.StartAsync(); }
            catch { /* one-shot exits during the running-wait — verified via exit code below */ }
            var migCode = await migration.GetExitCodeAsync();
            var (migOut, migErr) = await migration.GetLogsAsync();
            await migration.DisposeAsync();
            if (migCode != 0)
                throw new InvalidOperationException(
                    $"migration image {MigrationImage(Image!)} exited {migCode}:\n{migErr}\n{migOut}");

            _portal = new ContainerBuilder(Image!)
                .WithNetwork(_network)
                .WithEnvironment("ConnectionStrings__memex", conn)
                .WithEnvironment("ASPNETCORE_Kestrel__Endpoints__Http__Url", $"http://0.0.0.0:{PortalHttpPort}")
                // Filesystem (Azure-free self-host) backend: DataProtection keys + caches go to a local
                // volume instead of Azure Blob. Without this the image takes the Azure path, fails to
                // resolve the keyed BlobServiceClient, and crashes at UseAntiforgery on boot.
                .WithEnvironment("Deployment__Backend", "Filesystem")
                .WithEnvironment("Deployment__DataRoot", "/tmp/mw-data")
                .WithEnvironment("Deployment__Orleans__Clustering", "Localhost")
                .WithEnvironment("Authentication__Provider", "Custom")
                .WithEnvironment("Authentication__EnableDevLogin", "false")
                .WithEnvironment("Ai__KeyProtection__MasterKey", TestMasterKey)
                .WithPortBinding(PortalHttpPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(PortalHttpPort).ForPath("/healthz")))
                .Build();

            // BOUND the boot wait — a portal that never reaches /healthz must fail in minutes, not
            // hang the run. On timeout, surface the container logs so the missing config / crash is
            // diagnosable (the injected prod image needs its full deployment config to boot).
            using var startCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                await _portal.StartAsync(startCts.Token);
            }
            catch (Exception ex)
            {
                var logs = "";
                try { var (o, e) = await _portal.GetLogsAsync(); logs = Tail(e + "\n" + o, 3000); } catch { }
                throw new InvalidOperationException(
                    $"Injected image '{Image}' did not become healthy on /healthz within the startup " +
                    $"budget. This usually means it needs more of its deployment config than the minimal " +
                    $"set here. Tail of container logs:\n{logs}", ex);
            }

            _http = new HttpClient
            {
                BaseAddress = new Uri($"http://{_portal.Hostname}:{_portal.GetMappedPublicPort(PortalHttpPort)}"),
                Timeout = TimeSpan.FromSeconds(30),
            };
        }
        catch
        {
            // The Docker probe already passed, so a failure here is a real problem with the INJECTED
            // image / its config — clean up and let it FAIL (never a silent skip that hides a broken image).
            await SafeDisposeAsync();
            throw;
        }
    }

    private static string Tail(string s, int chars) =>
        string.IsNullOrEmpty(s) || s.Length <= chars ? (s ?? "") : "…" + s[^chars..];

    /// <summary>The injected image is deployable: it boots to a healthy state on a real Postgres.</summary>
    [Fact]
    public async Task InjectedImage_BootsAndServesHealthy()
    {
        SkipIfUnavailable();
        var resp = await _http!.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>The image ships the mesh REST surface — the route resolves (401 auth-required, not 404).</summary>
    [Fact]
    public async Task InjectedImage_ShipsMeshApi()
    {
        SkipIfUnavailable();
        var resp = await _http!.PostAsync("/api/mesh/get",
            new StringContent("{\"path\":\"@x\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    /// <summary>The image ships the plugin REGISTRY (#382): /api/mesh/catalog resolves, not a 404.</summary>
    [Fact]
    public async Task InjectedImage_ShipsPluginRegistry()
    {
        SkipIfUnavailable();
        var resp = await _http!.PostAsync("/api/mesh/catalog",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// The image ships <c>MeshWeaver.BusinessRules.dll</c> (the <c>IScope&lt;,&gt;</c> runtime surface)
    /// AND lists it in the app's <c>.deps.json</c> — i.e. it is part of the runtime reference set the
    /// host turns into TRUSTED_PLATFORM_ASSEMBLIES (TPA), which IS the mesh compiler's scope-compile
    /// reference set. That's why the mesh-local <c>#r</c> feed (BakeMeshLocalFeed) could be removed: a
    /// live-compiled scope node resolves those types from TPA, not from a baked feed. This guards that
    /// safety net — if a future change drops the runtime lib from the portal's published deps, scope
    /// nodes would silently fail to compile in prod, and this fails first. File presence alone would
    /// not prove the assembly is a resolvable reference; deps.json membership does.
    /// </summary>
    [Fact]
    public async Task InjectedImage_ShipsBusinessRulesRuntime_ForFeedlessScopeCompile()
    {
        SkipIfUnavailable();

        // (a) the runtime DLL is physically present in the image.
        var file = await _portal!.ExecAsync(new[] { "test", "-f", "/app/MeshWeaver.BusinessRules.dll" });
        Assert.Equal(0L, file.ExitCode);

        // (b) it is listed in the published .deps.json — the runtime-assembly list the host resolves
        //     into TPA, and thus the mesh compiler's scope-compile reference set. This is the assertion
        //     that actually backs the "resolves from TPA, no #r feed needed" claim above.
        var deps = await _portal!.ExecAsync(new[]
            { "sh", "-c", "grep -q '\"MeshWeaver.BusinessRules.dll\"' /app/*.deps.json" });
        Assert.Equal(0L, deps.ExitCode);
    }

    public async ValueTask DisposeAsync() => await SafeDisposeAsync();

    private async ValueTask SafeDisposeAsync()
    {
        _http?.Dispose();
        if (_portal is not null) { try { await _portal.DisposeAsync(); } catch { } }
        if (_pg is not null) { try { await _pg.DisposeAsync(); } catch { } }
        if (_network is not null) { try { await _network.DisposeAsync(); } catch { } }
    }

    private void SkipIfUnavailable()
    {
        if (_skip is not null)
            Assert.Skip(_skip);
    }

    /// <summary>Derives the migration image from the portal image: same registry + tag, repo swap.</summary>
    private static string MigrationImage(string portalImage)
    {
        var overridden = Environment.GetEnvironmentVariable("MW_MIGRATION_IMAGE");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        // meshweaver.azurecr.io/memex-portal-ai:TAG -> meshweaver.azurecr.io/memex-migration:TAG
        var colon = portalImage.LastIndexOf(':');
        var tag = colon >= 0 ? portalImage[(colon + 1)..] : "latest";
        var repoPart = colon >= 0 ? portalImage[..colon] : portalImage;
        var slash = repoPart.LastIndexOf('/');
        var registry = slash >= 0 ? repoPart[..slash] : "";
        return $"{registry}{(registry.Length > 0 ? "/" : "")}memex-migration:{tag}";
    }
}
