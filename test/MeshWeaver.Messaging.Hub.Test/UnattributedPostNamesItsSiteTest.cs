using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The never-null guard refuses a post that carries no identity and logs the SENDING hub. On the
/// mesh's shared seams (<c>portal/reads-{meshId}</c>) that hub is every root-hub holder in the
/// process, so the refusal alone cannot say which caller lost the identity — production logged 47
/// such refusals in six hours and the sender was never established (Systemorph/MeshWeaver#5227).
/// The guard runs synchronously inside <c>Post</c>, so it can name the call chain that posted; this
/// pins that it does, on a companion line that leaves the Error's text (and the incident
/// fingerprint that folds on it) untouched.
/// </summary>
public class UnattributedPostNamesItsSiteTest : HubTestBase
{
    private readonly PostSiteCapture _capture = new();

    public UnattributedPostNamesItsSiteTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(_capture));
    }

    private sealed record Probe : IRequest<ProbeResponse>;
    private sealed record ProbeResponse;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(Probe), typeof(ProbeResponse))
            .WithHandler<Probe>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            });

    [Fact]
    public async Task A_refused_post_names_the_method_that_issued_it()
    {
        var client = GetClient(c => c.WithPostingIdentity(PostingIdentity.User));
        GetHost();

        var refused = new AsyncSubject<Exception>();
        using var subscription = IssueTheUnattributedProbe(client, refused);

        var error = await refused.Should().Within(10.Seconds())
            .Emit("a post with no identity must be refused, not delivered");
        error.Message.Should().Contain("AccessContext must never be null",
            "the refusal itself is unchanged");

        var errorLine = _capture.Entries.Should().ContainSingle(
            e => e.Category == "MeshWeaver.AccessContext" && e.Level == LogLevel.Error,
            "the guard's Error line is the tripwire and the incident fingerprint").Subject;
        errorLine.Message.Should().NotContain(nameof(IssueTheUnattributedProbe),
            "the post site goes on its OWN line, so the Error's text — and the fingerprint the "
            + "tracker folds on — does not change with every caller");

        var siteLine = _capture.Entries.Should().ContainSingle(
            e => e.Category == MessageHubConfiguration.PostSiteLogCategory,
            "a refused post must say where it came from — the sending hub alone does not").Subject;
        siteLine.Level.Should().Be(LogLevel.Warning);
        siteLine.Message.Should().Contain($"{nameof(UnattributedPostNamesItsSiteTest)}.{nameof(IssueTheUnattributedProbe)}",
            "the call chain that posted is the one fact that identifies the sender (#5227)");
        siteLine.Message.Should().Contain("target=" + CreateHostAddress(),
            "the companion line joins to the Error line by hub, message and target");
    }

    /// <summary>
    /// The sender under test: a plain, named method that subscribes a request with no ambient
    /// identity — the shape of a root-hub holder reading from an Rx callback.
    /// </summary>
    private IDisposable IssueTheUnattributedProbe(IMessageHub client, AsyncSubject<Exception> refused)
        => client.Observe(new Probe(), o => o.WithTarget(CreateHostAddress()))
            .Subscribe(
                _ => { },
                ex =>
                {
                    refused.OnNext(ex);
                    refused.OnCompleted();
                });

    private sealed record Entry(string Category, LogLevel Level, string Message);

    private sealed class PostSiteCapture : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, Entries);
        public void Dispose() { }

        private sealed class Capturing(string category, ConcurrentQueue<Entry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) =>
                category.StartsWith("MeshWeaver.AccessContext", StringComparison.Ordinal)
                && logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;
                sink.Enqueue(new Entry(category, logLevel, formatter(state, exception)));
            }
        }
    }
}
