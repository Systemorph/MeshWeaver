using System.Collections.Immutable;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// The BOOT-TIME guard for the <c>Email</c> section: an install that CLAIMS mail
/// (<c>Email:Enabled=true</c>) and cannot possibly deliver it is refused at startup, by key
/// (MeshWeaver#2636, #2637).
///
/// <para>🚨 <b>The defect this closes.</b> On memex the section was half-set — enabled, with
/// <c>Email:TenantId</c> and <c>Email:ClientId</c> unset — and the portal came up perfectly
/// healthy with mail DARK. <see cref="EmailDeliveryGuard"/> did its job (the watchers refused, so
/// nothing was ever falsely stamped <c>Sent</c>), but a single <c>Error</c> line in a pod log is
/// not a signal anyone reads: <c>/health</c> was 200, the site served, and every invitation,
/// notification and document share queued as <c>New</c> and stayed there. The install said mail
/// was on and dropped every message. That is the failure — not the missing key, which is
/// ordinary, but the fact that nothing surfaced it.</para>
///
/// <para><b>Two rules, and they are deliberately different</b> — the same distinction
/// <see cref="Authentication.MicrosoftTenant"/> encodes for the OIDC tenant (#2621), and it is the
/// whole of this guard:</para>
/// <list type="number">
/// <item><description><b>Absent or <c>Email:Enabled=false</c> = the integration was NEVER
/// configured, and it must NOT refuse.</b> A blank section is exactly what "no mail on this
/// install" looks like — it is every local dev run, every test host, every deployment that never
/// wanted mail — and aborting a portal because an OPTIONAL integration is unconfigured is the
/// #2510 failure verbatim (a guard that fails worse than the thing it guards). Nothing here is
/// even looked at in that case.</description></item>
/// <item><description><b><c>Email:Enabled=true</c> with the section INCOMPLETE = a real
/// misconfiguration, refused at startup, naming the exact keys.</b> Someone MEANT to enable mail.
/// An install in this state is not sending anything today, so refusing it cannot take a working
/// portal's mail down — it converts a silent drop into a named configuration error at boot, which
/// is the one thing an operator can act on.</description></item>
/// </list>
///
/// <para>🚨 <b>Why this does NOT re-open #2510.</b> #2510 was not "email configuration stopped the
/// host" in the abstract; it was <c>EmailDeliveryGuard</c> ACTIVATING the Graph sender from
/// <c>IHostedService.StartAsync</c>, whose <c>ClientSecretCredential</c> constructor threw
/// <c>ArgumentException: Invalid tenant id provided</c> → <c>Hosting failed to start</c> — an
/// unactionable message, from a container resolution, that named Azure's validator instead of the
/// key to set. Two properties keep that fixed and are not negotiable here:
/// <list type="bullet">
/// <item><description>this verdict is reached from <b>inert configuration data only</b> — no
/// container, no credential object, no I/O — so it cannot throw for any reason other than the one
/// it is reporting; and</description></item>
/// <item><description>the <b>unconfigured</b> install (blank/disabled) is never refused.</description></item>
/// </list>
/// What changes is only the SEVERITY of the enabled-but-incomplete case: from a log line nobody
/// reads to a named startup refusal. <see cref="EmailDeliveryGuard"/> stays exactly as it is — it
/// still refuses the watchers on hosts that never call this guard (LocalMesh, test hosts, any host
/// not going through <c>ConfigureMemexMesh</c>), and it is still what guarantees queued mail is
/// never stamped <c>Sent</c>.</para>
///
/// <para>Pure decision, no I/O and no container, so it is unit-testable without spinning a host —
/// the same shape as <c>MemexConfiguration.ValidateContentStorageDurability</c> and
/// <see cref="Authentication.MicrosoftTenant"/>.</para>
///
/// <para>Scope is OUTBOUND, deliberately. The inbound channel (<c>Email:InboundEnabled</c>,
/// <c>Email:WebhookBaseUrl</c>, <c>Email:SubscriptionClientState</c>) has the same shape but no
/// incident behind it, and widening a boot-time refusal past the failure it is fixing is how a
/// guard acquires a blast radius nobody signed off on.</para>
/// </summary>
public static class EmailConfigurationGuard
{
    /// <summary>The configuration section this guard reads, in binder form.</summary>
    public const string SectionName = EmailOptions.SectionName;

    /// <summary>The switch that turns a blank section into a CLAIM, in binder form.</summary>
    public const string EnabledKey = $"{SectionName}:{nameof(EmailOptions.Enabled)}";

    /// <summary>The mailbox the system path sends AS — required by both credential flows.</summary>
    public const string MailboxAddressKey = $"{SectionName}:{nameof(EmailOptions.MailboxAddress)}";

    /// <summary>The alternative to the client-secret flow, named in every refusal as a way out.</summary>
    public const string UseManagedIdentityKey = $"{SectionName}:{nameof(EmailOptions.UseManagedIdentity)}";

    /// <summary>
    /// A binder key (<c>Email:TenantId</c>) as the environment-variable / configMap entry an
    /// operator actually sets (<c>Email__TenantId</c>).
    ///
    /// <para>Both forms appear in every refusal on purpose. The binder form is what the code, the
    /// docs and <see cref="EmailOptions.MissingCredentialKeys"/> speak; the environment form is
    /// what is typed into a Helm value, a configMap or a <c>kubectl set env</c>, and an operator
    /// reading a pod log has no reason to know the two are the same key. Naming only one of them
    /// is how a message that looks actionable still costs a search.</para>
    /// </summary>
    public static string EnvironmentKey(string binderKey)
        => binderKey.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>
    /// The keys this install CLAIMS mail with and has not set — empty when there is nothing to
    /// refuse, which includes every disabled or absent section.
    ///
    /// <para>The credential half is delegated to <see cref="EmailOptions.MissingCredentialKeys"/>
    /// rather than re-derived, so "which keys does the selected flow need" has exactly ONE
    /// definition; a second copy here would be free to drift from the one the watchers and the
    /// no-op sender already report. Managed identity carries no credential keys at all.</para>
    ///
    /// <para><see cref="EmailOptions.MailboxAddress"/> is added on top because it is required by
    /// BOTH flows and by no credential: the system send path is
    /// <c>/users/{MailboxAddress}/sendMail</c>, so a blank value composes a Graph request for no
    /// mailbox. It is not a credential, which is why it is not in
    /// <see cref="EmailOptions.MissingCredentialKeys"/> — but it is just as certainly the
    /// difference between an install that sends and one that silently does not.</para>
    /// </summary>
    /// <param name="options">The bound <c>Email</c> section (<c>null</c> when absent).</param>
    public static ImmutableArray<string> MissingRequiredKeys(EmailOptions? options)
    {
        // Rule 1: absent or disabled is a COMPLETE, supported configuration. Nothing is missing
        // from a section that never claimed anything.
        if (options is not { Enabled: true })
            return ImmutableArray<string>.Empty;

        var missing = ImmutableArray.CreateBuilder<string>(4);
        if (string.IsNullOrWhiteSpace(options.MailboxAddress))
            missing.Add(MailboxAddressKey);
        missing.AddRange(options.MissingCredentialKeys());
        return missing.ToImmutable();
    }

    /// <summary>
    /// The boot-time guard: throws when this install claims mail it cannot send, so the
    /// misconfiguration is named at startup instead of surfacing as mail that never arrives. An
    /// absent or disabled section passes.
    /// </summary>
    /// <param name="options">The bound <c>Email</c> section (<c>null</c> when absent).</param>
    /// <exception cref="InvalidOperationException">
    /// <c>Email:Enabled=true</c> and a required key is unset; the message names every missing key
    /// in both the binder and the environment-variable form.
    /// </exception>
    public static void Validate(EmailOptions? options)
    {
        var missing = MissingRequiredKeys(options);
        if (!missing.IsEmpty)
            throw new InvalidOperationException(Refusal(missing));
    }

    /// <summary>
    /// The call-site form: binds the <c>Email</c> section exactly as the portal composition does
    /// (<c>GetSection(...).Get&lt;EmailOptions&gt;()</c>) and validates it. An absent section binds
    /// to <c>null</c> and passes.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    public static void Validate(IConfiguration configuration)
        => Validate(configuration.GetSection(SectionName).Get<EmailOptions>());

    /// <summary>
    /// The refusal wording. Names every missing key in both forms, and every way OUT of the
    /// refusal — complete the client-secret flow, switch to managed identity, or turn the
    /// integration off — because "your configuration is incomplete" without a supported way to
    /// disable it is an instruction to guess.
    /// </summary>
    /// <param name="missing">The keys from <see cref="MissingRequiredKeys"/>; never empty.</param>
    internal static string Refusal(ImmutableArray<string> missing)
    {
        var named = string.Join(", ", missing.Select(k => $"{k} ({EnvironmentKey(k)})"));
        return $"Email misconfiguration (issues #2636, #2637): {EnabledKey}=true "
            + $"({EnvironmentKey(EnabledKey)}=true), but the {SectionName} section is INCOMPLETE — "
            + $"{named} {(missing.Length == 1 ? "is" : "are")} not set, so this install cannot send "
            + "mail at all. It would accept every invitation, notification and document share and "
            + "then DROP it: the mail queues as New and stays there, the portal serves normally, "
            + "/health stays 200, and nothing on any screen says mail is dark. Set the keys named "
            + $"above, or set {UseManagedIdentityKey}=true "
            + $"({EnvironmentKey(UseManagedIdentityKey)}=true) and grant the managed identity the "
            + $"Mail.Send app role, or turn the integration off with {EnabledKey}=false "
            + $"({EnvironmentKey(EnabledKey)}=false) — a disabled section is a complete, supported "
            + "configuration and starts normally. Refusing to start so the misconfiguration "
            + "surfaces now rather than as mail that never arrives.";
    }
}
