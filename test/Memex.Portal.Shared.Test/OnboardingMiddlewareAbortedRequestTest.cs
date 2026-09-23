using System;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #4859 — <see cref="OnboardingMiddleware"/> waits for its identity decision WITHOUT the request's
/// abort token (deliberately: the decision must not be abandoned half-way), so the request it resumes
/// may already be dead. On memex-cloud five such requests were handed to a JSON endpoint after
/// Kestrel's shutdown had aborted their connections and disposed its transport, and each response
/// write died on <c>ObjectDisposedException: 'MemoryPool'</c> with this middleware as the nearest
/// application frame. A dead request must not reach the endpoint, and an abort during host shutdown
/// must say so at Warning — the crash it replaces was the only trace a cut-short drain left.
/// </summary>
public class OnboardingMiddlewareAbortedRequestTest
{
    [Fact]
    public async Task ARequestAbortedWhileItsDecisionIsInFlight_IsNotHandedToTheEndpoint()
    {
        var endpointRan = false;
        var middleware = new OnboardingMiddleware(
            _ => { endpointRan = true; return Task.CompletedTask; },
            new CapturingLogger<OnboardingMiddleware>());

        using var aborted = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        context.Request.Path = "/api/version";

        await RunWithDecisionInFlight(middleware, context, aborted, abort: true);

        endpointRan.Should().BeFalse(
            because: "the connection was aborted before the identity decision arrived, so there is "
                     + "nobody to answer — and when the abort was Kestrel's shutdown, the endpoint's "
                     + "response write lands in a disposed memory pool (#4859)");
    }

    [Fact]
    public async Task ALiveRequestWhoseDecisionArrivesLate_IsStillHandedToTheEndpoint()
    {
        // The control for the test above: the SAME request, not aborted, must pass through — so the
        // refusal above is about the abort and nothing else.
        var endpointRan = false;
        var middleware = new OnboardingMiddleware(
            _ => { endpointRan = true; return Task.CompletedTask; },
            new CapturingLogger<OnboardingMiddleware>());

        using var notAborted = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = notAborted.Token };
        context.Request.Path = "/api/version";

        await RunWithDecisionInFlight(middleware, context, notAborted, abort: false);

        endpointRan.Should().BeTrue(because: "a request whose connection is alive must reach its endpoint");
    }

    [Fact]
    public async Task AnAbortWhileTheHostIsStopping_IsReportedAtWarning()
    {
        var logger = new CapturingLogger<OnboardingMiddleware>();
        var middleware = new OnboardingMiddleware(_ => Task.CompletedTask, logger);

        using var lifetime = new StoppingLifetime();
        lifetime.Stop();
        using var aborted = new CancellationTokenSource();
        var context = new DefaultHttpContext
        {
            RequestAborted = aborted.Token,
            RequestServices = new ServiceCollection()
                .AddSingleton<IHostApplicationLifetime>(lifetime)
                .BuildServiceProvider(),
        };
        context.Request.Path = "/api/version";

        await RunWithDecisionInFlight(middleware, context, aborted, abort: true);

        logger.Lines(LogLevel.Warning).Should().ContainSingle(
            line => line.Contains("ABORTED while the host is stopping") && line.Contains("/api/version"),
            because: "an abort during shutdown is how Kestrel ends a drain that ran out of budget, and "
                     + "not answering the request must not also erase the only trace of that (#4859)");
    }

    [Fact]
    public async Task AClientThatLeftOutsideShutdown_IsNotAWarning()
    {
        var logger = new CapturingLogger<OnboardingMiddleware>();
        var middleware = new OnboardingMiddleware(_ => Task.CompletedTask, logger);

        using var lifetime = new StoppingLifetime();
        using var aborted = new CancellationTokenSource();
        var context = new DefaultHttpContext
        {
            RequestAborted = aborted.Token,
            RequestServices = new ServiceCollection()
                .AddSingleton<IHostApplicationLifetime>(lifetime)
                .BuildServiceProvider(),
        };
        context.Request.Path = "/api/version";

        await RunWithDecisionInFlight(middleware, context, aborted, abort: true);

        logger.Lines(LogLevel.Warning).Should().BeEmpty(
            because: "a client disconnecting from a running portal is routine, not a cut-short drain");
        logger.Lines(LogLevel.Debug).Any(line => line.Contains("client aborted")).Should().BeTrue(
            because: "the routine case is still recorded, at Debug");
    }

    /// <summary>
    /// Runs the middleware with an identity decision that is still IN FLIGHT, aborts the connection
    /// (when asked) while it is pending, and only then lets the decision arrive — the #4859 race.
    /// </summary>
    private static async Task RunWithDecisionInFlight(
        OnboardingMiddleware middleware, HttpContext context, CancellationTokenSource connection, bool abort)
    {
        var decision = new Subject<OnboardingMiddleware.OnboardingDecision>();
        var running = middleware.Continue(context, decision);
        running.IsCompleted.Should().BeFalse(because: "the decision has not arrived yet, so the request must be parked");
        if (abort)
            connection.Cancel();
        decision.OnNext(OnboardingMiddleware.OnboardingDecision.PassThrough);
        decision.OnCompleted();
        await running;
    }

    /// <summary>A lifetime whose <see cref="ApplicationStopping"/> the test fires.</summary>
    private sealed class StoppingLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Stop() => stopping.Cancel();
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }
}
