using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A registry transfer FAILS ON SILENCE, at a NAMED stage, against a real socket (#4528).
///
/// <para><b>What was measured.</b> With #4549's streaming shape live on every replica of both
/// production deployments (2026-09-19, five pods, ~10.5 h on the image), the 120 s attempt budget
/// on <c>plugin-registry-bundles</c> was still exceeded roughly 1.5× a minute, at the reconciler's
/// 180 s per-package period to within 5 ms — and nothing in the fleet could say WHICH stage timed
/// out, because the transport pipeline's attempt timeout retries inside a 3-minute outer bound
/// that cannot hold the retry, so every occurrence surfaced as a bare "The operation has timed
/// out". A second consumer (#4963, a plain <c>curl</c> from a CI runner) then showed the shape from
/// outside the mesh: <i>180 s, 0 bytes received</i> — the registry accepting the connection and
/// never beginning a response.</para>
///
/// <para><b>What this pins.</b> The client owns the clock at every stage: a registry that never
/// begins a response is refused after ONE stall budget as <see cref="BundleTransferStage.NoResponse"/>;
/// one that sends headers and then goes quiet is refused as
/// <see cref="BundleTransferStage.StalledMidBody"/> with the byte count it reached; one that
/// streams slowly but continuously for longer than the budget SUCCEEDS (the bound is on silence,
/// never on total duration); one that is larger than the client accepts is refused as
/// <see cref="BundleTransferStage.OverSize"/> — before a byte when the length is declared, at the
/// bound when it is not — which re-establishes the bound the buffering read used to carry and
/// streaming had removed. And the index is read ONCE per client: it used to be re-sent per package,
/// so a stalled registry cost every package of a pass the whole stall.</para>
///
/// <para><b>Negative controls.</b> Every stall case runs under an outer <c>.Timeout</c> of three
/// stall budgets: without the client's own bound the request hangs until the transport's 100 s
/// clock, the outer timeout fires first, and the assertion fails with Rx's
/// <see cref="TimeoutException"/> instead of the named refusal — a hang reproduces as a red test,
/// never as a pass. The server is Kestrel on a loopback port, so what is exercised is the real
/// <c>HttpClient</c> against a real TCP connection, not a handler double.</para>
/// </summary>
public class BundleTransferFailsOnSilenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The silence budget under test — small, and scaled with the CI factor.</summary>
    private static TimeSpan StallBudget => TestTimeouts.Quick / 3;

    /// <summary>Three stall budgets: the bound that turns a hang into a red test.</summary>
    private static TimeSpan Outer => TestTimeouts.Quick;

    private const long ByteBound = 4096;
    private const string Plugin = "StallProbe";
    private const string Version = "1.0.0";

    private StallingRegistry registry = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        registry = await StallingRegistry.StartAsync(chunkGap: StallBudget / 2, byteBound: ByteBound);
    }

    // Async teardown because Kestrel stops asynchronously; a blocking bridge here is what
    // BlockingBridgeInTestRatchetGuard exists to prevent.
    public override async ValueTask DisposeAsync()
    {
        await registry.DisposeAsync();
        await base.DisposeAsync();
    }

    private PluginBundleClient Client() =>
        new(Mesh, registry.BaseUrl, "mwi_probe") { StallBudget = StallBudget, MaxTransferBytes = ByteBound };

    private static Task<BundleTransferException> Refusal(IObservable<object> transfer) =>
        Assert.ThrowsAsync<BundleTransferException>(() => transfer.Timeout(Outer).Await(Ct));

    private static void AssertWaitedTheBudget(BundleTransferException refusal) =>
        Assert.True(
            refusal.Elapsed >= StallBudget * 0.9,
            $"the refusal came after {refusal.Elapsed.TotalMilliseconds:0} ms, before the "
            + $"{StallBudget.TotalMilliseconds:0} ms silence budget — it did not fall on the silence bound");

    /// <summary>The #4963 shape, on the INDEX: the connection is accepted and nothing is ever
    /// written. Refused after one stall budget, naming the stage, the registry and zero bytes.</summary>
    [Fact]
    public async Task AnIndexThatNeverBeginsAResponse_IsRefusedAsNoResponse_AfterOneStallBudget()
    {
        registry.Mode = StallingRegistry.Behaviour.NeverAnswer;

        var refusal = await Refusal(Client().FetchIndex().Select(i => (object)i));

        Assert.Equal(BundleTransferStage.NoResponse, refusal.Stage);
        Assert.Equal(0, refusal.Received);
        Assert.Null(refusal.Declared);
        Assert.Equal((long)StallBudget.TotalSeconds, refusal.Bound);
        Assert.Contains("did not begin a response", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(registry.BaseUrl.TrimEnd('/'), refusal.Message, StringComparison.Ordinal);
        AssertWaitedTheBudget(refusal);
    }

    /// <summary>The same shape on the BUNDLE route.</summary>
    [Fact]
    public async Task ABundleThatNeverBeginsAResponse_IsRefusedAsNoResponse()
    {
        registry.Mode = StallingRegistry.Behaviour.NeverAnswer;

        var refusal = await Refusal(Client().DownloadOverHttp(Plugin, Version).Select(r => (object)r));

        Assert.Equal(BundleTransferStage.NoResponse, refusal.Stage);
        Assert.Equal(0, refusal.Received);
        Assert.Contains($"Bundle for {Plugin}@{Version}", refusal.Message, StringComparison.Ordinal);
        AssertWaitedTheBudget(refusal);
    }

    /// <summary>Headers and a prefix arrive, then nothing: refused as a MID-BODY stall carrying how
    /// far it got — the number that separates "the registry stopped" from "the transport did".</summary>
    [Fact]
    public async Task ABodyThatGoesQuiet_IsRefusedAsStalledMidBody_WithTheBytesItReached()
    {
        registry.Mode = StallingRegistry.Behaviour.HeadersThenSilence;
        registry.PrefixBytes = 100;

        var refusal = await Refusal(Client().DownloadOverHttp(Plugin, Version).Select(r => (object)r));

        Assert.Equal(BundleTransferStage.StalledMidBody, refusal.Stage);
        Assert.Equal(100, refusal.Received);
        Assert.Null(refusal.Declared);
        Assert.Contains("sent no data for", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("100 of an undeclared number of byte(s)", refusal.Message, StringComparison.Ordinal);
        AssertWaitedTheBudget(refusal);
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL: a body that streams slowly but continuously for longer than the
    /// stall budget SUCCEEDS. This is the property #4549 bought and the one a total-duration bound
    /// — the transport's, or the buffering default's — would break: the transfer takes about
    /// 2.5 stall budgets end to end and is never cut, because no single gap reaches the bound.
    /// </summary>
    [Fact]
    public async Task ABodyThatTricklesLongerThanTheBudget_StillCompletes()
    {
        registry.Mode = StallingRegistry.Behaviour.SlowButAlive;
        var clock = Stopwatch.StartNew();

        var result = await Client().DownloadOverHttp(Plugin, Version)
            .Timeout(Outer + Outer)
            .Await(Ct);

        Assert.Equal(BundleAdoptionKind.Adopted, result.Kind);
        Assert.NotNull(result.Bytes);
        Assert.Equal(StallingRegistry.ChunkCount * StallingRegistry.ChunkBytes, result.Bytes!.Length);
        Assert.True(
            clock.Elapsed > StallBudget,
            $"the trickle completed in {clock.Elapsed.TotalMilliseconds:0} ms, inside one stall budget "
            + $"({StallBudget.TotalMilliseconds:0} ms) — it did not exercise the silence-not-duration property");
    }

    /// <summary>A declared length over the bound is refused BEFORE a byte is read.</summary>
    [Fact]
    public async Task ADeclaredOversizedBody_IsRefusedBeforeAByteIsRead()
    {
        registry.Mode = StallingRegistry.Behaviour.DeclaredOversize;

        var refusal = await Refusal(Client().DownloadOverHttp(Plugin, Version).Select(r => (object)r));

        Assert.Equal(BundleTransferStage.OverSize, refusal.Stage);
        Assert.Equal(0, refusal.Received);
        Assert.Equal(ByteBound + 1, refusal.Declared);
        Assert.Equal(ByteBound, refusal.Bound);
        Assert.Contains("larger than the 4096 byte(s) this client accepts", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>An undeclared (chunked) body is refused the moment it crosses the bound.</summary>
    [Fact]
    public async Task AnUndeclaredOversizedBody_IsRefusedAtTheBound()
    {
        registry.Mode = StallingRegistry.Behaviour.UndeclaredOversize;

        var refusal = await Refusal(Client().DownloadOverHttp(Plugin, Version).Select(r => (object)r));

        Assert.Equal(BundleTransferStage.OverSize, refusal.Stage);
        Assert.True(refusal.Received > ByteBound, $"refused at {refusal.Received} byte(s), which is not over the bound");
        Assert.Null(refusal.Declared);
    }

    /// <summary>
    /// 🚨 ONE index read per client. The slot used to cache a COLD observable, so every package's
    /// <c>Adopt</c> re-sent the index request — and a stalled registry was paid for by every
    /// package of the pass, which is the 180 s per-package period the incident showed.
    /// </summary>
    [Fact]
    public async Task TheIndex_IsReadOnce_ForEveryPackageOfOneClient()
    {
        registry.Mode = StallingRegistry.Behaviour.Healthy;
        var client = Client();

        await client.Adopt(Plugin).Timeout(Outer).Await(Ct);
        await client.Adopt("AnotherPackage").Timeout(Outer).Await(Ct);
        Assert.Equal(1, registry.IndexRequests);

        // A second CLIENT is a second pass and asks again: the sharing is per client, not global.
        await Client().Adopt(Plugin).Timeout(Outer).Await(Ct);
        Assert.Equal(2, registry.IndexRequests);
    }

    /// <summary>A refused index read is EVICTED, so the next package asks again rather than
    /// replaying the refusal for the rest of the pass (#1369).</summary>
    [Fact]
    public async Task ARefusedIndexRead_IsEvicted_SoTheNextPackageAsksAgain()
    {
        registry.Mode = StallingRegistry.Behaviour.NeverAnswer;
        var client = Client();
        await Refusal(client.Adopt(Plugin).Select(n => (object)n));
        Assert.Equal(1, registry.IndexRequests);

        registry.Mode = StallingRegistry.Behaviour.Healthy;
        await client.Adopt(Plugin).Timeout(Outer).Await(Ct);
        Assert.Equal(2, registry.IndexRequests);
    }

    /// <summary>
    /// The transport fact the design leans on: the standard resilience handler the hosts register
    /// for <c>plugin-registry-bundles</c> leaves <see cref="HttpClient.Timeout"/> INFINITE, so the
    /// client's stage clocks are the only clocks over the header stage — and the fallback client
    /// without a factory says the same. A 100 s transport clock in front of a 120 s stall budget
    /// would cut every stall one stage earlier under a message that names no stage.
    /// </summary>
    [Fact]
    public void TheStandardResilienceHandler_LeavesTheTransportClockInfinite()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(InstanceRegistrationClient.BundleHttpClientName)
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = PluginBundleClient.TransferStallBudget;
                options.TotalRequestTimeout.Timeout = PluginBundleClient.TransferStallBudget * 2.5;
                options.CircuitBreaker.SamplingDuration = PluginBundleClient.TransferStallBudget * 2;
            });
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(InstanceRegistrationClient.BundleHttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    /// <summary>The reconciler's outer bound is DERIVED from the client's stall budget and sits
    /// above it, so a stalled stage is always refused — and named — before the outer bound cuts
    /// the package. Pinned because two independently authored numbers is how the inversion began.</summary>
    [Fact]
    public void TheReconcilersPerPackageBudget_IsDerivedFromTheStallBudget_AndExceedsIt()
    {
        var headroom = RegistryUpdateReconciler.PerPackageAdoptBudget - PluginBundleClient.TransferStallBudget;
        Assert.True(headroom > TimeSpan.Zero, "the outer bound must fire AFTER the client's own refusal");
        // Headroom for the read, the decision and the landing — less than a second stall budget,
        // because a second stalled stage is the client's to refuse, not the reconciler's to wait for.
        Assert.True(headroom < PluginBundleClient.TransferStallBudget);
    }

    /// <summary>
    /// A loopback registry that misbehaves on request: Kestrel bound to port 0 (never
    /// <c>HttpListener</c>, whose port reservation has a check-then-use window — #2436), serving the
    /// two bundle routes with one selectable behaviour, and counting index requests.
    /// </summary>
    private sealed class StallingRegistry : IAsyncDisposable
    {
        public enum Behaviour
        {
            /// <summary>Accept the connection, write nothing, until the client hangs up.</summary>
            NeverAnswer,
            /// <summary>Send 200 + headers (+ <see cref="PrefixBytes"/>), then write nothing.</summary>
            HeadersThenSilence,
            /// <summary><see cref="ChunkCount"/> chunks, each followed by a gap of half a stall budget.</summary>
            SlowButAlive,
            /// <summary>Declare <c>Content-Length</c> one over the bound, then send it.</summary>
            DeclaredOversize,
            /// <summary>Chunked, one byte over the bound.</summary>
            UndeclaredOversize,
            /// <summary>A real index advertising <see cref="Plugin"/> for this lane; 404 for bundles.</summary>
            Healthy,
        }

        // Five chunks of 500 bytes: well inside the byte bound the tests inject (4096), so the
        // trickle exercises the SILENCE bound alone — its first draft sent 5,000 bytes and was
        // refused as OverSize, which was the size bound doing its job on the wrong test.
        public const int ChunkCount = 5;
        public const int ChunkBytes = 500;

        private WebApplication app = null!;
        private TimeSpan chunkGap;
        private long byteBound;
        private int indexRequests;

        public string BaseUrl { get; private set; } = string.Empty;
        public volatile Behaviour Mode = Behaviour.Healthy;
        public volatile int PrefixBytes;
        public int IndexRequests => Volatile.Read(ref indexRequests);

        private StallingRegistry()
        {
        }

        public static async Task<StallingRegistry> StartAsync(TimeSpan chunkGap, long byteBound)
        {
            var server = new StallingRegistry { chunkGap = chunkGap, byteBound = byteBound };

            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddRoutingCore();
            builder.Logging.ClearProviders();

            var app = builder.Build();
            app.Run(server.Serve);
            await app.StartAsync().ConfigureAwait(false);

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            server.app = app;
            server.BaseUrl = address.EndsWith('/') ? address : address + "/";
            return server;
        }

        private async Task Serve(HttpContext context)
        {
            var isIndex = context.Request.Path.Value!.EndsWith("/index.json", StringComparison.Ordinal);
            if (isIndex)
                Interlocked.Increment(ref indexRequests);
            var aborted = context.RequestAborted;
            var response = context.Response;
            try
            {
                switch (Mode)
                {
                    case Behaviour.NeverAnswer:
                        await Park(aborted).ConfigureAwait(false);
                        return;

                    case Behaviour.HeadersThenSilence:
                        response.StatusCode = StatusCodes.Status200OK;
                        response.ContentType = "application/octet-stream";
                        await response.StartAsync(aborted).ConfigureAwait(false);
                        if (PrefixBytes > 0)
                        {
                            await response.Body.WriteAsync(new byte[PrefixBytes], aborted).ConfigureAwait(false);
                            await response.Body.FlushAsync(aborted).ConfigureAwait(false);
                        }
                        await Park(aborted).ConfigureAwait(false);
                        return;

                    case Behaviour.SlowButAlive:
                        response.StatusCode = StatusCodes.Status200OK;
                        response.ContentType = "application/octet-stream";
                        await response.StartAsync(aborted).ConfigureAwait(false);
                        for (var i = 0; i < ChunkCount; i++)
                        {
                            await response.Body.WriteAsync(new byte[ChunkBytes], aborted).ConfigureAwait(false);
                            await response.Body.FlushAsync(aborted).ConfigureAwait(false);
                            if (i < ChunkCount - 1)
                                // The stub's pacing — the thing under test is that this gap, being
                                // shorter than the budget, is NOT a stall.
                                await Observable.Timer(chunkGap).Await(aborted).ConfigureAwait(false);
                        }
                        return;

                    case Behaviour.DeclaredOversize:
                        response.StatusCode = StatusCodes.Status200OK;
                        response.ContentType = "application/octet-stream";
                        response.ContentLength = byteBound + 1;
                        await response.Body.WriteAsync(new byte[byteBound + 1], aborted).ConfigureAwait(false);
                        return;

                    case Behaviour.UndeclaredOversize:
                        response.StatusCode = StatusCodes.Status200OK;
                        response.ContentType = "application/octet-stream";
                        await response.StartAsync(aborted).ConfigureAwait(false);
                        await response.Body.WriteAsync(new byte[byteBound], aborted).ConfigureAwait(false);
                        await response.Body.FlushAsync(aborted).ConfigureAwait(false);
                        await response.Body.WriteAsync(new byte[1], aborted).ConfigureAwait(false);
                        return;

                    case Behaviour.Healthy:
                        if (isIndex)
                        {
                            response.StatusCode = StatusCodes.Status200OK;
                            response.ContentType = "application/json";
                            await response.WriteAsync(IndexJson(), Encoding.UTF8, aborted).ConfigureAwait(false);
                        }
                        else
                        {
                            response.StatusCode = StatusCodes.Status404NotFound;
                        }
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                // The client gave up on this request, which is exactly what the stall cases
                // assert on the client side; nothing to record here.
            }
        }

        /// <summary>The stall under test: hold the request open, writing nothing, until the client
        /// hangs up. Awaited through the one sanctioned bridge; the abort token ends the wait.</summary>
        private static async Task Park(CancellationToken aborted)
        {
            try
            {
                await Observable.Never<Unit>().Await(aborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The client hung up — the park is over.
            }
        }

        private string IndexJson() => JsonSerializer.Serialize(new
        {
            frameworkMvid = PrebuiltAssemblySeeder.LiveFrameworkMvid,
            architecture = ReleaseArchitecture.Live,
            bundles = new[]
            {
                new { plugin = Plugin, version = Version, url = $"{BaseUrl}api/plugins/bundles/{Plugin}/{Version}" },
            },
        });

        public async ValueTask DisposeAsync() => await app.DisposeAsync().ConfigureAwait(false);
    }
}
