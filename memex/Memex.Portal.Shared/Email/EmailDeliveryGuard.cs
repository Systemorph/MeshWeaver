using System.Collections.Immutable;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// 🚨 <b><c>Email:Enabled=true</c> on an install that cannot actually deliver is a REFUSED
/// configuration, not a working one</b> (#2023) — and reaching that verdict must never cost more
/// than the mail it guards (#2510).
///
/// <para><b>The defect this closes.</b> While the Graph sender was compiled into the portal,
/// "a sender is registered" and "mail can be delivered" were the same question, and
/// <see cref="NoOpEmailSender"/>'s report-success-without-sending contract was safe: it was only
/// ever reached with <c>Email:Enabled=false</c>, where no watcher calls it. The move into the
/// <c>MeshWeaver.Mail.MicrosoftGraph</c> module (#1737) added a SECOND path to the no-op — the
/// <c>TryAddSingleton</c> fallback when the module is not on the install — and on that path
/// <c>Enabled</c> IS true and the watchers DO run. <see cref="OutboundEmailSender"/> then flipped
/// every queued mail <c>New → Sending → Sent</c> while nothing left the process: node data saying
/// delivered, no error, no log (the send lines are Information under a Warning default). Hours
/// were spent hunting inboxes, junk folders and message traces because <c>Sent</c> was trusted.</para>
///
/// <para><b>Why refusing beats succeeding quietly.</b> Mail that stays <c>New</c> is recoverable:
/// complete the configuration and it goes out, with no data repair. Mail stamped <c>Sent</c> that
/// was never sent is unrecoverable, because nothing distinguishes it from mail that WAS sent. So
/// the watchers do not start at all on a refused configuration, and the no-op sender fails loudly
/// for every other caller instead of returning <c>true</c>.</para>
///
/// <para>The refusal is logged at <see cref="LogLevel.Error"/> from <c>StartAsync</c> — inside the
/// startup window — so <c>StartupErrorNotifier</c> folds it into the Admin bell notification that
/// reports a degraded boot, rather than it sitting at Information under a Warning default where
/// nobody would ever see it.</para>
///
/// <para>🚨 <b>Two refused configurations, asked in a fixed order.</b> See
/// <see cref="RefuseToStart"/>: the CONFIGURATION verdict is reached first, from inert data, and
/// only a completely-configured install goes on to ask the CONTAINER. That order is the whole of
/// #2510 and it is load-bearing — not a micro-optimisation.</para>
/// </summary>
internal static class EmailDeliveryGuard
{
    /// <summary>The module that supplies a real sender. Named in every refusal so the operator is
    /// told what to provision rather than that "email is broken".</summary>
    internal const string SenderModule = "MeshWeaver.Mail.MicrosoftGraph";

    /// <summary>
    /// The CONFIGURATION verdict: the <c>Email:*</c> keys this install is enabled-but-missing, or
    /// empty when there is nothing to refuse on this ground. A disabled section is never refused —
    /// blank keys are exactly what <c>Email:Enabled=false</c> means.
    ///
    /// <para>🚨 Reached without touching the service provider, and that is the point: see
    /// <see cref="EmailOptions.MissingCredentialKeys"/>. Everything a container could tell us here
    /// costs a construction that can throw; everything this tells us costs a string comparison.</para>
    /// </summary>
    internal static ImmutableArray<string> MissingConfiguration(EmailOptions options)
        => options.Enabled ? options.MissingCredentialKeys() : ImmutableArray<string>.Empty;

    /// <summary>
    /// The CONTAINER verdict: this install is configured to send mail, but the sender it resolved
    /// cannot deliver it. A <c>null</c> sender counts: no sender at all certainly does not deliver,
    /// and treating "could not resolve" as "probably fine" would be the skip-trapdoor this guard
    /// exists to remove.
    /// </summary>
    internal static bool RefusesDelivery(EmailOptions options, IEmailSender? sender)
        => options.Enabled && sender?.DeliversMail is not true;

    /// <summary>The one wording for the module-absent refusal, so the sender, the watchers and the
    /// tests all say the same thing.</summary>
    /// <param name="what">What is being refused (a component that will not start, or the send itself).</param>
    internal static string Explain(string what)
        => $"Email:Enabled=true but the resolved {nameof(IEmailSender)} does NOT deliver mail — this "
           + $"install has no mail sender. {what} Land the {SenderModule} module on this deployment "
           + "(Modules:Assemblies / the plugin bundle) to restore delivery. Refusing rather than "
           + "reporting success: mail left queued is recoverable once the module lands, mail stamped "
           + "Sent that was never sent is not.";

    /// <summary>
    /// The one wording for the INCOMPLETE-configuration refusal. Deliberately does not blame the
    /// module: on this path the module may well be present and perfectly healthy — what is missing
    /// is the credential the operator never set, so the message names the keys instead. Telling an
    /// operator to "land the module" when the module is already landed is the kind of confidently
    /// wrong diagnosis that costs an afternoon.
    /// </summary>
    /// <param name="missingKeys">The keys from <see cref="MissingConfiguration"/>; never empty.</param>
    /// <param name="what">What is being refused (a component that will not start, or the send itself).</param>
    internal static string ExplainIncomplete(ImmutableArray<string> missingKeys, string what)
        => $"Email:Enabled=true but the Email section is INCOMPLETE — {string.Join(", ", missingKeys)} "
           + $"{(missingKeys.Length == 1 ? "is" : "are")} not set, so no {nameof(IEmailSender)} on this "
           + $"install can authenticate. {what} Set {string.Join(", ", missingKeys)}, or set "
           + "Email:UseManagedIdentity=true and grant the managed identity the Mail.Send app role, to "
           + "restore delivery. Refusing rather than reporting success: mail left queued is recoverable "
           + "once the section is completed, mail stamped Sent that was never sent is not.";

    /// <summary>
    /// The refusal wording that fits THIS install — incomplete configuration when that is the
    /// cause, the module-absent wording otherwise. One entry point so a caller holding only the
    /// options (the no-op sender) reports the same cause the watchers logged, instead of the two
    /// disagreeing about why the same install refused.
    /// </summary>
    internal static string ExplainRefusal(EmailOptions options, string what)
        => MissingConfiguration(options) is { IsEmpty: false } missing
            ? ExplainIncomplete(missing, what)
            : Explain(what);

    /// <summary>
    /// The watcher-side gate: answers <c>true</c> when this install cannot deliver mail, having
    /// logged the refusal at Error, so the caller returns WITHOUT starting its watch. Queued mail
    /// then visibly stays <c>New</c>.
    ///
    /// <para>🚨 <b>The order of the two questions is the fix for #2510.</b> This runs inside
    /// <c>IHostedService.StartAsync</c>, where a throw does not degrade a feature — it aborts the
    /// HOST. Asking the container first meant ACTIVATING the module's sender there, and the
    /// reference sender validates its Azure credential in its constructor: an unset
    /// <c>Email:TenantId</c> became <c>ArgumentException: Invalid tenant id provided</c> →
    /// <c>Hosting failed to start</c> → a pod that never becomes ready. A half-configured optional
    /// integration took down the entire portal, twice, on two rollouts.</para>
    ///
    /// <para>So the configuration is asked FIRST, from data that cannot throw. An install missing
    /// its credentials is refused here and the sender is never resolved at all — which is the
    /// difference between a portal that serves without mail and a portal that does not serve. Only
    /// a COMPLETELY configured install goes on to ask the container, which is the question #2023
    /// needs (is the module actually here?) and the one that has no configuration-shaped answer.</para>
    ///
    /// <para>This is not a <c>catch</c> around the resolution, deliberately. Swallowing an
    /// activation failure would also swallow the ones that mean something is genuinely broken; not
    /// asking a question whose answer we already hold leaves every other failure exactly as loud as
    /// it was.</para>
    /// </summary>
    /// <param name="services">The provider the host's sender singleton lives in.</param>
    /// <param name="options">The bound <c>Email</c> section.</param>
    /// <param name="logger">Where the refusal is reported.</param>
    /// <param name="component">The watcher refusing to start, named in the log line.</param>
    internal static bool RefuseToStart(
        IServiceProvider services, EmailOptions options, ILogger? logger, string component)
    {
        var what = $"{component} will not claim or send anything, so outbound mail stays visibly queued.";

        // 1. CONFIGURATION — inert, cannot throw, cannot activate anything.
        var missing = MissingConfiguration(options);
        if (!missing.IsEmpty)
        {
            logger?.LogError(
                "{Component} is NOT starting. {Explanation}", component, ExplainIncomplete(missing, what));
            return true;
        }

        // 2. CONTAINER — only now, and only for an install whose configuration is complete.
        if (!RefusesDelivery(options, services.GetService<IEmailSender>()))
            return false;

        logger?.LogError("{Component} is NOT starting. {Explanation}", component, Explain(what));
        return true;
    }
}
