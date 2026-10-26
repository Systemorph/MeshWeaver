using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using Xunit;
using Defaults = Memex.Portal.ServiceDefaults.ServiceDefaults;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#7085 — <c>fail: Polly[3] Execution attempt. Source:
/// '-standard/(unnamed)/Standard-Retry' … Handled: 'True', Attempt: '3'</c> for an agent's web fetch
/// of a site that reset its TLS handshake. Polly reports the final exhausted retry at Error, the
/// fleet's log watcher turns every Error line into an incident, and so every remote site's failure
/// opened a ticket against the platform.
///
/// <para>The decision (policy <c>arbitrary-url-fetch-failure-is-warning</c>): a client that DECLARES
/// itself an <see cref="IArbitraryUrlFetcher"/> logs its give-up at Warning; every other client keeps
/// Error. This builds exactly what production builds
/// (<see cref="Defaults.AddHttpClientResilienceDefaults"/>) and reads the level off the Polly log line
/// itself — the only thing the log watcher sees.</para>
///
/// <para><b>Negative control:</b> an undeclared platform client failing the same way still logs its
/// final attempt at Error, and so does the default, unnamed client. Remove the severity provider from
/// <c>AddHttpClientResilienceDefaults</c> and the declared client's assertion fails with an Error
/// line.</para>
/// </summary>
public class AnArbitraryUrlFetcherGivingUpIsAWarningTest
{
    private const string Fetcher = "web-page-fetch";
    private const string Platform = "platform-service";

    [Fact]
    public void ADeclaredFetcher_LogsItsExhaustedRetryAtWarning()
    {
        var sink = new PollyLines();
        using var provider = Build(sink);

        SendAndFail(provider.GetRequiredService<IHttpClientFactory>().CreateClient(Fetcher));

        var lines = sink.For(Fetcher);
        Assert.Contains(lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Attempt: '3'", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Level >= LogLevel.Error);
    }

    [Fact]
    public void AnUndeclaredPlatformClient_StillLogsItsExhaustedRetryAtError()
    {
        var sink = new PollyLines();
        using var provider = Build(sink);
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        SendAndFail(factory.CreateClient(Platform));
        SendAndFail(factory.CreateClient());

        Assert.Contains(sink.For(Platform),
            l => l.Level == LogLevel.Error && l.Text.Contains("Attempt: '3'", StringComparison.Ordinal));
        Assert.Contains(sink.For(Defaults.UnnamedClient),
            l => l.Level == LogLevel.Error && l.Text.Contains("Attempt: '3'", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyADeclaredName_Qualifies()
    {
        using var provider = new ServiceCollection().DeclareArbitraryUrlFetcher(Fetcher).BuildServiceProvider();
        using var declared = Stamped(Fetcher);
        using var other = Stamped(Platform);
        using var unstamped = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/");

        Assert.True(Defaults.FetchesArbitraryUrls(declared, provider));
        Assert.False(Defaults.FetchesArbitraryUrls(other, provider));
        Assert.False(Defaults.FetchesArbitraryUrls(unstamped, provider));
        Assert.False(Defaults.FetchesArbitraryUrls(null, provider));
    }

    private static HttpRequestMessage Stamped(string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/");
        request.Options.Set(Defaults.ClientNameKey, name);
        return request;
    }

    private static Microsoft.Extensions.DependencyInjection.ServiceProvider Build(PollyLines sink)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
        Defaults.AddHttpClientResilienceDefaults(services);
        services.DeclareArbitraryUrlFetcher(Fetcher);
        // The test seams: no socket, every attempt is reset by the "remote side" — the #7085 shape,
        // which the retry predicate rightly treats as transient — and no backoff between attempts.
        services.ConfigureHttpClientDefaults(http =>
            http.ConfigurePrimaryHttpMessageHandler(() => new ResetsTheConnection()));
        services.PostConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });
        return services.BuildServiceProvider();
    }

    // Synchronous on purpose: HttpClient.Send drives the same handler chain without a Task in the test.
    private static void SendAndFail(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://remote.example.invalid/");
        Assert.Throws<HttpRequestException>(() => client.Send(request, CancellationToken.None));
    }

    private sealed class ResetsTheConnection : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The SSL connection could not be established, see inner exception.",
                new IOException("Unable to read data from the transport connection: Connection reset by peer."));

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(Send(request, cancellationToken));
    }

    private sealed record PollyLine(LogLevel Level, string Text);

    /// <summary>Collects every line logged under Polly's category, with its level — per test instance.</summary>
    private sealed class PollyLines : ILoggerProvider
    {
        private readonly ConcurrentQueue<PollyLine> lines = new();

        /// <summary>The lines of the pipeline instance a client named <paramref name="client"/> rides.</summary>
        public PollyLine[] For(string client) =>
            lines.Where(l => l.Text.Contains($"Source: '-standard/{client}/", StringComparison.Ordinal)).ToArray();

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, lines);

        public void Dispose() { }

        private sealed class Sink(string category, ConcurrentQueue<PollyLine> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category.StartsWith("Polly", StringComparison.Ordinal))
                    lines.Enqueue(new PollyLine(logLevel, formatter(state, exception)));
            }
        }
    }
}
