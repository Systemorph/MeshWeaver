#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Reactive.Assertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#4963 — the plugin registry ANSWERS or REFUSES; it never holds a consumer's request
/// open with nothing written. Production, 2026-10-07: consumers in two namespaces got <c>0 bytes</c>
/// for 120 s from the registry while its replicas logged no line for those requests — because each
/// live read on the route was bounded and the REQUEST was not, so a stage with no bound of its own
/// held it for as long as it held.
///
/// <para>The repro is the production shape at the route: the REAL mesh, the real registration and
/// install path, the bundle routes on a TestServer, and ONE stage of the index that never answers
/// (the pushed-artifacts record — a host-supplied seam, so the stall needs no fault injection inside
/// the platform). Negative control: the same request with the deadline pushed out of reach is held
/// with nothing written — the defect, reproduced — so the green above is the fix's and not the
/// harness's.</para>
/// </summary>
public class RegistryAnswersOrRefusesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "AnswerDeadlinePkg";
    private const string Source = "Plugins";
    private const string Instance = "answer-deadline-consumer";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    // ── the operator, on its own ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAnswerThatNeverComes_IsA503WithRetryAfter_NamingTheStageItWaitedOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = new DefaultHttpContext();
        var stages = RegistryAnswerDeadline.Stages(http);
        var log = new CapturingLogger();

        var result = await Observable.Return(0)
            .InStage(stages, "instance-key authentication")
            .SelectMany(_ => Observable.Never<IResult>().InStage(stages, "module activation list (module records on the share)"))
            .AnsweredWithin(http, TimeSpan.FromMilliseconds(300), 30, log)
            .Should().Within(TestTimeouts.Quick)
            .Emit("a route that cannot answer must refuse within its budget", cancellationToken: ct);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        Assert.Equal("30", http.Response.Headers.RetryAfter.ToString());
        var line = Assert.Single(log.Lines);
        Assert.Contains("produced no answer within", line, StringComparison.Ordinal);
        Assert.Contains("Still waiting on: module activation list (module records on the share)", line, StringComparison.Ordinal);
        Assert.Contains("Finished: instance-key authentication", line, StringComparison.Ordinal);
        Assert.Equal(["module activation list (module records on the share)"], stages.Pending());
    }

    [Fact]
    public async Task AnAnswerInsideTheBudget_PassesThroughUntouched_AndLogsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = new DefaultHttpContext();
        var log = new CapturingLogger();
        var answer = Results.Ok("served");

        var result = await Observable.Return(answer)
            .InStage(RegistryAnswerDeadline.Stages(http), "installed packages (mesh query)")
            .AnsweredWithin(http, TimeSpan.FromSeconds(5), 30, log)
            .Should().Within(TestTimeouts.Quick)
            .Emit("an answer inside the budget is the answer", cancellationToken: ct);

        Assert.Same(answer, result);
        Assert.Empty(log.Lines);
        Assert.True(string.IsNullOrEmpty(http.Response.Headers.RetryAfter.ToString()));
    }

    /// <summary>The budget runs from the request's ARRIVAL (the auth filter started the ledger), so a
    /// request that spent its budget before the handler ran is refused at once, not given a fresh one.</summary>
    [Fact]
    public async Task TheBudgetCountsFromArrival_NotFromTheHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = new DefaultHttpContext();
        var stages = RegistryAnswerDeadline.Stages(http);
        var spent = stages.Enter("instance-key authentication");
        await Observable.Timer(TimeSpan.FromMilliseconds(400)).Should().Within(TestTimeouts.Quick)
            .Emit("the arrival-side clock must run", cancellationToken: ct);
        spent.Finish();

        var result = await Observable.Never<IResult>()
            .AnsweredWithin(http, TimeSpan.FromMilliseconds(300), 30, logger: null)
            .Should().Within(TimeSpan.FromMilliseconds(250))
            .Emit("the budget was already spent before the handler ran", cancellationToken: ct);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    /// <summary>Negative control for the operator: the same never-answering stage WITHOUT the
    /// deadline emits nothing — the shape every registry route had.</summary>
    [Fact]
    public async Task WithoutTheDeadline_TheSameStageHoldsTheAnswerForever()
    {
        var http = new DefaultHttpContext();
        await Observable.Never<IResult>()
            .InStage(RegistryAnswerDeadline.Stages(http), "module activation list (module records on the share)")
            .Should().NotEmit(within: TimeSpan.FromMilliseconds(600),
                because: "without a deadline nothing ends a stage that never answers",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    // ── the route, end to end ───────────────────────────────────────────────────────────────────

    [Fact(Timeout = 300_000)]
    public async Task TheIndex_WithAStageThatNeverAnswers_RefusesWithinItsBudget_AndNamesTheStage()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallPackage(ct);
        var key = await RegisterInstance(ct);
        var app = await StartBundleHost(budgetSeconds: 2, ct);
        await using var _ = app;

        var clock = Stopwatch.StartNew();
        // The client's patience is the fixture's, not a guess: the refusal must arrive at the 2 s
        // budget, long before it (asserted below), so its exact value only bounds a broken run.
        var result = await Index(app, key, TestTimeouts.Quick, ct);
        using var response = result.Response;
        var elapsed = clock.Elapsed;

        Assert.False(result.ClientCut, "the registry must answer before the client's patience ends");
        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response!.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(PluginBundleEndpoints.TransientRetryAfterSeconds), response.Headers.RetryAfter?.Delta);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var waitingOn = body.RootElement.GetProperty("waitingOn").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["pushed artifacts record"], waitingOn);
        Assert.True(elapsed < TimeSpan.FromSeconds(15),
            $"the refusal must come at the 2 s budget, not at the client's patience (took {elapsed.TotalSeconds:F1} s)");
    }

    /// <summary>Negative control for the route: the deadline out of reach, the same stalled stage —
    /// the request is held with nothing written, exactly what the consumers measured.</summary>
    [Fact(Timeout = 300_000)]
    public async Task TheIndex_WithTheDeadlineOutOfReach_IsHeldWithNothingWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallPackage(ct);
        var key = await RegisterInstance(ct);
        var app = await StartBundleHost(budgetSeconds: 3600, ct);
        await using var _ = app;

        var patience = TimeSpan.FromSeconds(5);
        var result = await Index(app, key, patience, ct);
        using var response = result.Response;

        // The client gave up first: the route wrote no status line in 5 s. On the TestServer the
        // client's cancellation IS the request's RequestAborted, so the abandoned request may still
        // complete AFTER the cut — the route's wait faults with the aborted token, the host records
        // that as 499 Client Closed Request, and the TestServer can hand it back to a client that
        // has already cancelled (it raced the abort on CI: run 37652733264). A 499 is therefore
        // the same reading as no response — nothing the ROUTE answered, only the hang-up recorded —
        // and both may only come once the client's patience has run out.
        Assert.True(response is null || response.StatusCode == (HttpStatusCode)StatusCodes.Status499ClientClosedRequest,
            $"the held request must not be answered by the route; got {(int?)response?.StatusCode}");
        Assert.True(result.ClientCut,
            "nothing may come back before the client's own cancellation fires");
    }

    private Task<string> RegisterInstance(CancellationToken ct) =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection([new KeyValuePair<string, string?>(
                        $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:0", $"{Source}/*")])
                    .Build())
            .Register("deadline-owner", "Deadline Owner", "owner@test.com", Instance, Instance)
            .Select(r => r.RawKey)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(ct);

    private Task<InstallResult> InstallPackage(CancellationToken ct) =>
        PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = Package,
                    Name = Package,
                    Kind = PackageKind.Content,
                    TargetPartition = Package,
                    SourceFolder = Package,
                    Version = "1.0.0",
                    ReleasedVersion = "1.0.0",
                    Source = Source,
                },
                [new PackageFile($"{Package}/Doc.md", $"# {Package}")],
                "HEAD")
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(120))
            .Await(ct);

    private async Task<WebApplication> StartBundleHost(int budgetSeconds, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(
            RegistryAnswerDeadline.BudgetSecondsConfigKey, budgetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>()));
        // The stalled stage: a pushed-artifacts record that never answers.
        builder.Services.AddSingleton<IPublicationArtifacts>(new NeverAnsweringArtifacts());
        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync(ct);
        return app;
    }

    /// <summary>The index response, or null when the client's own <paramref name="patience"/> ran out
    /// with no status line received, together with whether that actual cancellation fired.</summary>
    private static async Task<(HttpResponseMessage? Response, bool ClientCut)> Index(
        WebApplication app, string key, TimeSpan patience, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PluginBundleEndpoints.RoutePrefix + "/index.json");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        using var patienceCut = new CancellationTokenSource();
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(ct, patienceCut.Token);
        patienceCut.CancelAfter(patience);
        try
        {
            var response = await app.GetTestClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cut.Token);
            return (response, patienceCut.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, patienceCut.IsCancellationRequested);
        }
    }

    private sealed class NeverAnsweringArtifacts : IPublicationArtifacts
    {
        public IObservable<IReadOnlyList<PublicationArtifact>> Read() => Observable.Never<IReadOnlyList<PublicationArtifact>>();
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> lines = new();
        public IReadOnlyList<string> Lines => lines.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                lines.Enqueue(formatter(state, exception));
        }
    }
}
