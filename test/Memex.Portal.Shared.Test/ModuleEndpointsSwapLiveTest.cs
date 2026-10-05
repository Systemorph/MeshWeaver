#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
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

        (await contexts.Retire(replaced!, Budget).Timeout(Budget).Await()).Should().BeTrue();
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

    private static string ModuleSource(int version, bool collide = false) => $$"""
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Routing;
        [assembly: MeshWeaver.Test.LiveEndpoints.Endpoints]
        namespace MeshWeaver.Test.LiveEndpoints;
        public sealed class EndpointsAttribute : MeshWeaver.Hosting.AspNetCore.MeshEndpointProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<System.Action<IEndpointRouteBuilder>> EndpointConfigurations =>
            [
                routes => routes.MapGet("{{(collide ? "/host-route" : "/live-endpoint")}}", () => "v{{version}}").AllowAnonymous(),
            ];
        }
        """;

    private string Write(string generation, string source)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(MeshEndpointProviderAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Module}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
