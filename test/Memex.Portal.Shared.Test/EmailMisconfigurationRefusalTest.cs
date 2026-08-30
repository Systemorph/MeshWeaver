using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Email;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A half-configured OPTIONAL integration must not be able to stop the portal from
/// starting</b> (#2510).
///
/// <para><b>The defect.</b> <c>Email:Enabled=true</c> with <c>Email:TenantId</c> unset crashed the
/// host outright: <c>Hosting failed to start … An exception was thrown while activating
/// GraphEmailSender … ArgumentException: Invalid tenant id provided</c>, and the pod never became
/// ready. Nothing about mail should be able to do that — the blast radius of an unconfigured mail
/// sender is mail.</para>
///
/// <para><b>What actually pulled the trigger</b> is the interesting part, and it is why this file
/// sits next to <see cref="NoOpEmailSenderRefusalTest"/>: it was <c>EmailDeliveryGuard</c>, the
/// guard added for #2023 to stop mail being falsely stamped <c>Sent</c>. It ran in
/// <c>IHostedService.StartAsync</c> — where a throw aborts the HOST, not a feature — and its first
/// act was to ask the CONTAINER for an <see cref="IEmailSender"/>. Resolving it ACTIVATES the
/// module's Graph sender, which builds an Azure <c>ClientSecretCredential</c> in its constructor,
/// which validates the tenant id eagerly. So the guard against silent data loss became a
/// deterministic startup crash: a guard that can fail worse than the thing it guards.</para>
///
/// <para><b>The fix, and what these cases pin.</b> The guard now reaches its verdict from the
/// CONFIGURATION first — inert data that cannot throw and cannot activate anything — and only a
/// completely-configured install goes on to ask the container. So both halves have to hold, and
/// each is asserted below:</para>
/// <list type="number">
/// <item><description>the portal <b>STARTS</b> with the section half-filled, even though the
/// registered sender cannot be constructed at all; and</description></item>
/// <item><description>a send attempt on that install <b>REFUSES</b>, naming the missing key —
/// never the silent success that #2023 was about.</description></item>
/// </list>
///
/// <para>Both are needed, and neither implies the other: "starts" alone is satisfied by quietly
/// dropping mail, which is the worse bug; "refuses" alone is satisfied by a portal that never
/// starts, which is this one.</para>
/// </summary>
public class EmailMisconfigurationRefusalTest
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(30);

    /// <summary>The section as a deployment that never finished wiring mail leaves it: switched ON,
    /// with the client-secret credential blank. This is the memex configuration from the incident.</summary>
    private static EmailOptions HalfConfigured() => new()
    {
        Enabled = true,
        MailboxAddress = "no-reply@example.test",
        // TenantId / ClientId / ClientSecret deliberately unset.
    };

    private static EmailOptions FullyConfigured() => new()
    {
        Enabled = true,
        MailboxAddress = "no-reply@example.test",
        TenantId = "00000000-0000-0000-0000-000000000000",
        ClientId = "client",
        ClientSecret = "secret",
    };

    // ───────────────────────── half 1: the portal starts ─────────────────────────

    /// <summary>
    /// 🚨 The regression pin, at the level the incident happened: a REAL generic host, running the
    /// real mail hosted services, with a sender registered BY TYPE so the container has to
    /// construct it — and that constructor throws exactly the way
    /// <c>Azure.Identity</c>'s does on an unset tenant id.
    ///
    /// <para>Before the fix this is <c>Hosting failed to start</c>, which in production is a pod
    /// that never becomes ready. After it, the host starts: the guard reads the missing key out of
    /// the configuration and refuses there, so nothing ever asks the container for the sender.</para>
    /// </summary>
    [Fact]
    public async Task HalfConfiguredEmail_TheHostStillStarts_AnOptionalIntegrationCannotAbortStartup()
    {
        using var cts = new CancellationTokenSource(TestBudget);
        using var host = BuildHost(HalfConfigured());

        // No try/catch and no assertion helper on purpose: the failure mode IS the throw, and the
        // xUnit failure then carries the real stack — the same one the pod logged.
        await host.StartAsync(cts.Token);

        Assert.False(
            SenderProbe.Of(host).WasConstructed,
            "the sender must never be CONSTRUCTED on a half-configured install: constructing it is "
            + "what threw, and the whole point is that the verdict comes from configuration instead.");

        await host.StopAsync(cts.Token);
    }

    /// <summary>
    /// The control, and it is what stops the case above from being satisfied by a guard that
    /// refuses everything: a fully-configured install DOES resolve its sender at startup. That
    /// resolution is the #2023 question ("is a real sender actually here?"), which has no
    /// configuration-shaped answer and must keep being asked.
    /// </summary>
    [Fact]
    public async Task FullyConfiguredEmail_TheSenderIsStillResolvedAtStartup_SoTheModuleAbsentCheckSurvives()
    {
        using var cts = new CancellationTokenSource(TestBudget);
        using var host = BuildHost(FullyConfigured());

        await host.StartAsync(cts.Token);

        Assert.True(
            SenderProbe.Of(host).WasConstructed,
            "a complete configuration must still resolve the sender — otherwise #2023's "
            + "module-absent refusal would silently stop being checked.");

        await host.StopAsync(cts.Token);
    }

    /// <summary>
    /// Managed identity needs no keys in configuration at all (<c>DefaultAzureCredential</c> takes
    /// no tenant id), which is exactly why <c>Email:UseManagedIdentity=true</c> was never hit by
    /// the crash. Pinned so the new configuration gate cannot start refusing the production
    /// deployment shape — a false refusal here would stop mail on every install that works today.
    /// </summary>
    [Fact]
    public async Task ManagedIdentity_NeedsNoCredentialKeys_AndIsNeverRefused()
    {
        using var cts = new CancellationTokenSource(TestBudget);
        var options = new EmailOptions
        {
            Enabled = true,
            UseManagedIdentity = true,
            MailboxAddress = "no-reply@example.test",
        };

        Assert.Empty(options.MissingCredentialKeys());

        using var host = BuildHost(options);
        await host.StartAsync(cts.Token);

        Assert.True(
            SenderProbe.Of(host).WasConstructed,
            "managed identity is a COMPLETE configuration — it must reach the container check.");

        await host.StopAsync(cts.Token);
    }

    /// <summary>
    /// The same verdict one level down, observed on the provider rather than through a host: the
    /// guard must not so much as ASK for the sender. Asserting "the host started" alone would also
    /// pass if the resolution were merely wrapped in a <c>catch</c> — which is not the fix, because
    /// a catch there would swallow real activation failures too.
    /// </summary>
    [Fact]
    public async Task HalfConfiguredEmail_TheWatcherNeverEvenAsksTheContainerForASender()
    {
        var options = HalfConfigured();
        var services = new ServiceCollection()
            .AddSingleton<IEmailSender, CredentialValidatingSender>()
            .AddSingleton(options)
            .BuildServiceProvider();
        var recording = new SenderRequestCountingProvider(services);
        using var lifetime = new StartableLifetime();

        var watcher = new OutboundEmailSender(recording, lifetime, options);
        await watcher.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();

        Assert.Equal(0, recording.SenderRequests);
        watcher.Dispose();
    }

    // ───────────────────────── half 2: a send refuses ─────────────────────────

    /// <summary>
    /// 🚨 The other half. A portal that starts and then silently swallows mail is a WORSE outcome
    /// than the crash — that is #2023, and it cost an afternoon of inbox archaeology. So on the
    /// same half-configured install a send must surface as a failure, and the message must name the
    /// key to set: an operator told "email is broken" goes hunting; one told
    /// <c>Email:TenantId</c> is done.
    /// </summary>
    [Fact]
    public async Task HalfConfiguredEmail_ASendAttemptRefuses_AndNamesTheMissingKey()
    {
        IEmailSender sender = new NoOpEmailSender(HalfConfigured());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendEmail("x@example.test", "s", "<p>b</p>").FirstAsync().Await());

        Assert.Contains("Email:TenantId", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Email:ClientId", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Email:ClientSecret", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Email:Enabled=true", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal must diagnose the RIGHT cause. On this install the module may be present and
    /// perfectly healthy — only a credential is missing — so telling the operator to land a module
    /// they already have is a confidently wrong answer that sends them somewhere there is nothing
    /// to find.
    /// </summary>
    [Fact]
    public void TheIncompleteConfigurationRefusal_DoesNotBlameTheModule()
    {
        var explanation = EmailDeliveryGuard.ExplainRefusal(HalfConfigured(), "The send is refused.");

        Assert.Contains("Email:TenantId", explanation, StringComparison.Ordinal);
        Assert.DoesNotContain(EmailDeliveryGuard.SenderModule, explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the converse: a COMPLETE configuration whose sender still cannot deliver is the
    /// module-absent case, and there the module name IS the whole diagnosis (#2023). Pinned
    /// together with the case above because the failure mode of "one wording for everything" is
    /// that each install gets told the other one's cause.
    /// </summary>
    [Fact]
    public void TheModuleAbsentRefusal_StillNamesTheModule()
    {
        var explanation = EmailDeliveryGuard.ExplainRefusal(FullyConfigured(), "The send is refused.");

        Assert.Contains(EmailDeliveryGuard.SenderModule, explanation, StringComparison.Ordinal);
    }

    // ───────────────────────── the verdict table ─────────────────────────

    /// <summary>
    /// What "configured" means, stated once. The client-secret flow needs all three keys; managed
    /// identity needs none. Whitespace counts as unset — a Helm value templated from an empty
    /// secret arrives as <c>" "</c> and would fail Azure's validator exactly like <c>""</c>.
    /// </summary>
    [Theory]
    [InlineData(false, "t", "c", "s", 0)]              // client secret, complete
    [InlineData(false, "", "c", "s", 1)]               // 🚨 the incident: tenant id unset
    [InlineData(false, "  ", "c", "s", 1)]             // whitespace is not a tenant id
    [InlineData(false, "", "", "", 3)]                 // nothing configured at all
    [InlineData(true, "", "", "", 0)]                  // managed identity needs no keys
    public void MissingCredentialKeys_NamesExactlyWhatTheSelectedFlowNeeds(
        bool useManagedIdentity, string tenantId, string clientId, string clientSecret, int expected)
    {
        var options = new EmailOptions
        {
            Enabled = true,
            UseManagedIdentity = useManagedIdentity,
            TenantId = tenantId,
            ClientId = clientId,
            ClientSecret = clientSecret,
        };

        Assert.Equal(expected, options.MissingCredentialKeys().Length);
        Assert.All(options.MissingCredentialKeys(),
            key => Assert.StartsWith($"{EmailOptions.SectionName}:", key, StringComparison.Ordinal));
    }

    /// <summary>
    /// A DISABLED section is legitimately blank — that is what <c>Email:Enabled=false</c> means, and
    /// it is every local dev run and every test host. Refusing it would break all of them, so the
    /// guard's verdict is empty regardless of what the keys hold.
    /// </summary>
    [Fact]
    public void Disabled_IsNeverRefusedForIncompleteConfiguration()
        => Assert.Empty(EmailDeliveryGuard.MissingConfiguration(HalfConfigured() with { Enabled = false }));

    // ───────────────────────── fixtures ─────────────────────────

    /// <summary>
    /// A host wired the way the portal is: the bound <see cref="EmailOptions"/>, an
    /// <see cref="IEmailSender"/> registered BY TYPE (so the container must construct it — an
    /// already-built instance would never reproduce the defect), and the two mail hosted services
    /// whose <c>StartAsync</c> runs the guard.
    /// </summary>
    private static IHost BuildHost(EmailOptions options)
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        // Empty: no console provider, so the refusals these cases EXPECT do not read as test noise.
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SenderProbe>();
        builder.Services.AddSingleton<IEmailSender, CredentialValidatingSender>();
        builder.Services.AddHostedService<OutboundEmailSender>();
        builder.Services.AddHostedService<InvitationEmailSender>();
        return builder.Build();
    }

    /// <summary>Records whether the container ever constructed the sender — the outward sign of the
    /// activation that used to abort the host.</summary>
    private sealed class SenderProbe
    {
        public bool WasConstructed { get; private set; }

        public void Constructed() => WasConstructed = true;

        public static SenderProbe Of(IHost host) => host.Services.GetRequiredService<SenderProbe>();
    }

    /// <summary>
    /// Stands in for the module's <c>GraphEmailSender</c>, and faithfully in the one respect that
    /// matters: it builds and VALIDATES its credential in the CONSTRUCTOR. That is what
    /// <c>new ClientSecretCredential(options.TenantId, …)</c> does — <c>Azure.Identity</c>'s
    /// <c>Validations.ValidateTenantId</c> was the top frame of the production stack — so activating
    /// it on a blank tenant id throws at exactly the point the real one did.
    ///
    /// <para>Not a mock: it is a real <see cref="IEmailSender"/> the container constructs by type,
    /// which is the whole mechanism under test. Substituting a pre-built instance would pass
    /// whether or not the fix is present.</para>
    /// </summary>
    private sealed class CredentialValidatingSender : IEmailSender
    {
        public CredentialValidatingSender(EmailOptions options, SenderProbe probe)
        {
            probe.Constructed();
            if (string.IsNullOrWhiteSpace(options.TenantId) && !options.UseManagedIdentity)
                throw new ArgumentException("Invalid tenant id provided.", "tenantId");
        }

        public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
            => Observable.Return(true);

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments)
            => Observable.Return(true);
    }

    /// <summary>Counts how often anything asked the container for an <see cref="IEmailSender"/> —
    /// zero is the assertion that the guard never reached for it.</summary>
    private sealed class SenderRequestCountingProvider(IServiceProvider inner) : IServiceProvider
    {
        private int senderRequests;

        public int SenderRequests => Volatile.Read(ref senderRequests);

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IEmailSender))
                Interlocked.Increment(ref senderRequests);
            return inner.GetService(serviceType);
        }
    }

    /// <summary>A lifetime whose <see cref="IHostApplicationLifetime.ApplicationStarted"/> can be
    /// fired on demand — the real token the watcher registers its watch on.</summary>
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
