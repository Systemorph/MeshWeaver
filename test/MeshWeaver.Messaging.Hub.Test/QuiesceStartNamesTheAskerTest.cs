using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b><c>[QUIESCE-START]</c> must name who asked for the teardown (#3510).</b>
///
/// <para>#3510's bake wedge turned on one unknown the log could not answer. The Hosting root was
/// disposed at 23:37:51Z while its own 145-file install was in flight; its per-node children went
/// with it; the writes they owed acks for were stranded, so four creates never got a reply and the
/// install ran out a ten-minute bound. The issue's own words: <i>"Who disposed the root is not in
/// the log at this level"</i> — so the leading hypothesis, a NodeType rebind posting
/// <c>DisposeRequest</c> to the root, stayed a hypothesis through the whole investigation. Its
/// Expected section asks for this line directly.</para>
///
/// <para>🚨 <b>The <c>ShutdownRequest</c>'s own sender cannot answer it</b>, which is why nothing
/// was there to read: <c>Dispose()</c> posts that request to ITSELF, so its sender is always the
/// dying hub. The discriminating fact is one frame earlier — whether a <c>DisposeRequest</c>
/// arrived over the bus at all, and from where.</para>
///
/// <para>Three outcomes, because a reader has three real cases to tell apart and the automatic
/// recycles post to their own hub: a recycle this hub asked for, a teardown another hub asked for,
/// and no message at all.</para>
/// </summary>
public class QuiesceStartNamesTheAskerTest : HubTestBase
{
    private readonly QuiesceLogCapture _capture = new();

    public QuiesceStartNamesTheAskerTest(ITestOutputHelper output)
        : base(output)
    {
        // 🚨 The filter, not just the provider. TestBase binds log-level filters from
        // test/appsettings.json (`logging.AddConfiguration("Logging")`), which leaves MeshWeaver
        // above Information — so an Information line is dropped BEFORE any provider sees it, and a
        // capture alone observes nothing. The sibling DisposalDeadlockDiagnosticsTest works without
        // this only because the verdicts it watches for are Errors. Raising it HERE, in this test's
        // own DI, is the self-contained way: the src-tree appsettings.json is committed contract and
        // is never edited to make a test see a line.
        Services.AddLogging(l =>
        {
            l.Services.AddSingleton<ILoggerProvider>(_capture);
            l.AddFilter("MeshWeaver", LogLevel.Information);
        });
    }

    private async Task<string> QuiesceLineFor(MessageHub hub)
    {
        // TestTimeouts.Convergence, never a literal: it scales with MW_TEST_TIMEOUT_FACTOR on CI and
        // stays strictly below the [Fact(Timeout)] outer bound, so a wait that loses says WHAT did
        // not converge instead of dying as an anonymous xunit timeout. TestTimeoutLiteralRatchetGuard
        // holds the hand-written count to a number that only goes down.
        await hub.DisposalCompleted.FirstOrDefaultAsync().Timeout(TestTimeouts.Convergence).Await();
        var address = hub.Address.ToString();
        var line = _capture.Entries.FirstOrDefault(e => e.Contains(address, StringComparison.Ordinal));
        line.Should().NotBeNull(
            $"PRECONDITION: a [QUIESCE-START] line must have been logged for {address} — without one "
            + "there is nothing for this test to assert about, and it would pass vacuously");
        return line!;
    }

    [HubFact]
    public async Task ARoutedDisposeRequestFromAnotherHub_NamesThatSender()
    {
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "asked-by-other"), c => c)!;
        var asker = (MessageHub)Mesh.GetHostedHub(new Address("asker", "installer"), c => c)!;

        asker.Post(new DisposeRequest(), o => o.WithTarget(victim.Address));

        var line = await QuiesceLineFor(victim);

        line.Should().Contain("requested by", "the line must answer the question at all");
        line.Should().Contain("asker/installer",
            "this is the shape that matters most — PackageInstaller posting a DisposeRequest to a "
            + "package ROOT while that package installs is #3510's wedge, and the sender is the one "
            + "fact that identifies it");
        line.Should().NotContain("no routed DisposeRequest",
            "a teardown that came over the bus must not read as a direct Dispose()");
    }

    /// <summary>
    /// 🚨 The self-posted case is called out rather than printed as an address. The automatic
    /// recycles (<c>NodeTypeRebindWatcher</c>, <c>WithOverlaySelfHeal</c>) post to their OWN hub, so
    /// a bare sender would render "Hosting: requested by Hosting" — true, useless, and easy to
    /// misread as a routing oddity. This is #3510's LEADING hypothesis, so it is the reading that
    /// must not be ambiguous.
    /// </summary>
    [HubFact]
    public async Task ASelfPostedDisposeRequest_SaysSoRatherThanNamingTheHubTwice()
    {
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "asked-by-self"), c => c)!;

        victim.Post(new DisposeRequest(), o => o.WithTarget(victim.Address));

        var line = await QuiesceLineFor(victim);

        line.Should().Contain("itself",
            "a rebind/self-heal recycle must be readable as one, not as a hub naming itself");
        line.Should().Contain("self-posted DisposeRequest");
        line.Should().NotContain("no routed DisposeRequest",
            "it DID come over the bus — that is exactly what distinguishes a recycle from a "
            + "teardown by an owner");
    }

    /// <summary>
    /// The negative case, and it carries real information: an absent DisposeRequest RULES OUT the
    /// message path, which is what #3510 needed and could not get. Host teardown, an owner
    /// disposing its children and a <c>using</c> all land here.
    /// </summary>
    [HubFact]
    public async Task ADirectDispose_SaysNoRoutedRequestBroughtItDown()
    {
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "asked-by-nobody"), c => c)!;

        victim.Dispose();

        var line = await QuiesceLineFor(victim);

        line.Should().Contain("no routed DisposeRequest",
            "the ABSENCE of a request is the answer here — it rules the message path out, which is "
            + "the deduction #3510 could not make");
        line.Should().NotContain("itself", "nothing asked; there is no requester to name");
    }

    private sealed class QuiesceLogCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capturing(Entries);
        public void Dispose() { }

        private sealed class Capturing(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            // Information: [QUIESCE-START] is an Information line, unlike the Error-level
            // deadlock verdicts the sibling capture watches for.
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Information)
                    return;
                var message = formatter(state, exception);
                if (message.Contains("[QUIESCE-START]", StringComparison.Ordinal))
                    sink.Enqueue(message);
            }
        }
    }
}
