using System;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <see cref="OpenGraphPreviewService"/> against a REAL loopback HTTP server
/// (<see cref="TestOgServer"/>): the promise cache fetches each URL once and replays to every
/// subscriber; a FAILED fetch surfaces the URL-only fallback and evicts its entry so the next
/// subscriber retries once; and the SSRF guard refuses non-http(s) schemes and literal
/// loopback / private / link-local hosts without issuing any request.
/// </summary>
public sealed class OpenGraphPreviewServiceTest : IDisposable
{
    private readonly TestOgServer server = new();
    private readonly IoPoolRegistry pools = new();
    private readonly HttpClient http = new();

    private OpenGraphPreviewService CreateService(bool allowLoopback = true) =>
        new(() => pools.Get(IoPoolNames.Http), () => http, allowLoopback);

    private static Task<OpenGraphPreview> Await(IObservable<OpenGraphPreview> preview) =>
        preview.FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask();

    [Fact]
    public async Task Get_SameUrlTwice_FetchesOnceAndReplays()
    {
        var service = CreateService();
        var url = server.BaseUrl + "page";

        var first = await Await(service.Get(url));
        var second = await Await(service.Get(url));

        Assert.Equal(1, server.RequestCount);
        Assert.True(first.Fetched);
        Assert.Equal("Served Title", first.Title);
        Assert.Equal("Served description.", first.Description);
        // The relative og:image resolves against the page URL.
        Assert.Equal(server.BaseUrl + "og.png", first.Image);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Get_FailedFetch_FallsBackEvictsAndRetriesOnNextSubscriber()
    {
        var service = CreateService();
        var url = server.BaseUrl + "flaky";

        server.StatusCode = 500;
        var failed = await Await(service.Get(url));

        Assert.False(failed.Fetched);
        Assert.Null(failed.Title);
        Assert.Equal(url, failed.Url);

        // The failure evicted the entry: the next page view's subscriber re-runs the fetch once.
        server.StatusCode = 200;
        var recovered = await Await(service.Get(url));

        Assert.True(recovered.Fetched);
        Assert.Equal("Served Title", recovered.Title);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task Get_GuardedTarget_YieldsFallbackWithoutAnyRequest()
    {
        var service = CreateService(allowLoopback: false);

        var preview = await Await(service.Get(server.BaseUrl + "page"));

        Assert.False(preview.Fetched);
        Assert.Equal(0, server.RequestCount);
    }

    [Theory]
    [InlineData("ftp://example.org/file")]
    [InlineData("not a url")]
    [InlineData("https://localhost/admin")]
    [InlineData("https://127.0.0.1/admin")]
    [InlineData("https://10.1.2.3/internal")]
    [InlineData("https://172.16.0.1/internal")]
    [InlineData("https://192.168.1.1/router")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public void IsFetchable_RefusesForgeableTargets(string url) =>
        Assert.False(CreateService(allowLoopback: false).IsFetchable(url));

    [Theory]
    [InlineData("https://memex.meshweaver.cloud/Underwriting")]
    [InlineData("http://example.org/page")]
    [InlineData("https://8.8.8.8/page")]
    [InlineData("https://172.15.0.1/page")]
    public void IsFetchable_AllowsPublicTargets(string url) =>
        Assert.True(CreateService(allowLoopback: false).IsFetchable(url));

    public void Dispose()
    {
        server.Dispose();
        pools.Dispose();
        http.Dispose();
    }
}
