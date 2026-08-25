using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// 🚨 <b><c>Email:Enabled=true</c> with a sender that does not deliver is a REFUSED
/// configuration, not a working one</b> (#2023).
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
/// land the module and it goes out, with no data repair. Mail stamped <c>Sent</c> that was never
/// sent is unrecoverable, because nothing distinguishes it from mail that WAS sent. So the
/// watchers do not start at all on this configuration, and the no-op sender fails loudly for
/// every other caller instead of returning <c>true</c>.</para>
///
/// <para>The refusal is logged at <see cref="LogLevel.Error"/> from <c>StartAsync</c> — inside the
/// startup window — so <c>StartupErrorNotifier</c> folds it into the Admin bell notification that
/// reports a degraded boot, rather than it sitting at Information under a Warning default where
/// nobody would ever see it.</para>
/// </summary>
internal static class EmailDeliveryGuard
{
    /// <summary>The module that supplies a real sender. Named in every refusal so the operator is
    /// told what to provision rather than that "email is broken".</summary>
    internal const string SenderModule = "MeshWeaver.Mail.MicrosoftGraph";

    /// <summary>
    /// True when this install is configured to send mail but the resolved sender cannot deliver
    /// it. A <c>null</c> sender counts: no sender at all certainly does not deliver, and treating
    /// "could not resolve" as "probably fine" would be the skip-trapdoor this guard exists to
    /// remove.
    /// </summary>
    internal static bool RefusesDelivery(EmailOptions options, IEmailSender? sender)
        => options.Enabled && sender?.DeliversMail is not true;

    /// <summary>The one wording, so the sender, the watchers and the tests all say the same thing.</summary>
    /// <param name="what">What is being refused (a component that will not start, or the send itself).</param>
    internal static string Explain(string what)
        => $"Email:Enabled=true but the resolved {nameof(IEmailSender)} does NOT deliver mail — this "
           + $"install has no mail sender. {what} Land the {SenderModule} module on this deployment "
           + "(Modules:Assemblies / the plugin bundle) to restore delivery. Refusing rather than "
           + "reporting success: mail left queued is recoverable once the module lands, mail stamped "
           + "Sent that was never sent is not.";

    /// <summary>
    /// The watcher-side gate: resolves the sender this install would actually use and, when it
    /// cannot deliver, logs the refusal at Error and answers <c>true</c> so the caller returns
    /// WITHOUT starting its watch. Queued mail then visibly stays <c>New</c>.
    /// </summary>
    /// <param name="services">The provider the host's sender singleton lives in.</param>
    /// <param name="options">The bound <c>Email</c> section.</param>
    /// <param name="logger">Where the refusal is reported.</param>
    /// <param name="component">The watcher refusing to start, named in the log line.</param>
    internal static bool RefuseToStart(
        IServiceProvider services, EmailOptions options, ILogger? logger, string component)
    {
        if (!RefusesDelivery(options, services.GetService<IEmailSender>()))
            return false;

        logger?.LogError(
            "{Component} is NOT starting. {Explanation}",
            component,
            Explain($"{component} will not claim or send anything, so outbound mail stays visibly queued."));
        return true;
    }
}
