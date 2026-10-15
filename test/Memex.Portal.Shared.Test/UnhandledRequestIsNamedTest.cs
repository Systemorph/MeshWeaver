#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// MeshWeaver#6790 — a portal request failed with an unhandled <see cref="TimeoutException"/> that
/// had no frames of its own, and the only line it left (<c>ExceptionHandlerMiddleware</c>'s
/// <i>"An unhandled exception has occurred while executing the request."</i>) named neither the
/// method nor the route, so the endpoint that produced it could not be identified.
///
/// <para>Two halves, each over a REAL ASP.NET host (TestServer) and, for the verb surface, the REAL
/// mesh — no doubles:</para>
/// <list type="number">
/// <item><b>The verb surface answers its deadline.</b> <c>MeshApiEndpoints.RunString</c> — the shared
/// body behind every <c>/api/mesh/*</c> verb — let a verb's expired <c>.Timeout(…)</c> escape as a
/// faulted <see cref="Task"/>, i.e. exactly the unhandled 500 of the incident. It now answers 504 with
/// a body naming the route. Its control: a fault that is NOT a deadline still reaches the exception
/// handler — the fix turns one designed outcome into a response, it does not swallow faults.</item>
/// <item><b>The unhandled-exception line names the request.</b>
/// <see cref="RequestNamingExceptionHandling.UseRequestNamingExceptionHandler"/> writes that line ONCE,
/// with the exception, the method and the route TEMPLATE — never the concrete path. Its control: the
/// framework's plain <c>UseExceptionHandler</c> on the same host writes the pathless line this issue
/// could not decide from.</item>
/// </list>
/// </summary>
public class UnhandledRequestIsNamedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ErrorPath = "/error";
    private const string MiddlewareCategory = "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware";

    [Fact]
    public async Task A_verb_whose_bounded_wait_expires_answers_504_naming_the_route()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartHost(logs, useRequestNaming: true, a =>
            a.MapPost("/api/mesh/test-deadline", (HttpContext http, CancellationToken ct) =>
                // The shape of the incident: an Rx deadline with no fallback, whose TimeoutException
                // is created, never thrown — so it carries no frames.
                MeshApiEndpoints.RunString(http, Mesh, ct,
                    _ => Observable.Never<string>().Timeout(TimeSpan.FromMilliseconds(100)))));

        var response = await Post(app, "/api/mesh/test-deadline");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout,
            "a verb's expired deadline is a designed outcome, answered like /render-area's — not an "
            + $"unhandled 500. Body: {body}");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("kind").GetString().Should().Be(MeshApiEndpoints.TimeoutKind);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("POST /api/mesh/test-deadline");

        logs.Records.Should().ContainSingle(r =>
                r.Level == LogLevel.Warning
                && r.Exception is TimeoutException
                && r.Message.Contains("POST /api/mesh/test-deadline"),
            "the expiry is logged once, at Warning, naming the method and route");
        logs.Records.Should().NotContain(r => r.Level >= LogLevel.Error,
            "nothing reached the exception handler");
    }

    [Fact]
    public async Task A_verb_fault_that_is_not_a_deadline_still_reaches_the_exception_handler()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartHost(logs, useRequestNaming: true, a =>
            a.MapPost("/api/mesh/test-defect", (HttpContext http, CancellationToken ct) =>
                MeshApiEndpoints.RunString(http, Mesh, ct,
                    _ => Observable.Throw<string>(new InvalidOperationException("a real defect")))));

        var response = await Post(app, "/api/mesh/test-defect");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            "only the deadline is a designed answer; any other fault is a defect and must stay one");
        logs.Records.Should().ContainSingle(r => r.Level == LogLevel.Error && r.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task The_unhandled_exception_line_names_method_and_route_template_once()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartHost(logs, useRequestNaming: true, MapThrowingRoute);

        var response = await Post(app, "/api/test/items/private-node-name");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var errors = logs.Records.Where(r => r.Level >= LogLevel.Error).ToList();
        errors.Should().ContainSingle("the exception is logged ONCE — the middleware's own pathless "
            + "line is replaced, not duplicated. Got: " + string.Join(" | ", errors.Select(e => $"{e.Category}: {e.Message}")));
        var line = errors[0];
        line.Category.Should().Be(RequestNamingExceptionHandling.LogCategory);
        line.Exception.Should().BeOfType<TimeoutException>();
        line.Message.Should().Contain("POST /api/test/items/{id}");
        line.Message.Should().NotContain("private-node-name",
            "the route TEMPLATE is logged, never the concrete path — it carries user data");
    }

    /// <summary>
    /// The control: the framework's plain handler on the SAME host and route writes the line #6790
    /// had — the exception and no request. If this ever names the route, the test above proves
    /// nothing about the extension.
    /// </summary>
    [Fact]
    public async Task Control_the_plain_exception_handler_line_does_not_name_the_request()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartHost(logs, useRequestNaming: false, MapThrowingRoute);

        var response = await Post(app, "/api/test/items/private-node-name");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var line = logs.Records.Should().ContainSingle(r => r.Level >= LogLevel.Error).Subject;
        line.Category.Should().Be(MiddlewareCategory);
        line.Message.Should().NotContain("/api/test/items");
    }

    private static void MapThrowingRoute(WebApplication app) =>
        app.MapPost("/api/test/items/{id}", (string id) =>
            Task.FromException<IResult>(new TimeoutException()));

    private async Task<WebApplication> StartHost(
        CapturingLoggerProvider logs, bool useRequestNaming, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<IMessageHub>(Mesh);

        var app = builder.Build();
        if (useRequestNaming)
            app.UseRequestNamingExceptionHandler(ErrorPath, createScopeForErrors: true);
        else
            app.UseExceptionHandler(ErrorPath, createScopeForErrors: true);
        app.Map(ErrorPath, () => Results.Problem(statusCode: StatusCodes.Status500InternalServerError));
        map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static Task<HttpResponseMessage> Post(WebApplication app, string route) =>
        app.GetTestClient().PostAsync(route, new StringContent(""), TestContext.Current.CancellationToken);

    private sealed record CapturedLog(LogLevel Level, string Category, string Message, Exception? Exception);

    /// <summary>Keeps every record the host's logger factory routes to it. Instance-owned — its
    /// lifetime is one test's host — never static state.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> records = new();

        public IReadOnlyList<CapturedLog> Records => records.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, records);

        public void Dispose() { }

        private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => sink.Enqueue(new CapturedLog(logLevel, category, formatter(state, exception), exception));
        }
    }
}
