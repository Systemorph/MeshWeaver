using System.Collections.Concurrent;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>The sender of a post is the hub that posts it</b> (#6553, Doc/Architecture/SenderIsThePostingHub).
///
/// <para>A hub guard that decides by sender — the stamped-field guard admits only writes from the
/// owning hub — is only as good as the sender. <see cref="PostOptions"/> is a record, so posting code
/// can write <c>o with { Sender = … }</c>; before the fix the delivery left with whatever address the
/// caller wrote, and in-process code could pass such a guard by naming the guarded hub's own
/// address. The posting hub now overwrites a foreign sender with its own address.</para>
///
/// <para>Both arms are read off the RECEIVER: the host replies with the sender it was handed, and
/// the reply arriving back at the real poster is itself the proof — a delivery that left under the
/// forged address would have been answered to that address, and the poster's wait would time out.</para>
/// </summary>
public class SenderIsThePostingHubTest : HubTestBase
{
    private readonly ForeignSenderCapture capture = new();

    public SenderIsThePostingHubTest(ITestOutputHelper output) : base(output)
    {
        // Registered after TestBase's ctor cleared the providers, so it sits beside the xUnit sink.
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(capture));
    }

    private record WhoSentMe : IRequest<SenderSeen>;

    private record SenderSeen(Address Sender);

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(WhoSentMe), typeof(SenderSeen))
            .WithHandler<WhoSentMe>((hub, request) =>
            {
                hub.Post(new SenderSeen(request.Sender), o => o.ResponseFor(request));
                return request.Processed();
            });

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(WhoSentMe), typeof(SenderSeen));

    private static Address Bare(Address address) => address with { Host = null };

    /// <summary>The control: an ordinary post is seen with the poster's address.</summary>
    [HubFact]
    public async Task AnOrdinaryPost_ReachesTheTargetUnderThePostersAddress()
    {
        var client = GetClient();
        var response = await client
            .Observe<SenderSeen>(new WhoSentMe(), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit("an ordinary request is answered",
                cancellationToken: TestContext.Current.CancellationToken);
        Bare(response.Message.Sender).Should().Be(Bare(client.Address));
    }

    /// <summary>
    /// The impersonation: the client names the TARGET's own address as sender (the shape that would
    /// pass an owner-only guard), or an unrelated hub's. The target must see the real poster.
    /// </summary>
    [Theory(Timeout = 120000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AForgedSender_IsReplacedByThePostingHubsAddress(bool claimTheTarget)
    {
        var client = GetClient();
        var host = CreateHostAddress();
        var forged = claimTheTarget ? host : new Address("forged", "someone-else");

        var response = await client
            .Observe<SenderSeen>(new WhoSentMe(), o => o.WithTarget(host) with { Sender = forged })
            .Should().Within(10.Seconds()).Emit(
                "the post leaves as the client, so the reply comes back to the client",
                cancellationToken: TestContext.Current.CancellationToken);

        Bare(response.Message.Sender).Should().Be(Bare(client.Address),
            "the target is handed the address of the hub that actually posted, never the one the caller wrote");
        Bare(response.Message.Sender).Should().NotBe(Bare(forged));
    }

    /// <summary>
    /// The report is keyed on STABLE data: a caller writing a fresh address on every post — the
    /// claimed sender is caller-controlled — gets ONE warning for that call site, never one per post.
    /// The control is the first post: it must be reported, so a capture that saw nothing fails here.
    /// </summary>
    [HubFact]
    public void AFreshForgedSenderOnEveryPost_IsReportedOncePerCallSite()
    {
        var client = GetClient();
        var host = CreateHostAddress();
        for (var i = 0; i < 50; i++)
            client.Post(new SenderSeen(client.Address), o => o.WithTarget(host) with { Sender = new Address("forged", $"n{i}") });

        var reports = capture.Lines.Where(l => l.Contains(nameof(SenderSeen))).ToArray();
        reports.Should().HaveCount(1, "fifty posts from one call site with fifty different claimed senders are one defect");
        reports[0].Should().Contain(nameof(AFreshForgedSenderOnEveryPost_IsReportedOncePerCallSite),
            "the report names the code that wrote the sender");
    }

    /// <summary>
    /// <c>Observe</c> wraps the caller's options delegate in a lambda declared on the hub, so a call
    /// site read off that delegate names the hub's own plumbing. The report must name the caller.
    /// </summary>
    [HubFact]
    public async Task AForgedSenderThroughObserve_NamesTheCallerNotTheHubsPlumbing()
    {
        var client = GetClient();
        var host = CreateHostAddress();
        await client
            .Observe<SenderSeen>(new WhoSentMe(), o => o.WithTarget(host) with { Sender = new Address("forged", "observe") })
            .Should().Within(10.Seconds()).Emit("the post leaves as the client",
                cancellationToken: TestContext.Current.CancellationToken);

        var report = capture.Lines.Single(l => l.Contains(nameof(WhoSentMe)));
        var firstFrame = report[(report.IndexOf("Call site:", StringComparison.Ordinal) + "Call site:".Length)..]
            .TrimStart().Split('\n', 2)[0];
        firstFrame.Should().NotContain("MeshWeaver.Messaging.MessageHub",
            "the first frame printed is the code that asked, not the hub's own Observe wrapper");
        firstFrame.Should().NotStartWith("at System.Reactive");
    }

    /// <summary>Collects the formatted [ForeignSender] warnings; instance state only.</summary>
    private sealed class ForeignSenderCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();
        internal string[] Lines => lines.ToArray();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(lines);
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var line = formatter(state, exception);
                if (line.StartsWith("[ForeignSender]", StringComparison.Ordinal))
                    sink.Enqueue(line);
            }
        }
    }
}
