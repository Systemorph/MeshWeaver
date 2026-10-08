#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
/// 🚨 A webhook delivery whose target-existence read STALLS answers 503 + <c>Retry-After</c> —
/// never an unhandled 500 (MeshWeaver#6125).
///
/// <para><c>WebhookInbox.Deliver</c> confirms the allowlisted target exists with a
/// <c>path:{target}</c> query before storing. When a query provider never delivers its Initial,
/// the fan-in terminates that read with <see cref="QueryProviderStalledException"/> (policy
/// <c>query-fanin-stall-terminal</c>) — retryable by its own statement. The route let it escape, so
/// ASP.NET answered a bare 500 and logged an unhandled exception of
/// <c>WebhookInboxEndpoints.Deliver</c> (measured on memex 2026-10-04 19:25:46Z and 2026-10-05
/// 07:44:45Z, query <c>path:Hosting/PlatformBuilds</c>). The sibling plugin-bundle routes already
/// answer this 503 (#5345); this pins the inbox to the same convention.</para>
///
/// <para>Same harness as <c>PluginBundleStalledReadTest</c>: a test <see cref="IMeshQueryProvider"/>
/// — the extension point every backend plugs into, not a mock of a core service — that claims every
/// query, answers each with an empty Initial, and leaves ONLY the target-existence read unanswered
/// (or faults it) while armed. The stall fires at the fan-in's own production rung.</para>
///
/// <para><b>Negative control.</b> With the endpoint's <c>UnavailableOnAStalledRead</c> line removed,
/// <see cref="AStalledTargetRead_Answers503WithRetryAfter"/> goes red: the exception escapes and the
/// TestServer rethrows it / answers 500.</para>
/// </summary>
public class WebhookInboxStalledReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Target = "Hosting/StalledInbox";
    private const string Route = "/api/hooks/" + Target;
    private const string DefectMessage = "target read defect (test)";

    private readonly StallingTargetProvider provider = new($"path:{Target}");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddWebhookInbox()
        .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(provider));

    private async Task<WebApplication> StartHost(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{WebhookInbox.TargetsConfigSection}:0"] = Target,
        });
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        var app = builder.Build();
        app.MapWebhookInbox();
        await app.StartAsync(ct);
        return app;
    }

    private static Task<HttpResponseMessage> Post(WebApplication app, CancellationToken ct) =>
        app.GetTestClient().PostAsync(Route,
            new StringContent("{\"action\":\"edited\"}", Encoding.UTF8, "application/json"), ct);

    /// <summary>
    /// THE CHANGE: the existence read stalls, and the wire says "ask again later" with the shared
    /// retry schedule. Unfixed, the exception escapes the handler.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AStalledTargetRead_Answers503WithRetryAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartHost(ct);

        provider.Stalling = true;
        using var response = await Post(app, ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the target read produced no answer — an availability failure that says it is "
            + "retryable — so the wire must say 'ask again later', not report an unhandled 500");
        response.Headers.RetryAfter.Should().NotBeNull("a 503 without Retry-After gives the sender no schedule");
        response.Headers.RetryAfter!.Delta.Should().Be(
            TimeSpan.FromSeconds(InstanceRegistryAuthenticator.RetryAfterSeconds),
            "the inbox uses the same retry convention as the plugin-bundle routes");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        json.RootElement.GetProperty("error").GetString().Should().Be(
            WebhookInboxEndpoints.StalledReadError,
            "the body must name what THIS route could not read, not the registry's catalogue");
        provider.StalledReads.Should().BeGreaterThan(0,
            "the stall must actually have been the target-existence read — otherwise this test "
            + "measured something else");
    }

    /// <summary>
    /// THE CONTROL: the same host, the provider answering, the target present — 200 accepted. A route
    /// that answered 503 unconditionally would pass the case above and fail this one.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnAnsweredTargetRead_StillAccepts()
    {
        var ct = TestContext.Current.CancellationToken;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // RunAsSystem, never Observable.Using(ImpersonateAsSystem, …) (#1790): Using opens the
        // AsyncLocal scope on the subscribing thread and disposes it wherever the create's reply
        // lands, which can leave this test's thread latched as System.
        await access.RunAsSystem(
                () => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateOrUpdateNode(
                    new MeshNode(Target)
                    {
                        Name = Target,
                        NodeType = "Markdown",
                        Content = new MarkdownContent { Content = "# Inbox target\n" },
                    }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await using var app = await StartHost(ct);

        provider.Stalling = false;
        using var response = await Post(app, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        json.RootElement.GetProperty("status").GetString().Should().Be("accepted");
    }

    /// <summary>
    /// THE OTHER SIDE of the classification: an ordinary DEFECT on the same read still escapes, so a
    /// real defect stays visible and only an availability fault becomes "retry later".
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnOrdinaryFault_OnTheTargetRead_IsNotConvertedTo503()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartHost(ct);

        provider.Faulting = true;
        HttpStatusCode? status = null;
        Exception? escaped = null;
        try
        {
            using var response = await Post(app, ct);
            status = response.StatusCode;
        }
        catch (Exception ex)
        {
            // TestServer rethrows an unhandled pipeline exception to the client — that IS the escape.
            escaped = ex;
        }

        (escaped is not null || status == HttpStatusCode.InternalServerError).Should().BeTrue(
            $"a defect must still escape as one (status {status?.ToString() ?? "none"}, "
            + $"exception {escaped?.GetType().Name ?? "none"})");
        status.Should().NotBe(HttpStatusCode.ServiceUnavailable,
            "only an availability fault may be answered 'retry later'");
        if (escaped is not null)
            escaped.ToString().Should().Contain(DefectMessage,
                "the exception that escaped must be the target read's own defect, not something else");
    }

    /// <summary>
    /// Claims every query and answers each with an empty Initial — except the target-existence query
    /// while armed, which it leaves unanswered (<see cref="Stalling"/>: no Initial, no completion, no
    /// error — the shape the fan-in's stall terminal exists for) or faults with an ordinary defect
    /// (<see cref="Faulting"/>).
    /// </summary>
    private sealed class StallingTargetProvider(string targetQuery) : IMeshQueryProvider
    {
        private int stalling;
        private int faulting;
        private int stalledReads;

        public string Name => nameof(StallingTargetProvider);

        public bool Stalling
        {
            get => Volatile.Read(ref stalling) == 1;
            set => Interlocked.Exchange(ref stalling, value ? 1 : 0);
        }

        public bool Faulting
        {
            get => Volatile.Read(ref faulting) == 1;
            set => Interlocked.Exchange(ref faulting, value ? 1 : 0);
        }

        /// <summary>How many target reads were left unanswered.</summary>
        public int StalledReads => Volatile.Read(ref stalledReads);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(
            MeshQueryRequest request, JsonSerializerOptions options) =>
            Observable.Defer(() =>
            {
                var isTargetRead = request.EffectiveQueries.Any(q =>
                    string.Equals(q.Trim(), targetQuery, StringComparison.Ordinal));
                if (isTargetRead && Faulting)
                    return Observable.Throw<QueryResultChange<T>>(
                        new InvalidOperationException(DefectMessage));
                if (!isTargetRead || !Stalling)
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
