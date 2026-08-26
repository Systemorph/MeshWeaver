using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Email;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b><c>Email:Enabled=true</c> with a sender that cannot deliver must NEVER produce a
/// <c>Sent</c> record</b> (#2023).
///
/// <para>The defect: once the Graph sender moved into the <c>MeshWeaver.Mail.MicrosoftGraph</c>
/// module, an install could have <c>Email:Enabled=true</c> and still resolve
/// <see cref="NoOpEmailSender"/> — whose contract was to report success without sending. Every
/// queued mail was then stamped <c>New → Sending → Sent</c> while nothing left the process: node
/// data saying delivered, no error, and no log (the send lines are Information under a Warning
/// default). Hours went into inboxes, junk folders and message traces because <c>Sent</c> was
/// trusted.</para>
///
/// <para>These cases pin the whole refusal, at the three places it has to hold: the sender itself,
/// the send state machine that writes the status, and the watcher that would have claimed the mail
/// in the first place. The last one is what keeps mail recoverable — mail left <c>New</c> goes out
/// by itself once the module lands, mail stamped <c>Sent</c> is indistinguishable from mail that
/// really was sent and can never be re-driven.</para>
/// </summary>
public class NoOpEmailSenderRefusalTest
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    private static EmailOptions Configured(bool enabled) => new()
    {
        Enabled = enabled,
        MailboxAddress = "no-reply@example.test",
        TenantId = "tenant",
        ClientId = "client",
        ClientSecret = "secret",
    };

    /// <summary>
    /// The one case the no-op is FOR: mail is switched off, nothing queues, and a caller's chain
    /// completes normally. Kept as the control — without it "refuses everything" would satisfy the
    /// test below while breaking every local dev run and test host.
    /// </summary>
    [Fact]
    public async Task Disabled_TheNoOpStillReportsSuccess_SoLocalDevAndTestsAreUnaffected()
    {
        IEmailSender sender = new NoOpEmailSender(Configured(enabled: false));

        Assert.False(sender.DeliversMail);
        Assert.True(await sender.SendEmail("x@example.test", "s", "<p>b</p>").FirstAsync().ToTask());
    }

    /// <summary>
    /// 🚨 The regression pin. Enabled + a sender that does not deliver is a MISCONFIGURATION, and
    /// the send must surface as a failure rather than emit <c>true</c> — a caller that records an
    /// outcome then records the truth.
    /// </summary>
    [Fact]
    public async Task Enabled_ButTheSenderCannotDeliver_TheSendFails_ItNeverReportsSuccess()
    {
        IEmailSender sender = new NoOpEmailSender(Configured(enabled: true));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendEmail("x@example.test", "s", "<p>b</p>").FirstAsync().ToTask());

        // The message must name what to provision. "Email is broken" sends an operator hunting;
        // the module name is the whole diagnosis.
        Assert.Contains(EmailDeliveryGuard.SenderModule, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Email:Enabled=true", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 End to end through the code that writes the status: the real
    /// <see cref="NoOpEmailSender"/> on an <c>Enabled=true</c> install, driven by the real
    /// <see cref="OutboundSendQueue"/>. The mail must end <c>Failed</c> — visible and
    /// re-queueable — and <c>Sent</c> must never appear in the status history at all. Asserting
    /// the FINAL status alone would be weaker: the defect wrote Sent, and a later correction to
    /// Failed would still have left a window in which the node claimed delivery.
    /// </summary>
    [Fact]
    public async Task Enabled_ButTheSenderCannotDeliver_QueuedMailEndsFailed_AndIsNeverStampedSent()
    {
        var sender = new NoOpEmailSender(Configured(enabled: true));
        var store = new ConcurrentDictionary<string, MeshWeaver.Mesh.Email>();
        var statusHistory = new List<EmailStatus>();

        using var queue = new OutboundSendQueue(
            readCurrent: path => Observable.Defer(() => Observable.Return(
                store.TryGetValue(path, out var mail) ? mail : null)),
            writeStatus: (node, current, status) => Observable.Defer(() =>
            {
                lock (statusHistory) statusHistory.Add(status);
                store[node.Path] = current with { Status = status };
                return Observable.Return(node);
            }),
            send: mail => sender.SendEmail(mail.To!, mail.Subject, mail.Body));

        var email = new MeshWeaver.Mesh.Email
        {
            Direction = EmailDirection.Outbound,
            Status = EmailStatus.New,
            To = "recipient@example.test",
            Subject = "s",
            Body = "b",
        };
        var node = new MeshNode("m1", "T") { NodeType = "Email", Name = "m1", Content = email };
        store[node.Path] = email;

        var processed = queue.Processed.Take(1).Timeout(TestBudget).FirstAsync().ToTask();
        queue.Enqueue(node, email);
        await processed;

        Assert.Equal(EmailStatus.Failed, store[node.Path].Status);
        lock (statusHistory)
            Assert.DoesNotContain(EmailStatus.Sent, statusHistory);
    }

    /// <summary>
    /// 🚨 The watcher must not even ARM on this configuration. This is the half that keeps the
    /// mail RECOVERABLE: a watcher that starts and declines per-item has already claimed
    /// (<c>New → Sending</c>) whatever it picked up, whereas one that never starts leaves every
    /// queued mail at <c>New</c>, so delivery resumes on its own once the module lands — no data
    /// repair, no re-queueing by hand.
    ///
    /// <para>Observed through the provider, because "did it arm" has no other outward sign:
    /// <c>BeginWatching</c>'s first act is <c>rootServices.CreateScope()</c>, which asks the
    /// provider for an <see cref="IServiceScopeFactory"/>. Zero requests after
    /// <c>ApplicationStarted</c> has fired means the watch was never established. Its own body is
    /// wrapped in a try/catch, so a "did it throw" assertion would prove nothing.</para>
    /// </summary>
    [Fact]
    public async Task Enabled_ButTheSenderCannotDeliver_TheOutboundWatcherDoesNotStart()
    {
        var options = Configured(enabled: true);
        var services = new ServiceCollection()
            .AddSingleton<IEmailSender>(new NoOpEmailSender(options))
            .BuildServiceProvider();
        var recording = new RecordingProvider(services);
        using var lifetime = new StartableLifetime();

        var watcher = new OutboundEmailSender(recording, lifetime, options);
        await watcher.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();

        Assert.Equal(0, recording.ScopeFactoryRequests);
        watcher.Dispose();
    }

    /// <summary>
    /// The control for the case above: a sender that DOES deliver must still arm the watcher.
    /// Without this the previous test is satisfied by a watcher that never starts at all, which is
    /// the failure mode that would silently stop all outbound mail on a correctly configured
    /// install.
    /// </summary>
    [Fact]
    public async Task Enabled_WithADeliveringSender_TheOutboundWatcherStarts()
    {
        var options = Configured(enabled: true);
        var services = new ServiceCollection()
            .AddSingleton<IEmailSender>(new DeliveringSender())
            .BuildServiceProvider();
        var recording = new RecordingProvider(services);
        using var lifetime = new StartableLifetime();

        var watcher = new OutboundEmailSender(recording, lifetime, options);
        await watcher.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();

        Assert.True(recording.ScopeFactoryRequests > 0,
            "a delivering sender must leave the outbound watch armed — otherwise the refusal above "
            + "would be satisfied by a watcher that never runs for anyone.");
        watcher.Dispose();
    }

    /// <summary>The guard's verdict table, stated once so every caller reads the same rule.</summary>
    [Theory]
    [InlineData(false, false, false)] // disabled + no-op: the intended configuration
    [InlineData(false, true, false)]  // disabled + a real sender: nothing to refuse
    [InlineData(true, true, false)]   // enabled + a real sender: the working install
    [InlineData(true, false, true)]   // 🚨 enabled + no-op: the refused configuration
    public void TheGuard_RefusesExactlyTheEnabledButUndeliverableCombination(
        bool enabled, bool delivers, bool refused)
    {
        IEmailSender sender = delivers
            ? new DeliveringSender()
            : new NoOpEmailSender(Configured(enabled));

        Assert.Equal(refused, EmailDeliveryGuard.RefusesDelivery(Configured(enabled), sender));
    }

    /// <summary>
    /// A sender resolving to nothing at all certainly cannot deliver. Pinned because the tempting
    /// reading — "could not resolve, assume it is fine" — is the skip-trapdoor the guard exists to
    /// remove.
    /// </summary>
    [Fact]
    public void TheGuard_TreatsAnAbsentSenderAsUndeliverable()
        => Assert.True(EmailDeliveryGuard.RefusesDelivery(Configured(enabled: true), sender: null));

    /// <summary>A stand-in for the module's Graph sender: it claims delivery, nothing more.</summary>
    private sealed class DeliveringSender : IEmailSender
    {
        public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
            => Observable.Return(true);

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments)
            => Observable.Return(true);
    }

    /// <summary>Counts scope-factory requests — the outward sign that the watch was established.</summary>
    private sealed class RecordingProvider(IServiceProvider inner) : IServiceProvider
    {
        private int scopeFactoryRequests;

        public int ScopeFactoryRequests => Volatile.Read(ref scopeFactoryRequests);

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceScopeFactory))
                Interlocked.Increment(ref scopeFactoryRequests);
            return inner.GetService(serviceType);
        }
    }

    /// <summary>
    /// A lifetime whose <see cref="IHostApplicationLifetime.ApplicationStarted"/> can be fired on
    /// demand — the real token the watcher registers its <c>BeginWatching</c> on.
    /// </summary>
    private sealed class StartableLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource started = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
        public void NotifyStarted() => started.Cancel();
        public void Dispose() => started.Dispose();
    }
}
