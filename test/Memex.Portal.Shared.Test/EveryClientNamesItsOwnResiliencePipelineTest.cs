using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
// The namespace and the class share a name, so the class needs an alias to be nameable.
using Defaults = Memex.Portal.ServiceDefaults.ServiceDefaults;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b><c>Source: '-standard//…'</c> NAMED NOBODY</b> — Systemorph/MeshWeaver#4528.
///
/// <para><c>ConfigureHttpClientDefaults</c> hands out a builder with NO name, and
/// <c>AddStandardResilienceHandler</c> names its pipeline <c>{builder.Name}-standard</c>. So the
/// defaults pipeline was called <c>-standard</c> for EVERY client that does not re-register
/// itself — named or not — and its instance slot was empty. Every Polly line read
/// <c>-standard//Standard-AttemptTimeout</c>, which was taken for "some caller resolves an unnamed
/// client". Measured on memex-cloud 2026-09-24, every stack under those timeouts ended in
/// <c>SelfUpdateHandover.Post</c> — the NAMED <c>self-update-handover</c> client posting to the
/// control instance's inbox. And one (name, instance) key meant ONE pipeline, so one circuit breaker
/// was shared by every such client and every host they call.</para>
///
/// <para>This builds exactly what production builds
/// (<see cref="Defaults.AddHttpClientResilienceDefaults"/>, the method <c>AddServiceDefaults</c>
/// calls) and reads the pipeline identity off the Polly log line itself — the only place the
/// fleet's log watcher ever sees it. Negative control: drop the <c>SelectPipelineBy</c> call and the
/// three default-riding assertions fail with <c>-standard//…</c>.</para>
/// </summary>
public class EveryClientNamesItsOwnResiliencePipelineTest
{
    private const string Handover = "self-update-handover";
    private const string Report = "deployment-report";

    [Fact]
    public void EachNamedClient_IsItsOwnPipelineInstance_AndSaysSoInEveryPollyLine()
    {
        var sink = new PollyLines();
        using var provider = Build(sink);
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        Send(factory.CreateClient(Handover));
        Send(factory.CreateClient(Report));
        Send(factory.CreateClient());

        Assert.Contains(sink.Lines, l => l.Contains($"Source: '-standard/{Handover}/Standard-", StringComparison.Ordinal));
        Assert.Contains(sink.Lines, l => l.Contains($"Source: '-standard/{Report}/Standard-", StringComparison.Ordinal));
        Assert.Contains(sink.Lines, l => l.Contains($"Source: '-standard/{Defaults.UnnamedClient}/Standard-", StringComparison.Ordinal));

        // The form that named nobody must be gone entirely — not merely outnumbered.
        Assert.DoesNotContain(sink.Lines, l => l.Contains("Source: '-standard//", StringComparison.Ordinal));
    }

    /// <summary>
    /// A client re-registered with its own budget keeps its OWN pipeline (#1133/#1137) — the stamp
    /// the defaults add must not leak it back onto the shared one.
    /// </summary>
    [Fact]
    public void AReRegisteredClient_KeepsItsOwnNamedPipeline()
    {
        var sink = new PollyLines();
        using var provider = Build(sink);

        Send(provider.GetRequiredService<IHttpClientFactory>().CreateClient("plugin-registry"));

        Assert.Contains(sink.Lines, l => l.Contains("Source: 'plugin-registry-standard/", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Lines, l => l.Contains("Source: '-standard/", StringComparison.Ordinal));
    }

    /// <summary>The selector is pure: a request that never passed the stamp is the unnamed client's.</summary>
    [Fact]
    public void ARequestWithoutAStamp_BelongsToTheUnnamedInstance()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/");
        Assert.Equal(Defaults.UnnamedClient, Defaults.ClientNameOf(request));

        request.Options.Set(Defaults.ClientNameKey, Handover);
        Assert.Equal(Handover, Defaults.ClientNameOf(request));
    }

    private static ServiceProvider Build(PollyLines sink)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(sink));
        Defaults.AddHttpClientResilienceDefaults(services);
        // The one test seam: no socket. Every client answers 200 from memory.
        services.ConfigureHttpClientDefaults(http =>
            http.ConfigurePrimaryHttpMessageHandler(() => new AnswersOk()));
        return services.BuildServiceProvider();
    }

    // Synchronous on purpose: HttpClient.Send drives the same handler chain (the stamp and the
    // resilience handler both implement Send) without any Task in the test.
    private static void Send(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/");
        using var response = client.Send(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class AnswersOk : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { RequestMessage = request };

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(Send(request, cancellationToken));
    }

    /// <summary>Collects every formatted line logged under Polly's category — per test instance.</summary>
    private sealed class PollyLines : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();

        public string[] Lines => lines.ToArray();

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, lines);

        public void Dispose() { }

        private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category.StartsWith("Polly", StringComparison.Ordinal))
                    lines.Enqueue(formatter(state, exception));
            }
        }
    }
}
