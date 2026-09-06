using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;                  // TestTimeouts.Convergence
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #3450, on a real monolith mesh: <c>IEmailSender.CanSendAsUser</c> had TWO answers for THREE
/// states of the world, and the send dialog rendered the missing one as a lie.
///
/// <para><b>What the user saw.</b> The Share ⇒ as email dialog probes "can I send as this person?"
/// when it OPENS. On <c>false</c> it renders <c>ui.sendDocument.connectFirst</c> — <i>"Your
/// Microsoft 365 mailbox is not connected yet"</i> — beside a Connect button. Both consumers wrapped
/// the probe in <c>.Catch(_ =&gt; Observable.Return(false))</c>, so a probe that TIMED OUT or FAULTED
/// arrived as the same <c>false</c> as a probe that completed and found no credential. A connected
/// user was told, in a sentence, that they had not connected — and nothing was logged, so the state
/// was not even greppable. This is #3433 one layer down, on the mail seam.</para>
///
/// <para><b>What the fix is.</b> <see cref="IEmailSender.ObserveSendAsCapability"/> answers with
/// three states, and the RENDERING RULE lives on the answer:
/// <see cref="EmailSendAsCapability.OffersConnect"/> is true for
/// <see cref="EmailSendAs.Unavailable"/> alone. A consumer cannot re-derive the collapse by writing
/// <c>!IsAvailable</c>, because that expression is not what the UI asks.</para>
///
/// <para><b>The positive control that must pass BOTH ways.</b>
/// <see cref="AnUndeterminedProbe_StillFoldsToFalse_SoTheSendPathAsksWhichMailbox"/>. The SEND path
/// is a binary decision — send as the person, or ask which mailbox — and folding an unknown to
/// <c>false</c> there is CORRECT: the user is asked rather than assumed. That half was never the
/// bug and must not move. If a "fix" makes it pass, the fix broke the send path.</para>
/// </summary>
public class EmailSendAsCapabilityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string User = "send-as-user-object-id";

    /// <summary>
    /// The sender this test's mesh resolves. One instance per test (the base builds a mesh per
    /// test), scripted before the probe runs — a real <see cref="IEmailSender"/> the container
    /// hands out, not a mock of a core interface.
    /// </summary>
    private readonly ScriptedEmailSender sender = new();

    /// <summary>Captures what the framework LOGGED, so "greppable" is asserted rather than assumed.</summary>
    private readonly CapturingLoggerProvider logs = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<IEmailSender>(sender)
                .AddLogging(l => l.AddProvider(logs)));

    // ── The three states, and what each one lets the dialog claim ─────────────────────────────────

    /// <summary>
    /// A connected user. The positive control for everything below: without it a "fix" that
    /// answered <see cref="EmailSendAs.Undetermined"/> to everything would pass the defect test and
    /// strand every genuinely connected user behind a "we could not check" panel.
    /// </summary>
    [Fact]
    public async Task AConnectedUser_IsAvailable_AndTheDialogClaimsNothingAboutConnecting()
    {
        sender.Answer = _ => Observable.Return(EmailSendAsCapability.Available);

        var capability = await Mesh.ObserveSendAsCapability(User)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"connected verdict: {capability}");
        capability.IsAvailable.Should().BeTrue();
        capability.OffersConnect.Should().BeFalse(
            "a connected user must never be shown a Connect panel");
    }

    /// <summary>
    /// A user who genuinely never connected. This is the ONE state in which
    /// <c>ui.sendDocument.connectFirst</c> is a true sentence, and the fix must keep offering it —
    /// otherwise it trades a false claim for a missing affordance.
    /// </summary>
    [Fact]
    public async Task AUserWhoNeverConnected_IsUnavailable_AndTheDialogOffersConnect()
    {
        sender.Answer = _ => Observable.Return(
            EmailSendAsCapability.Unavailable("no stored delegated credential for this user"));

        var capability = await Mesh.ObserveSendAsCapability(User)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"never-connected verdict: {capability}");
        capability.OffersConnect.Should().BeTrue(
            "a completed check with a negative answer is exactly when the Connect offer is truthful");
        capability.IsUndetermined.Should().BeFalse();
    }

    /// <summary>
    /// 🚨 THE DEFECT, pinned. The probe FAULTS — the shape both consumers swallowed with
    /// <c>.Catch(_ =&gt; Observable.Return(false))</c>.
    ///
    /// <para>Three assertions, deliberately separate. The first says what the failed probe IS
    /// (<see cref="EmailSendAs.Undetermined"/>); the second says what the dialog may therefore NOT
    /// claim; the third says the reason survived, so the state is greppable rather than merely
    /// modelled. On the old code the answer was <c>false</c> at both consumers, which
    /// <see cref="EmailSendAsCapability.OffersConnect"/> would have rendered as the Connect panel —
    /// so the second assertion is the one that names the user-visible defect.</para>
    /// </summary>
    [Fact]
    public async Task AProbeThatFaults_IsUndetermined_AndTheDialogDoesNotClaimNotConnected()
    {
        sender.Answer = _ => Observable.Throw<EmailSendAsCapability>(
            new TimeoutException("the credential read did not answer within its budget"));

        var capability = await Mesh.ObserveSendAsCapability(User)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"faulted-probe verdict: {capability}");

        capability.IsUndetermined.Should().BeTrue(
            "a probe that faulted produced NO answer about this user's mailbox");
        capability.OffersConnect.Should().BeFalse(
            "rendering 'your Microsoft 365 mailbox is not connected yet' here states as fact "
            + "something nothing was learned about — that is #3450, and the user it hits is a "
            + "CONNECTED one");
        capability.Diagnostic.Should().NotBeNullOrWhiteSpace(
            "an undetermined state with nothing to say is indistinguishable from the swallow it "
            + "replaces");
        capability.Diagnostic!.Should().Contain(nameof(TimeoutException));
    }

    /// <summary>
    /// The other way a probe fails to answer: it completes without emitting. A dropped mesh read
    /// looks exactly like this, and it must not be read as a negative either.
    /// </summary>
    [Fact]
    public async Task AProbeThatNeverAnswers_IsUndetermined()
    {
        sender.Answer = _ => Observable.Empty<EmailSendAsCapability>();

        var capability = await Mesh.ObserveSendAsCapability(User)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"silent-probe verdict: {capability}");
        capability.IsUndetermined.Should().BeTrue();
        capability.OffersConnect.Should().BeFalse();
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL — this must pass with the fix AND without it.
    ///
    /// <para>At SEND time the question really is binary: send as the person, or show
    /// <c>AskWhichMailbox</c>. Folding an unknown to <c>false</c> there is the RIGHT answer, and the
    /// code says so — <i>"The send does NOT proceed on an assumption either way."</i> So
    /// <c>hub.CanSendAsUser</c> still answers <c>false</c> for an undetermined probe, and the user
    /// is asked which mailbox instead of being assumed connected. A change that made this pass
    /// would have started sending as a person whose mailbox nobody could confirm.</para>
    /// </summary>
    [Theory]
    [InlineData("faulted")]
    [InlineData("silent")]
    [InlineData("unavailable")]
    public async Task AnUndeterminedProbe_StillFoldsToFalse_SoTheSendPathAsksWhichMailbox(string shape)
    {
        sender.Answer = shape switch
        {
            "faulted" => _ => Observable.Throw<EmailSendAsCapability>(new TimeoutException("no answer")),
            "silent" => _ => Observable.Empty<EmailSendAsCapability>(),
            _ => _ => Observable.Return(EmailSendAsCapability.Unavailable("no credential")),
        };

        var canSend = await Mesh.CanSendAsUser(User)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"send-time fold for '{shape}': {canSend}");
        canSend.Should().BeFalse(
            "the send path asks which mailbox unless it KNOWS it can send as the person — an "
            + "unknown must never become a silent send from someone else's identity");
    }

    /// <summary>The other half of the fold: a known-available probe still sends as the person.</summary>
    [Fact]
    public async Task AnAvailableProbe_FoldsToTrue_SoTheSendGoesOutAsThePerson()
    {
        sender.Answer = _ => Observable.Return(EmailSendAsCapability.Available);

        var canSend = await Mesh.CanSendAsUser(User)
            .Should().Within(TestTimeouts.Convergence).Emit();

        canSend.Should().BeTrue(
            "without this the fold could 'pass' the control above by answering false to everything");
    }

    /// <summary>
    /// 🚨 The probe stays LIVE — the hub extension must not truncate it to one answer.
    ///
    /// <para>The dialog <c>CombineLatest</c>s this stream into the form it renders, so it is a live
    /// data-bound view, and <c>.Take(1)</c> on one of those freezes the binding. It is also the
    /// mechanism the "Check again" affordance needs: a sender that re-probes has to be able to
    /// replace an <see cref="EmailSendAs.Undetermined"/> answer with a real one <i>in place</i>,
    /// without the dialog being torn down and rebuilt.</para>
    ///
    /// <para>Neither operator in the extension needs a single emission — <c>Catch</c> replaces only
    /// the tail after a fault, and <c>DefaultIfEmpty</c> fires only on an empty completion — so the
    /// truncation would buy nothing and cost the retry.</para>
    /// </summary>
    [Fact]
    public async Task TheProbeStaysLive_SoARetryCanReplaceAnUndeterminedAnswerInPlace()
    {
        sender.Answer = _ => Observable
            .Return(EmailSendAsCapability.Unknown("first check did not answer"))
            .Concat(Observable.Return(EmailSendAsCapability.Available));

        var answers = await Mesh.ObserveSendAsCapability(User)
            .Take(2).ToList()
            .Should().Within(TestTimeouts.Convergence).Emit();

        foreach (var answer in answers)
            Output.WriteLine($"live probe emitted: {answer}");

        answers.Should().HaveCount(2,
            "truncating the probe to one answer freezes the dialog's binding and makes 'Check "
            + "again' unable to replace the panel it is offered on");
        answers[0].IsUndetermined.Should().BeTrue();
        answers[1].IsAvailable.Should().BeTrue(
            "the second answer must reach the view, in place, without a rebuild");
    }

    // ── The fault is now greppable ───────────────────────────────────────────────────────────────

    /// <summary>
    /// #3433 added the Warning that names the user and the diagnostic; #3450's collapse had no log
    /// line at all, which is why nobody could tell how often it fired. Pinned here so the routing
    /// cannot silently become a swallow again: a fault must be REPORTED as well as modelled.
    /// </summary>
    [Fact]
    public async Task AFaultedProbe_IsLoggedAtWarning_NamingTheUser()
    {
        sender.Answer = _ => Observable.Throw<EmailSendAsCapability>(
            new InvalidOperationException("graph transport refused the credential read"));

        await Mesh.ObserveSendAsCapability(User)
            .Should().Within(TestTimeouts.Convergence).Emit();

        var warnings = logs.Entries(LogLevel.Warning);
        foreach (var line in warnings)
            Output.WriteLine($"warning: {line}");

        warnings.Should().Contain(line => line.Contains(User, StringComparison.Ordinal),
            "the fault must name the user it was asked about, or an operator cannot tell whose "
            + "dialog was affected");
        warnings.Should().Contain(
            line => line.Contains("graph transport refused the credential read", StringComparison.Ordinal),
            "the diagnostic is what makes the third state greppable — it was absent entirely before");
    }

    // ── Additive: an old two-state sender is unchanged ────────────────────────────────────────────

    /// <summary>
    /// 🚨 The compatibility proof, and the reason this change needs no cross-repo pair.
    ///
    /// <para><see cref="TwoStateSender"/> implements ONLY what <see cref="IEmailSender"/> required
    /// before this change — the shape of every sender in a repository that pins an older core
    /// (MeshWeaver.Plugins' <c>GraphEmailSender</c> and its capturing test doubles). It compiles
    /// untouched, and it answers exactly what it answered before: no third state appears from
    /// nowhere, and no answer changes.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOldTwoStateSender_KeepsItsExactAnswer(bool canSendAsUser)
    {
        IEmailSender legacy = new TwoStateSender(canSendAsUser);

        var capability = await legacy.ObserveSendAsCapability(User)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"two-state sender ({canSendAsUser}) verdict: {capability}");

        capability.IsAvailable.Should().Be(canSendAsUser);
        capability.OffersConnect.Should().Be(!canSendAsUser,
            "a sender that only speaks yes/no reports its negative as a completed negative — which "
            + "is what it has always meant, and all it can mean");
        capability.IsUndetermined.Should().BeFalse(
            "the new state must not be invented for a sender that cannot observe it");
    }

    /// <summary>
    /// A deployment with no sender registered at all cannot send as anybody, and that is a
    /// COMPLETED check with a negative answer — not an unknown. Pinned because the tempting reading
    /// ("could not resolve, so we do not know") would show every user on a mail-less deployment a
    /// permanent "we could not check" panel.
    /// </summary>
    [Fact]
    public void NoSenderRegistered_IsAnAnsweredNegative_NotAnUnknown()
    {
        var capability = EmailSendAsCapability.Unavailable("no sender");
        capability.IsUndetermined.Should().BeFalse();
        capability.OffersConnect.Should().BeTrue();
    }

    /// <summary>An unknown with nothing to say is refused at construction, not merely discouraged.</summary>
    [Fact]
    public void AnUnknownWithNoDiagnostic_IsRefused()
        => Assert.Throws<ArgumentException>(() => EmailSendAsCapability.Unknown("   "));

    // ── Senders ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A real <see cref="IEmailSender"/> whose three-state probe is scripted per test. It sends
    /// nothing — every test here is about the PROBE, and a sender that delivered would only add a
    /// mailbox this suite must never touch.
    /// </summary>
    private sealed class ScriptedEmailSender : IEmailSender
    {
        public Func<string, IObservable<EmailSendAsCapability>> Answer { get; set; } =
            _ => Observable.Return(EmailSendAsCapability.Available);

        public IObservable<EmailSendAsCapability> ObserveSendAsCapability(string userObjectId)
            => Answer(userObjectId);

        public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
            => Observable.Return(true);

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments)
            => Observable.Return(true);
    }

    /// <summary>
    /// A sender written against the PRE-#3450 contract: it knows only
    /// <see cref="IEmailSender.CanSendAsUser"/>. Deliberately does not mention the new member — that
    /// absence is the assertion.
    /// </summary>
    private sealed class TwoStateSender(bool canSendAsUser) : IEmailSender
    {
        public IObservable<bool> CanSendAsUser(string userObjectId)
            => Observable.Return(canSendAsUser);

        public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
            => Observable.Return(true);

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments)
            => Observable.Return(true);
    }

    /// <summary>Records formatted log lines per level — instance state, owned by the test.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Line)> entries = new();

        public IReadOnlyList<string> Entries(LogLevel level)
        {
            var matching = new List<string>();
            foreach (var (entryLevel, line) in entries)
                if (entryLevel == level)
                    matching.Add(line);
            return matching;
        }

        public ILogger CreateLogger(string categoryName) => new Sink(entries);

        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
