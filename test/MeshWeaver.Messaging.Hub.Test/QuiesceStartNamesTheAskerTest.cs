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

    /// <summary>
    /// 🚨 <b>WHO is only half the question (#3510).</b> The sender identifies the recycler only
    /// when it is a DIFFERENT hub; the three automatic recyclers post to their OWN hub and render
    /// as one sentence — "a rebind or self-heal recycle" — which is the
    /// <see href="/Doc/Architecture/ControlsThatCannotFail">one word covering three states</see>
    /// shape. The poster has always known why. Now the request carries it.
    /// </summary>
    [HubFact]
    public async Task ADisposeRequestCarryingAReason_PrintsThatReason()
    {
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "asked-with-reason"), c => c)!;
        var asker = (MessageHub)Mesh.GetHostedHub(new Address("asker", "with-reason"), c => c)!;

        asker.Post(
            new DisposeRequest { Reason = "NodeType rebind: node 'X' is now typed 'A'" },
            o => o.WithTarget(victim.Address));

        var line = await QuiesceLineFor(victim);

        line.Should().Contain("why: ",
            "the line must have a WHY field at all — the whole residual of #3510 is that it did not");
        line.Should().Contain("NodeType rebind: node 'X' is now typed 'A'",
            "the poster's own sentence is the answer; a reader must not have to infer it from the "
            + "sender the way #3510 had to infer it from ordering");
        line.Should().NotContain(DisposeRequest.ReasonNotStated,
            "a stated reason must never be reported as unstated");
    }

    /// <summary>
    /// 🚨 The <c>NothingWasChecked</c> shape, one subsystem over: an unanswered question is a NAMED
    /// answer, never a blank. A caller that said nothing is reported as having said nothing —
    /// which is what tells the next reader to go and look at the poster, instead of reading an
    /// empty field as "there was nothing to report".
    ///
    /// <para>This is also the NEGATIVE control on the test above: a formatter that invented a
    /// plausible reason, or that silently dropped the field when there was none, would pass
    /// <see cref="ADisposeRequestCarryingAReason_PrintsThatReason"/> and fail here.</para>
    /// </summary>
    [HubFact]
    public async Task ADisposeRequestWithNoReason_SaysTheCallerDidNotStateOne()
    {
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "asked-no-reason"), c => c)!;
        var asker = (MessageHub)Mesh.GetHostedHub(new Address("asker", "no-reason"), c => c)!;

        asker.Post(new DisposeRequest(), o => o.WithTarget(victim.Address));

        var line = await QuiesceLineFor(victim);

        line.Should().Contain(DisposeRequest.ReasonNotStated,
            "an absent reason must be REPORTED as absent; a field that simply disappears reads to "
            + "the next person as 'there was nothing to report'");
        line.Should().Contain("asker/no-reason",
            "the half that IS known must still be printed — a missing reason does not cost the "
            + "sender");
    }

    /// <summary>
    /// 🚨 <b>#3510's exact shape, one level down — and the reading nothing could produce.</b>
    ///
    /// <para>The issue's trail: <i>"The Hosting root hub was disposed at 23:37:51Z while its own
    /// 145-file install was in flight. Its per-node children — the owners of
    /// <c>Hosting/*/_Activity/compile-state</c> … — went with it"</i>, and the writes those
    /// children owed acks for were stranded. The stranded writes were owed by the CHILDREN, so the
    /// child's own <c>[QUIESCE-START]</c> is where a reader lands — and a hosted hub is torn down
    /// by a plain <c>Dispose()</c> from <c>HostedHubsCollection</c>, so that line read
    /// <c>requested by a direct Dispose() (no routed DisposeRequest)</c>: the same sentence a
    /// <c>using</c> produces. The root recycle that actually took it was invisible from there.</para>
    ///
    /// <para>Now the child names the cascade AND the originating teardown, so one line answers
    /// both halves without a second log to correlate against.</para>
    /// </summary>
    [HubFact]
    public async Task AChildTornDownWithItsOwner_NamesTheCascadeAndTheOriginatingTeardown()
    {
        var root = (MessageHub)Mesh.GetHostedHub(new Address("root", "recycled-mid-install"), c => c)!;
        var child = (MessageHub)((IMessageHub)root)
            .GetHostedHub(new Address("child", "compile-state"), c => c)!;
        var installer = (MessageHub)Mesh.GetHostedHub(new Address("installer", "packages"), c => c)!;

        installer.Post(
            new DisposeRequest
            {
                Reason = "PackageInstaller.SettleRetypedRoot: recycling the root WHILE INSTALLING "
                         + "that package",
            },
            o => o.WithTarget(root.Address));

        var line = await QuiesceLineFor(child);

        line.Should().Contain("a cascade from its owner",
            "a hub that goes down because its OWNER does must say so — this is the reading #3510 "
            + "could not get from the child, which is where its stranded writes were owed");
        line.Should().Contain("root/recycled-mid-install",
            "and it must NAME the owner, so the reader can go to the right hub's line next");
        line.Should().Contain("PackageInstaller.SettleRetypedRoot",
            "the ORIGINATING teardown travels down the cascade — otherwise the child names its "
            + "parent and the reader still has to correlate two logs to learn that an install "
            + "recycled the root");
        line.Should().NotContain("no routed DisposeRequest",
            "a cascade is not a direct Dispose(), and reading it as one is exactly what cost "
            + "#3510 six occurrences");
    }

    /// <summary>
    /// 🚨 <b>The negative control, in the other direction.</b> A change that stamped EVERY disposal
    /// as a cascade would satisfy the test above while destroying the one reading #3510 already
    /// had — the absence of a routed request, which rules the message path out. An ordinary hub
    /// disposed on its own must still read as one.
    /// </summary>
    [HubFact]
    public async Task AHubDisposedOnItsOwn_IsNotReportedAsACascade()
    {
        var lonely = (MessageHub)Mesh.GetHostedHub(new Address("lonely", "no-owner-teardown"), c => c)!;

        lonely.Dispose();

        var line = await QuiesceLineFor(lonely);

        line.Should().NotContain("a cascade from its owner",
            "nothing tore this hub down but the caller — claiming an owner did would be a "
            + "confidently wrong attribution, which is worse than the blank it replaces");
        line.Should().Contain("no routed DisposeRequest",
            "the ABSENCE of a request must survive the change: it is what rules the message path "
            + "out, and it is the one deduction #3510 could already make");
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
