using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>HTTP endpoints of a module go live too (policy <c>module-live-update-default</c>).</b> A module
/// held in its own load context maps its routes through <see cref="ModuleEndpointDataSource"/>, which
/// re-maps them from the module's CURRENT generation when it is swapped — so Mcp, Teams, Mail, WhatsApp
/// and Courses no longer need a restart for their endpoints. Real ASP.NET Core routing on a TestServer,
/// real emitted modules.
/// </summary>
public sealed class ModuleEndpointsSwapLiveTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.LiveEndpoints";
    private static readonly TimeSpan Budget = TestTimeouts.Convergence;
    private readonly string root = Path.Combine(Path.GetTempPath(), "live-endpoints-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task AModulesEndpoints_AreRemappedFromTheNewGeneration_OnALiveSwap()
    {
        var unloads = new CollectibleContextUnloads();
        using var contexts = new ModuleContexts().Attach(unloads, null);
        var first = contexts.Load(Write("g1", ModuleSource(1)));
        contexts.Commit(first);
        await using var app = await Start(contexts);
        var client = app.GetTestClient();

        (await client.GetStringAsync("/live-endpoint")).Should().Be("v1");

        var second = contexts.Load(Write("g2", ModuleSource(2)));
        var replaced = contexts.Commit(second);

        (await client.GetStringAsync("/live-endpoint")).Should().Be("v2",
            "the route must answer from the new generation, in the running process — no restart");

        (await contexts.Retire(replaced ?? throw new InvalidOperationException("committing g2 must hand back the replaced g1"), Budget).Timeout(Budget).Await()).Should().BeTrue();
        replaced = null;
        first = null;
        var drained = await CollectibleUnloadDrain.WaitUntilCollectedAsync(unloads);
        drained.Collected.Should().BeTrue($"no routing structure may keep the old generation alive ({drained})");
    }

    /// <summary>A held module that maps NO endpoints at boot and ADDS them in a later generation (#6128
    /// review): the dynamic source must exist anyway, or the new routes are silently absent while the
    /// swap reports Live.</summary>
    [Fact]
    public async Task AGenerationThatAddsEndpoints_IsMappedLive_EvenWhenBootMappedNone()
    {
        using var contexts = new ModuleContexts();
        contexts.Commit(contexts.Load(Write("g0", NoEndpointsSource)));
        await using var app = await Start(contexts);
        var client = app.GetTestClient();

        (await client.GetAsync("/live-endpoint")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the boot generation maps nothing");

        contexts.Commit(contexts.Load(Write("g1", ModuleSource(1))));

        (await client.GetStringAsync("/live-endpoint")).Should().Be("v1",
            "the generation that adds the attribute must be mapped in the running process");
    }

    private const string NoEndpointsSource = """
        namespace MeshWeaver.Test.LiveEndpoints;
        public sealed class Placeholder { }
        """;

    /// <summary>The NEGATIVE CONTROL for the refusal: a new generation whose route collides with one the
    /// host already serves is NOT published — the previous endpoints keep serving.</summary>
    [Fact]
    public async Task ASwapWhoseRouteCollidesWithTheHost_IsNotPublished_AndThePreviousRouteKeepsServing()
    {
        using var contexts = new ModuleContexts();
        contexts.Commit(contexts.Load(Write("g1", ModuleSource(1))));
        await using var app = await Start(contexts);
        var client = app.GetTestClient();

        contexts.Commit(contexts.Load(Write("g2", ModuleSource(2, collide: true))));

        (await client.GetStringAsync("/live-endpoint")).Should().Be("v1",
            "a colliding re-map must not be published — never two registrations on one (verb, pattern)");
        (await client.GetStringAsync("/host-route")).Should().Be("host");
    }

    /// <summary>#6128 review: the refusal is scoped PER MODULE. Module B's new generation collides with
    /// the host and is refused; module A's later swap must STILL go live. With the old all-or-nothing
    /// re-map, B's colliding current generation refused every later map, so A's swap stayed unpublished.</summary>
    [Fact]
    public async Task OneModulesRefusedSwap_DoesNotFreezeAnotherModulesEndpoints()
    {
        using var contexts = new ModuleContexts();
        contexts.Commit(contexts.Load(Write("g1", ModuleSource(1))));
        contexts.Commit(contexts.Load(Write("g1", ModuleSource(1, route: "/other-endpoint", module: OtherModule), OtherModule)));
        await using var app = await Start(contexts);
        var client = app.GetTestClient();
        (await client.GetStringAsync("/live-endpoint")).Should().Be("v1");
        (await client.GetStringAsync("/other-endpoint")).Should().Be("v1");

        contexts.Commit(contexts.Load(Write("g2", ModuleSource(2, collide: true, module: OtherModule), OtherModule)));
        (await client.GetStringAsync("/other-endpoint")).Should().Be("v1",
            "the colliding module's swap is refused and its previous route keeps serving");

        contexts.Commit(contexts.Load(Write("g2", ModuleSource(2))));
        (await client.GetStringAsync("/live-endpoint")).Should().Be("v2",
            "another module's refused generation must not freeze this module's live swap");
        (await client.GetStringAsync("/other-endpoint")).Should().Be("v1",
            "the refused module still serves its previous route");
        (await client.GetStringAsync("/host-route")).Should().Be("host");
    }

    private const string OtherModule = "MeshWeaver.Test.LiveEndpointsOther";

    /// <summary>#6128 review: two modules that EXCHANGE routes across swap waves. A's new map takes B's
    /// route, which B still serves, so A is refused. B's new map then takes A's old route. Measured
    /// one at a time against each other's stale served map, both would be refused on every later swap,
    /// for good. Decided together, the combined set has no collision, so both go live.</summary>
    [Fact]
    public async Task TwoModulesExchangingRoutes_AreBothPublished_OnceTheExchangeCompletes()
    {
        using var contexts = new ModuleContexts();
        contexts.Commit(contexts.Load(Write("g1", ModuleSource(1, route: "/route-a"))));
        contexts.Commit(contexts.Load(Write("g1", ModuleSource(1, route: "/route-b", module: OtherModule), OtherModule)));
        await using var app = await Start(contexts);
        var client = app.GetTestClient();

        contexts.Commit(contexts.Load(Write("g2", ModuleSource(2, route: "/route-b"))));
        (await client.GetStringAsync("/route-b")).Should().Be("v1",
            "half-way through the exchange A's new route still collides with B's served one, so A is refused");

        contexts.Commit(contexts.Load(Write("g2", ModuleSource(2, route: "/route-a", module: OtherModule), OtherModule)));
        (await client.GetStringAsync("/route-a")).Should().Be("v2",
            "once the exchange completes, B now serves A's old route");
        (await client.GetStringAsync("/route-b")).Should().Be("v2",
            "and A serves B's old route — the pending maps are decided together, so neither is refused for good");
    }

    /// <summary>#6128 review: the swap subscription is bound to the HOST, not the module registry. The
    /// registry can outlive a host, so once the host stops, a later swap must not re-map this source
    /// (which would fire change tokens no matcher reads, and root the source through the registry's
    /// subject). The control: while the host runs, the same swap does re-map.</summary>
    [Fact]
    public async Task AStoppedHostsSource_NoLongerFollowsSwaps_WhileARunningOneDoes()
    {
        using var contexts = new ModuleContexts();
        contexts.Commit(contexts.Load(Write("g1", ModuleSource(1))));
        await using var app = await Start(contexts);
        var source = ((IEndpointRouteBuilder)app).DataSources.OfType<ModuleEndpointDataSource>().Single();

        var boot = source.Endpoints;
        contexts.Commit(contexts.Load(Write("g2", ModuleSource(2))));
        var running = source.Endpoints;
        running.Should().NotBeSameAs(boot, "the control: a running host's source re-maps on a swap");

        await app.StopAsync();
        contexts.Commit(contexts.Load(Write("g3", ModuleSource(3))));
        source.Endpoints.Should().BeSameAs(running,
            "a stopped host's source must have ended its subscription to the registry's swaps");
    }

    /// <summary>#6128 review: the dynamic source publishes its BOOT map without its own collision
    /// check, so the boot routes of a held module must be caught by the host's startup refusal, which
    /// reads the composite endpoint table the dynamic source is part of. The control: the same host
    /// with a non-colliding held module keeps running.</summary>
    [Fact]
    public async Task AHeldModuleWhoseBootRouteCollidesWithTheHost_IsRefusedAtStartup()
    {
        using var colliding = new ModuleContexts();
        colliding.Commit(colliding.Load(Write("g1", ModuleSource(1, collide: true))));
        await using (var refused = await Start(colliding))
            refused.Lifetime.ApplicationStopping.IsCancellationRequested.Should().BeTrue(
                "a held module's boot route on the host's (verb, pattern) must take the app down at startup, "
                + "never be resolved by registration order at request time");

        using var clean = new ModuleContexts();
        clean.Commit(clean.Load(Write("g2", ModuleSource(1))));
        await using var served = await Start(clean);
        served.Lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse(
            "the control: a held module whose routes collide with nothing starts and serves");
    }

    private static async Task<WebApplication> Start(ModuleContexts contexts)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(contexts);
        builder.Services.AddAuthorization();
        builder.Services.AddRouting();
        var app = builder.Build();
        app.UseRouting();
        app.UseAuthorization();
        app.MapGet("/host-route", () => "host").AllowAnonymous();
        app.MapMeshModuleEndpoints();
        await app.StartAsync();
        return app;
    }

    private static string ModuleSource(int version, bool collide = false, string route = "/live-endpoint", string module = Module) => $$"""
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Routing;
        [assembly: {{module}}.Endpoints]
        namespace {{module}};
        public sealed class EndpointsAttribute : MeshWeaver.Hosting.AspNetCore.MeshEndpointProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<System.Action<IEndpointRouteBuilder>> EndpointConfigurations =>
            [
                routes => routes.MapGet("{{(collide ? "/host-route" : route)}}", () => "v{{version}}").AllowAnonymous(),
            ];
        }
        """;

    private string Write(string generation, string source, string module = Module)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(MeshEndpointProviderAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(module, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{module}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
