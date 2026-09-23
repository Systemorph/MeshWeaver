#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A bundle route whose catalogue read STALLS answers 503 + <c>Retry-After</c> — never an
/// unhandled 500 (MeshWeaver#5345).
///
/// <para>Both bundle routes read the installed-package catalogue
/// (<c>namespace:Plugins nodeType:Package</c>) through the query fan-in. When a provider never
/// delivers its Initial, the fan-in terminates the read with
/// <see cref="QueryProviderStalledException"/> (policy <c>query-fanin-stall-terminal</c>) — an
/// availability failure that states, in its own text, that it is retryable. The routes let it
/// escape, so ASP.NET's exception middleware answered a bare 500 and logged the stall as an
/// unhandled exception of the ENDPOINT: measured on memex-cloud 2026-09-22 14:43:42Z, pod
/// <c>5c444645f8-8pdzb</c>, two occurrences, inside the same minute as 88 compile-watcher stalls on
/// that pod.</para>
///
/// <para><b>The provider here is a test PROVIDER, not a mock of a core service</b> — the
/// <see cref="IMeshQueryProvider"/> extension point every backend plugs into, the same seam
/// <c>QueryFanInStallIsTerminalTest</c> and <c>ColdEmptyInitialIsNotCachedTest</c> drive. It
/// claims ONLY the catalogue query, and only while <see cref="StallingCatalogueProvider.Stalling"/>
/// is set, so the instance-key authentication in front of the routes reads the real mesh. The stall
/// fires at the fan-in's own production rung (<see cref="MeshOperationOptions.QueryInitialBudget"/>);
/// nothing here shortens it.</para>
/// </summary>
public class PluginBundleStalledReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Source = "Plugins";
    private const string Instance = "stalled-read-consumer";

    private readonly StallingCatalogueProvider provider = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddPluginCatalog()
        .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(provider));

    private Task<string> RegisterInstance() =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                    [
                        new KeyValuePair<string, string?>(
                            $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:0", $"{Source}/*"),
                    ])
                    .Build())
            .Register("stalled-owner", "Stalled Owner", "owner@test.com", Instance, Instance)
            .Select(r => r.RawKey)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(TestContext.Current.CancellationToken);

    private async Task<WebApplication> StartBundleHost(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>()));
        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync(cancellationToken);
        return app;
    }

    private static async Task<HttpResponseMessage> Get(WebApplication app, string key, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PluginBundleEndpoints.RoutePrefix + route);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return await app.GetTestClient().SendAsync(request);
    }

    /// <summary>
    /// THE CHANGE, on both routes that read the catalogue: the stall arrives as 503 with
    /// <c>Retry-After</c>. Unfixed, the exception escapes the handler and the TestServer rethrows it
    /// (or answers 500) — either way no 503.
    /// </summary>
    [Theory(Timeout = 300_000)]
    [InlineData("/index.json")]
    [InlineData("/Plugins.Anything/1.0.0")]
    public async Task AStalledCatalogueRead_Answers503WithRetryAfter(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterInstance();
        var app = await StartBundleHost(ct);
        await using var _ = app;

        provider.Stalling = true;
        using var response = await Get(app, key, route);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the catalogue read produced no answer — an availability failure that says it is "
            + "retryable — so the wire must say 'ask again later', not report an unhandled 500");
        response.Headers.RetryAfter.Should().NotBeNull("a 503 without Retry-After gives the consumer no schedule");
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(InstanceRegistryAuthenticator.RetryAfterSeconds),
            "the bundle routes use the same retry convention as the instance-key 503");
        provider.StalledReads.Should().BeGreaterThan(0,
            "the stall must actually have been the catalogue read — otherwise this test measured "
            + "something else");
    }

    /// <summary>
    /// THE CONTROL: the same host, the same provider answering — the index is 200. Without this arm a
    /// route that answered 503 unconditionally would pass the case above.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnAnsweredCatalogueRead_StillAnswers200()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterInstance();
        var app = await StartBundleHost(ct);
        await using var _ = app;

        provider.Stalling = false;
        using var response = await Get(app, key, "/index.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        json.RootElement.TryGetProperty("bundles", out var bundles).Should().BeTrue();
        bundles.ValueKind.Should().Be(JsonValueKind.Array);
    }

    /// <summary>
    /// THE OTHER SIDE of the classification: an ordinary DEFECT on the same read is NOT dressed up as
    /// "come back later". It still escapes the route, so a real endpoint defect stays visible, and
    /// only an availability fault becomes 503. Without this arm a catch widened to every exception
    /// would pass the stall case above while hiding real defects behind a retry.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnOrdinaryFault_OnTheCatalogueRead_IsNotConvertedTo503()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterInstance();
        var app = await StartBundleHost(ct);
        await using var _ = app;

        provider.Faulting = true;
        HttpStatusCode? status = null;
        Exception? escaped = null;
        try
        {
            using var response = await Get(app, key, "/index.json");
            status = response.StatusCode;
        }
        catch (Exception ex)
        {
            // TestServer rethrows an unhandled pipeline exception to the client by default — that
            // IS the escape this case asserts.
            escaped = ex;
        }

        (escaped is not null || status == HttpStatusCode.InternalServerError).Should().BeTrue(
            $"a defect must still escape as one (status {status?.ToString() ?? "none"}, "
            + $"exception {escaped?.GetType().Name ?? "none"})");
        status.Should().NotBe(HttpStatusCode.ServiceUnavailable,
            "only an availability fault may be answered 'retry later'");
        if (escaped is not null)
            escaped.ToString().Should().Contain(DefectMessage,
                "the exception that escaped must be the catalogue read's own defect, not something else");
    }

    private const string DefectMessage = "catalogue read defect (test)";

    /// <summary>
    /// Claims every query (so it is in every fan-in) and answers each with an empty Initial — except
    /// the installed-package catalogue query while <see cref="Stalling"/> is set, which it subscribes
    /// and never answers: no Initial, no completion, no error. That is the production shape the
    /// fan-in's stall terminal exists for.
    /// </summary>
    private sealed class StallingCatalogueProvider : IMeshQueryProvider
    {
        private int stalling;
        private int stalledReads;

        public string Name => nameof(StallingCatalogueProvider);

        public bool Stalling
        {
            get => Volatile.Read(ref stalling) == 1;
            set => Interlocked.Exchange(ref stalling, value ? 1 : 0);
        }

        private int faulting;

        /// <summary>When set, the catalogue read FAULTS with an ordinary defect
        /// (<see cref="InvalidOperationException"/>) instead of stalling.</summary>
        public bool Faulting
        {
            get => Volatile.Read(ref faulting) == 1;
            set => Interlocked.Exchange(ref faulting, value ? 1 : 0);
        }

        /// <summary>How many catalogue reads were left unanswered.</summary>
        public int StalledReads => Volatile.Read(ref stalledReads);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(
            MeshQueryRequest request, JsonSerializerOptions options) =>
            Observable.Defer(() =>
            {
                var catalogue = request.EffectiveQueries.Any(q =>
                    q.Contains($"nodeType:{PackageInstaller.PackageNodeType}", StringComparison.Ordinal));
                if (catalogue && Faulting)
                    return Observable.Throw<QueryResultChange<T>>(
                        new InvalidOperationException(DefectMessage));
                if (!catalogue || !Stalling)
                    return Observable.Return(new QueryResultChange<T>
                    {
                        ChangeType = QueryChangeType.Initial,
                        Items = Array.Empty<T>(),
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                Interlocked.Increment(ref stalledReads);
                return Observable.Never<QueryResultChange<T>>();
            });

        public IObservable<IReadOnlyCollection<QueryResult>> Query(
            MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }
}
