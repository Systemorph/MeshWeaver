using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A module bundle adopted AFTER the host started serves its <c>wwwroot</c> assets without a
/// restart: the static-asset lane resolves the mounts per request from the modules loaded NOW,
/// rather than mounting what existed at startup and never looking again.
/// </summary>
public class AModuleAdoptedAfterBootServesItsStaticAssetsTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-live-assets-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string LayDownModule(string name, string file)
    {
        var wwwroot = Path.Combine(root, name, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, file), "export {};");
        return wwwroot;
    }

    private sealed class Host
    {
        public long Stamp;
        public readonly List<ModuleStaticAssetMount> Mounts = [];
        public int Builds;
        public RequestDelegate App = null!;
    }

    private static Host Start()
    {
        var host = new Host();
        var source = new ModuleStaticAssetSource(
            () => Interlocked.Read(ref host.Stamp),
            () =>
            {
                Interlocked.Increment(ref host.Builds);
                return new ModuleStaticAssetManifest(host.Mounts.ToArray(), []);
            });
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IWebHostEnvironment>(new StubEnvironment())
            .BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        builder.UseLiveMeshModuleStaticAssets(source);
        builder.Run(context =>
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });
        host.App = builder.Build();
        return host;
    }

    private static async Task<int> Get(Host host, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        await host.App(context);
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task AModuleAdoptedAfterTheHostStartedAnswers200()
    {
        var host = Start();
        Assert.Equal(404, await Get(host, "/_content/Late.Module/view.js"));

        // Adopt the module AFTER the host is already serving requests.
        host.Mounts.Add(new ModuleStaticAssetMount(
            "/_content/Late.Module", LayDownModule("Late.Module", "view.js")));
        Interlocked.Increment(ref host.Stamp);

        Assert.Equal(200, await Get(host, "/_content/Late.Module/view.js"));
        // A path the module does not carry still falls through to the rest of the host.
        Assert.Equal(404, await Get(host, "/_content/Late.Module/missing.js"));
    }

    [Fact]
    public async Task AnUnchangedModuleSetIsNotReresolvedPerRequest()
    {
        var host = Start();
        host.Mounts.Add(new ModuleStaticAssetMount(
            "/_content/Early.Module", LayDownModule("Early.Module", "view.js")));
        Interlocked.Increment(ref host.Stamp);

        for (var i = 0; i < 5; i++)
            Assert.Equal(200, await Get(host, "/_content/Early.Module/view.js"));

        Assert.Equal(1, host.Builds);
    }

    [Fact]
    public async Task ConcurrentRequestsAcrossAnAdoptionAllSettleOnTheNewSet()
    {
        var host = Start();
        host.Mounts.Add(new ModuleStaticAssetMount(
            "/_content/A.Module", LayDownModule("A.Module", "a.js")));
        Interlocked.Increment(ref host.Stamp);
        Assert.Equal(200, await Get(host, "/_content/A.Module/a.js"));

        host.Mounts.Add(new ModuleStaticAssetMount(
            "/_content/B.Module", LayDownModule("B.Module", "b.js")));
        Interlocked.Increment(ref host.Stamp);

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i =>
            Task.Run(() => Get(host, i % 2 == 0 ? "/_content/A.Module/a.js" : "/_content/B.Module/b.js"))));
        Assert.All(results, status => Assert.Equal(200, status));
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = "Development";
    }
}
