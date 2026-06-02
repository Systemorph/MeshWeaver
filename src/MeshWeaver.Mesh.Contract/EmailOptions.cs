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

    /// <summary>Public base URL Graph calls back for notifications (e.g. <c>https://memex.systemorph.com</c>); the webhook is <c>{WebhookBaseUrl}/api/email</c>.</summary>
    public string WebhookBaseUrl { get; init; } = "";

    /// <summary>Shared secret echoed by Graph on every notification; the webhook rejects mismatches. Generate a random value per deployment.</summary>
    public string SubscriptionClientState { get; init; } = "";
}
