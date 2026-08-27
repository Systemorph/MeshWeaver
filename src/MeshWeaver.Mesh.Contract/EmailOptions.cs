using System.Collections.Immutable;

namespace MeshWeaver.Mesh;

/// <summary>
/// Configuration for outbound system email, bound from the <c>Email</c> configuration section.
/// When <see cref="Enabled"/> is <c>false</c> (the default) the host registers a no-op sender so
/// local dev and tests never send mail.
///
/// <para>The reference implementation sends via Microsoft Graph <c>/sendMail</c> using the
/// <c>Mail.Send</c> <b>application</b> permission. That permission requires tenant-admin consent on
/// the app registration and a real (licensed or shared) mailbox named by
/// <see cref="MailboxAddress"/> that the app is allowed to send as. In production prefer
/// <see cref="UseManagedIdentity"/> and grant the managed identity the <c>Mail.Send</c> app role.</para>
/// </summary>
public sealed record EmailOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "Email";

    /// <summary>When false, the no-op sender is registered (no mail leaves the process).</summary>
    public bool Enabled { get; init; }

    /// <summary>The mailbox to send as (e.g. <c>no-reply@yourtenant.com</c>).</summary>
    public string MailboxAddress { get; init; } = "";

    /// <summary>Entra tenant id (client-credentials flow). Unused when <see cref="UseManagedIdentity"/>.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>App-registration client id (client-credentials flow). Unused when <see cref="UseManagedIdentity"/>.</summary>
    public string ClientId { get; init; } = "";

    /// <summary>App-registration client secret (client-credentials flow). Unused when <see cref="UseManagedIdentity"/>.</summary>
    public string ClientSecret { get; init; } = "";

    /// <summary>
    /// When true, authenticate via <c>DefaultAzureCredential</c> (managed identity in prod)
    /// instead of a client secret. Grant the identity the <c>Mail.Send</c> app role.
    /// </summary>
    public bool UseManagedIdentity { get; init; }

    // --- Inbound (email-as-agent channel) -----------------------------------

    /// <summary>
    /// When true, the portal subscribes to the mailbox inbox (Microsoft Graph change notifications)
    /// and routes inbound mail to agent threads (known users) or the admin inbox (everyone else).
    /// Requires the <c>Mail.ReadWrite</c> application permission and a public <see cref="WebhookBaseUrl"/>.
    /// </summary>
    public bool InboundEnabled { get; init; }

    /// <summary>Public base URL Graph calls back for notifications (e.g. <c>https://portal.example.com</c>); the webhook is <c>{WebhookBaseUrl}/api/email</c>.</summary>
    public string WebhookBaseUrl { get; init; } = "";

    /// <summary>Shared secret echoed by Graph on every notification; the webhook rejects mismatches. Generate a random value per deployment.</summary>
    public string SubscriptionClientState { get; init; } = "";

    /// <summary>
    /// The <c>Email:*</c> keys this section is MISSING for the credential flow it selected — empty
    /// when the section carries everything that flow needs. Says nothing about
    /// <see cref="Enabled"/>: a disabled section is legitimately blank, and it is the CALLER that
    /// decides whether an incomplete section matters (see <c>EmailDeliveryGuard</c>).
    ///
    /// <para>🚨 <b>This is inert data, and that is the entire point.</b> It answers "can this
    /// install authenticate?" from the bound values alone — no container, no credential object, no
    /// I/O — so it cannot throw and cannot activate anything. The question used to be answerable
    /// only by CONSTRUCTING the sender, and the reference sender builds an Azure
    /// <c>ClientSecretCredential</c> in its constructor, which validates the tenant id eagerly.
    /// Asking it from an <c>IHostedService.StartAsync</c> therefore turned a half-filled
    /// <c>Email</c> section into <c>Hosting failed to start</c> — the whole portal down because an
    /// OPTIONAL integration was misconfigured (#2510). A verdict reached from configuration cannot
    /// do that.</para>
    ///
    /// <para>Managed identity needs no keys here at all: <c>DefaultAzureCredential</c> takes no
    /// tenant id, which is also why <c>Email:UseManagedIdentity=true</c> was never affected by that
    /// crash. The client-secret flow needs all three, and each is named with its full configuration
    /// key so an operator is told what to set rather than that "email is broken".</para>
    ///
    /// <para>Note what this deliberately does NOT do: it does not judge whether a PRESENT value is
    /// a valid tenant id. Re-implementing <c>Azure.Identity</c>'s validator here would be a second
    /// copy free to drift from the real one. A present-but-malformed value is the sender's own
    /// business, and the sender must therefore not validate it during construction either.</para>
    /// </summary>
    public ImmutableArray<string> MissingCredentialKeys()
    {
        // Managed identity carries no secrets in configuration — nothing here can be missing.
        if (UseManagedIdentity)
            return ImmutableArray<string>.Empty;

        var missing = ImmutableArray.CreateBuilder<string>(3);
        if (string.IsNullOrWhiteSpace(TenantId)) missing.Add(Key(nameof(TenantId)));
        if (string.IsNullOrWhiteSpace(ClientId)) missing.Add(Key(nameof(ClientId)));
        if (string.IsNullOrWhiteSpace(ClientSecret)) missing.Add(Key(nameof(ClientSecret)));
        return missing.ToImmutable();
    }

    /// <summary>The full configuration key for one of this section's properties, e.g. <c>Email:TenantId</c>.</summary>
    private static string Key(string property) => $"{SectionName}:{property}";
}
