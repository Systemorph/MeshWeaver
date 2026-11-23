using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// 🚨 <b>A collection-config read is answered ONCE — and what answered it must not leave a second
/// reader behind that NACKs the already-answered request when the owner is disposed.</b>
///
/// <para>Measured on memex and memex-cloud, 2026-11-20…23: every daily content-index backfill is
/// followed ten minutes later by hundreds of
/// <c>GetDataRequest(collection/attachments|content) at '…': the owning hub was disposed while this
/// read was still outstanding</c> warnings, in bursts at second :24 of consecutive minutes — the
/// moment the read-stream cache's per-minute idle sweep retires what the backfill touched and the
/// node hubs go. Every one of those reads HAD been answered: the indexing worked. The answer came
/// from the content collections' dedicated handler; the generic <c>GetDataRequest</c> handler ran
/// as well, opened a workspace stream for the same reference and kept it registered for the hub's
/// disposal, so each read leaked one zombie that "NACKed" an already-answered request when the hub
/// went (three or nine per hub: one per read).</para>
/// </summary>
public class AnsweredCollectionConfigReadIsNotNackedOnDisposeTest : HubTestBase, IDisposable
{
    private readonly string path = Path.Combine(
        AppContext.BaseDirectory, "Files", "ConfigRead", Guid.NewGuid().ToString("N"));

    private readonly CapturedWarnings warnings = new();

    public AnsweredCollectionConfigReadIsNotNackedOnDisposeTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<ILoggerProvider>(warnings);
    }

    /// <summary>Removes the per-run directory the collection was rooted at.</summary>
    void IDisposable.Dispose()
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { /* best effort: the output tree is disposable */ }
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddContentCollections()
            .AddFileSystemContentCollection("attachments", _ => path);

    [Fact(Timeout = 60_000)]
    public async Task ReadsThatWereAnswered_LeaveNothingToNackWhenTheOwnerIsDisposed()
    {
        Directory.CreateDirectory(path);
        var host = GetHost();
        var client = GetClient();

        const int reads = 3;
        for (var i = 0; i < reads; i++)
        {
            var answer = await client
                .Observe(new GetDataRequest(new ContentCollectionReference(["attachments"])),
                    o => o.WithTarget(CreateHostAddress()))
                .Take(1)
                .Timeout(TestTimeouts.Convergence)
                .FirstAsync().Await(TestContext.Current.CancellationToken);
            answer.Message.Should().BeOfType<GetDataResponse>("precondition: the read is answered");
        }

        host.Dispose();
        await host.DisposalCompleted.ObserveCompletion(
            ex => Output.WriteLine($"late disposal fault: {ex}"),
            TestContext.Current.CancellationToken);

        warnings.Messages.Where(m => m.Contains("was disposed while this read was still outstanding"))
            .Should().BeEmpty(
                "an answered read has nothing outstanding — a registration that survives the answer "
                + "NACKs a request nobody is waiting on, once per read, when the owner goes");
    }

    private sealed class CapturedWarnings : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                Messages.Enqueue(formatter(state, exception));
        }
    }
}
