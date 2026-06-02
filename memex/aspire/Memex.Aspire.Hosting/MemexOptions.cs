namespace Aspire.Hosting;

/// <summary>
/// Options for <see cref="MemexHostingExtensions.AddMemex"/>. Sensible defaults give a
/// single-node, Azure-free self-host out of the box; override for HA, Azure, or to gate
/// AI capabilities. Every value maps 1:1 to a portal config key, so the same surface flows
/// through Docker Compose <c>.env</c>, Kubernetes config, and ACA / ARM container env.
/// <para>
/// This is an immutable <see langword="record"/>: configure it either by chaining the
/// <c>With…</c> helpers (each returns a new instance) or with a raw record <c>with</c>
/// expression. Both forms compose inside the <see cref="MemexHostingExtensions.AddMemex"/>
/// <c>configure</c> lambda:
/// <code>
/// builder.AddMemex("memex", o => o
///     .WithBackend("Filesystem")
///     .WithOrleansClustering("AdoNet")
///     .WithImage(tag: imageTag)
///     .WithMicrosoftSignIn(clientId, clientSecret, tenantId));
///
/// // equivalent, raw record form:
/// builder.AddMemex("memex", o => o with { Backend = "Filesystem", ImageTag = imageTag });
/// </code>
/// </para>
/// </summary>
public sealed record MemexOptions
{
    /// <summary>Container registry + namespace holding the Memex images. Default GHCR / Systemorph.</summary>
    public string ImageRegistry { get; init; } = "ghcr.io/systemorph";

    /// <summary>Image tag applied to all Memex images (portal, migration). Default <c>latest</c>.</summary>
    public string ImageTag { get; init; } = "latest";

    /// <summary>
    /// Use the <c>memex-portal-ai</c> image (co-hosted Claude Code + GitHub Copilot CLIs baked in)
    /// rather than the lean <c>memex-portal</c>. Default <c>true</c>. The runtime
    /// <see cref="Anthropic"/>/<see cref="ClaudeCode"/>/<see cref="Copilot"/> flags still gate
    /// whether those providers are actually registered.
    /// </summary>
    public bool IncludeAiClis { get; init; } = true;

    /// <summary>
    /// Object-storage / NodeType cache / NuGet cache / DataProtection backend:
    /// <c>Filesystem</c> (default, mounted volumes) or <c>Azure</c> (blob). Mesh data always lives in Postgres.
    /// </summary>
    public string Backend { get; init; } = "Filesystem";

    /// <summary>
    /// Orleans clustering: <c>Localhost</c> (single node, default), <c>AdoNet</c> (HA, Postgres-backed),
    /// or <c>AzureTables</c>.
    /// </summary>
    public string OrleansClustering { get; init; } = "Localhost";

    /// <summary>Encryption master key for provider credentials (<c>Ai:KeyProtection:MasterKey</c>). Required for production.</summary>
    public string? MasterKey { get; init; }

    // --- Embeddings (vector search) -----------------------------------------
    // When the endpoint + key are set, the one-shot migration vector-indexes the built-in
    // documentation and the portal embeds search-bar queries → semantic search. Without them,
    // docs are still copied to Postgres and searchable, just full-text only (no vector ranking).

    /// <summary>Azure AI Foundry embeddings endpoint (Cohere embed-v4). Empty = vector search off (FTS still works).</summary>
    public string? EmbeddingEndpoint { get; init; }

    /// <summary>Embeddings API key (secret). Only emitted to containers when set.</summary>
    public string? EmbeddingApiKey { get; init; }

    /// <summary>Embeddings model / deployment name. Default <c>cohere-embed-v-4-0</c>.</summary>
    public string EmbeddingModel { get; init; } = "cohere-embed-v-4-0";

    /// <summary>The portal's externally reachable base URL; the co-hosted CLIs connect back to <c>{BaseUrl}/mcp</c>. Defaults to the portal's own endpoint.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// OTLP collector endpoint for telemetry export (sets <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> on the portal).
    /// Empty = telemetry no-ops (<c>ServiceDefaults</c> skips the exporter when unset). Point this at an
    /// in-cluster OpenTelemetry collector / Grafana Alloy (e.g. <c>http://otel-collector:4317</c>) to ship
    /// traces + metrics. NOTE: container <b>logs</b> are collected out-of-band by the cluster log agent
    /// (Promtail in the grafana/loki-stack — see <c>deploy/aks/scripts/install-observability.sh</c>), so this
    /// endpoint is only needed for OTLP traces/metrics, not for log shipping.
    /// </summary>
    public string? OtlpEndpoint { get; init; }

    // Deploy-time capability flags. null = leave the portal default (on); set false to disable explicitly.
    public bool? Anthropic { get; init; }
    public bool? AzureFoundry { get; init; }
    public bool? AzureOpenAI { get; init; }
    public bool? OpenAI { get; init; }
    public bool? ClaudeCode { get; init; }
    public bool? Copilot { get; init; }

    // --- External sign-in (OAuth) providers ---------------------------------
    // Set the ClientId to OFFER a provider on the login page; each provider self-skips when
    // its ClientId is empty (so leaving these unset = that provider simply isn't shown). Register
    // the matching redirect URI on the provider app: {BaseUrl}/signin-{microsoft|google|linkedin}.

    /// <summary>Entra/Microsoft app registration Application (client) id. Empty = Microsoft sign-in off.</summary>
    public string? MicrosoftClientId { get; init; }
    /// <summary>Microsoft client secret.</summary>
    public string? MicrosoftClientSecret { get; init; }
    /// <summary>Microsoft/Entra tenant GUID for the HOME directory. Empty/omitted = "common" (any Microsoft account).</summary>
    public string? MicrosoftTenantId { get; init; }

    /// <summary>Google OAuth client id. Empty = Google sign-in off.</summary>
    public string? GoogleClientId { get; init; }
    /// <summary>Google OAuth client secret.</summary>
    public string? GoogleClientSecret { get; init; }

    /// <summary>LinkedIn OAuth client id (powers both sign-in AND post publishing). Empty = LinkedIn off.</summary>
    public string? LinkedInClientId { get; init; }
    /// <summary>LinkedIn OAuth client secret.</summary>
    public string? LinkedInClientSecret { get; init; }

    // --- Outbound email (Microsoft Graph /sendMail) -------------------------
    // When EmailEnabled is true the portal sends mail (invitations, notifications) as the
    // configured no-reply mailbox via the Mail.Send application permission. Left unset = disabled
    // (the portal registers a NoOp sender). See Doc/Architecture/SendingEmail.md.

    /// <summary>Enable outbound email. null/false = NoOp sender (no mail sent).</summary>
    public bool? EmailEnabled { get; init; }
    /// <summary>Mailbox to send as (e.g. <c>no-reply@yourtenant.com</c>).</summary>
    public string? EmailMailboxAddress { get; init; }
    /// <summary>Entra tenant GUID for the mail app (client-secret flow).</summary>
    public string? EmailTenantId { get; init; }
    /// <summary>Mail app registration client id (client-secret flow).</summary>
    public string? EmailClientId { get; init; }
    /// <summary>Mail app client secret (keep in Key Vault).</summary>
    public string? EmailClientSecret { get; init; }
    /// <summary>Authenticate via managed identity instead of a client secret (prod).</summary>
    public bool? EmailUseManagedIdentity { get; init; }

    /// <summary>Enable the inbound email→agent channel (Graph subscription + webhook). Needs Mail.ReadWrite + a public WebhookBaseUrl.</summary>
    public bool? EmailInboundEnabled { get; init; }
    /// <summary>Public base URL Graph calls back for inbound notifications (e.g. <c>https://memex.systemorph.com</c>).</summary>
    public string? EmailWebhookBaseUrl { get; init; }
    /// <summary>Shared secret echoed by Graph on each inbound notification (webhook validation).</summary>
    public string? EmailSubscriptionClientState { get; init; }

    /// <summary>Require an invitation to onboard (<c>Features:Onboarding:InvitationOnly</c>). null = portal default (false).</summary>
    public bool? InvitationOnly { get; init; }

    // === Fluent configuration ===============================================
    // Each helper returns a NEW MemexOptions (record `with`); chain them in the AddMemex
    // lambda. Empty/null arguments leave the existing value untouched where keeping a
    // baked-in default matters (image, embedding model); the grouped sign-in/email helpers
    // assign through verbatim (including null) so the env-emission helpers can self-skip.

    /// <summary>Override the image registry and/or tag. Null/empty arguments keep the current value.</summary>
    public MemexOptions WithImage(string? registry = null, string? tag = null) =>
        this with
        {
            ImageRegistry = string.IsNullOrEmpty(registry) ? ImageRegistry : registry,
            ImageTag = string.IsNullOrEmpty(tag) ? ImageTag : tag,
        };

    /// <summary>Select the AI-CLI portal image (<c>memex-portal-ai</c> vs lean <c>memex-portal</c>).</summary>
    public MemexOptions WithAiClis(bool include = true) => this with { IncludeAiClis = include };

    /// <summary>Object-storage / cache / DataProtection backend: <c>Filesystem</c> or <c>Azure</c>.</summary>
    public MemexOptions WithBackend(string backend) => this with { Backend = backend };

    /// <summary>Orleans clustering provider: <c>Localhost</c>, <c>AdoNet</c>, or <c>AzureTables</c>.</summary>
    public MemexOptions WithOrleansClustering(string clustering) => this with { OrleansClustering = clustering };

    /// <summary>Provider-credential encryption master key (<c>Ai:KeyProtection:MasterKey</c>).</summary>
    public MemexOptions WithMasterKey(string? masterKey) => this with { MasterKey = masterKey };

    /// <summary>The portal's externally reachable base URL ({BaseUrl}/mcp for the co-hosted CLIs).</summary>
    public MemexOptions WithBaseUrl(string? baseUrl) => this with { BaseUrl = baseUrl };

    /// <summary>OTLP collector endpoint for trace/metric export.</summary>
    public MemexOptions WithOtlpEndpoint(string? endpoint) => this with { OtlpEndpoint = endpoint };

    /// <summary>
    /// Configure vector-search embeddings. <paramref name="endpoint"/> + <paramref name="apiKey"/>
    /// turn on semantic search; an empty <paramref name="model"/> keeps the default deployment name.
    /// </summary>
    public MemexOptions WithEmbeddings(string? endpoint, string? apiKey = null, string? model = null) =>
        this with
        {
            EmbeddingEndpoint = endpoint,
            EmbeddingApiKey = apiKey,
            EmbeddingModel = string.IsNullOrEmpty(model) ? EmbeddingModel : model,
        };

    /// <summary>
    /// Toggle individual AI providers / CLIs. A <see langword="null"/> argument leaves that flag
    /// at its current value (so callers flip only what they name, e.g. <c>WithAiProviders(openAI: false)</c>).
    /// </summary>
    public MemexOptions WithAiProviders(
        bool? anthropic = null,
        bool? azureFoundry = null,
        bool? azureOpenAI = null,
        bool? openAI = null,
        bool? claudeCode = null,
        bool? copilot = null) =>
        this with
        {
            Anthropic = anthropic ?? Anthropic,
            AzureFoundry = azureFoundry ?? AzureFoundry,
            AzureOpenAI = azureOpenAI ?? AzureOpenAI,
            OpenAI = openAI ?? OpenAI,
            ClaudeCode = claudeCode ?? ClaudeCode,
            Copilot = copilot ?? Copilot,
        };

    /// <summary>Microsoft / Entra sign-in. Omit <paramref name="tenantId"/> for "common" (any Microsoft account).</summary>
    public MemexOptions WithMicrosoftSignIn(string? clientId, string? clientSecret, string? tenantId = null) =>
        this with
        {
            MicrosoftClientId = clientId,
            MicrosoftClientSecret = clientSecret,
            MicrosoftTenantId = tenantId,
        };

    /// <summary>Google sign-in.</summary>
    public MemexOptions WithGoogleSignIn(string? clientId, string? clientSecret) =>
        this with { GoogleClientId = clientId, GoogleClientSecret = clientSecret };

    /// <summary>LinkedIn — the one app powers both sign-in and post publishing.</summary>
    public MemexOptions WithLinkedIn(string? clientId, string? clientSecret) =>
        this with { LinkedInClientId = clientId, LinkedInClientSecret = clientSecret };

    /// <summary>
    /// Outbound email via Microsoft Graph /sendMail. A <see langword="null"/> <paramref name="enabled"/>
    /// leaves the portal default (NoOp sender); the secret is normally supplied out-of-band (Key Vault).
    /// </summary>
    public MemexOptions WithOutboundEmail(
        bool? enabled = true,
        string? mailboxAddress = null,
        string? tenantId = null,
        string? clientId = null,
        string? clientSecret = null,
        bool? useManagedIdentity = null) =>
        this with
        {
            EmailEnabled = enabled,
            EmailMailboxAddress = mailboxAddress,
            EmailTenantId = tenantId,
            EmailClientId = clientId,
            EmailClientSecret = clientSecret,
            EmailUseManagedIdentity = useManagedIdentity,
        };

    /// <summary>
    /// Inbound email→agent channel (Graph subscription + webhook). Needs Mail.ReadWrite and a
    /// public <paramref name="webhookBaseUrl"/>; a <see langword="null"/> <paramref name="enabled"/>
    /// leaves the portal default (off).
    /// </summary>
    public MemexOptions WithInboundEmail(
        bool? enabled = true,
        string? webhookBaseUrl = null,
        string? clientState = null) =>
        this with
        {
            EmailInboundEnabled = enabled,
            EmailWebhookBaseUrl = webhookBaseUrl,
            EmailSubscriptionClientState = clientState,
        };

    /// <summary>Require an invitation to onboard. <see langword="null"/> leaves the portal default (false).</summary>
    public MemexOptions WithInvitationOnly(bool? invitationOnly = true) => this with { InvitationOnly = invitationOnly };

    internal IEnumerable<KeyValuePair<string, string>> AuthEnvironment()
    {
        if (!string.IsNullOrEmpty(MicrosoftClientId)) yield return new("Authentication__Microsoft__ClientId", MicrosoftClientId);
        if (!string.IsNullOrEmpty(MicrosoftClientSecret)) yield return new("Authentication__Microsoft__ClientSecret", MicrosoftClientSecret);
        if (!string.IsNullOrEmpty(MicrosoftTenantId)) yield return new("Authentication__Microsoft__TenantId", MicrosoftTenantId);
        if (!string.IsNullOrEmpty(GoogleClientId)) yield return new("Authentication__Google__ClientId", GoogleClientId);
        if (!string.IsNullOrEmpty(GoogleClientSecret)) yield return new("Authentication__Google__ClientSecret", GoogleClientSecret);
        if (!string.IsNullOrEmpty(LinkedInClientId))
        {
            // The same LinkedIn app id powers sign-in (Authentication) AND post publishing (Social).
            yield return new("Authentication__LinkedIn__ClientId", LinkedInClientId);
            yield return new("Social__LinkedIn__ClientId", LinkedInClientId);
        }
        if (!string.IsNullOrEmpty(LinkedInClientSecret))
        {
            yield return new("Authentication__LinkedIn__ClientSecret", LinkedInClientSecret);
            yield return new("Social__LinkedIn__ClientSecret", LinkedInClientSecret);
        }
    }

    /// <summary>
    /// Embedding config shared by the migration (vector-indexes docs) and the portal (embeds
    /// search-bar queries). Model is always emitted so both sides size the vector column the same
    /// way; endpoint + key are emitted only when configured (ACA rejects empty secrets).
    /// </summary>
    internal IEnumerable<KeyValuePair<string, string>> EmbeddingEnvironment()
    {
        yield return new("Embedding__Model", EmbeddingModel);
        if (!string.IsNullOrEmpty(EmbeddingEndpoint)) yield return new("Embedding__Endpoint", EmbeddingEndpoint);
        if (!string.IsNullOrEmpty(EmbeddingApiKey)) yield return new("Embedding__ApiKey", EmbeddingApiKey);
    }

    internal IEnumerable<KeyValuePair<string, string>> FeatureEnvironment()
    {
        if (Anthropic is { } an) yield return new("Features__Ai__Providers__Anthropic", an ? "true" : "false");
        if (AzureFoundry is { } af) yield return new("Features__Ai__Providers__AzureFoundry", af ? "true" : "false");
        if (AzureOpenAI is { } ao) yield return new("Features__Ai__Providers__AzureOpenAI", ao ? "true" : "false");
        if (OpenAI is { } op) yield return new("Features__Ai__Providers__OpenAI", op ? "true" : "false");
        if (ClaudeCode is { } cc) yield return new("Features__Ai__Clis__ClaudeCode", cc ? "true" : "false");
        if (Copilot is { } co) yield return new("Features__Ai__Clis__Copilot", co ? "true" : "false");
        if (InvitationOnly is { } io) yield return new("Features__Onboarding__InvitationOnly", io ? "true" : "false");
    }

    /// <summary>
    /// Outbound-email config. Emitted only when configured (ACA rejects empty secrets). The client
    /// secret is best supplied out-of-band (Key Vault → <c>Email__ClientSecret</c>) rather than here.
    /// </summary>
    internal IEnumerable<KeyValuePair<string, string>> EmailEnvironment()
    {
        if (EmailEnabled is { } en) yield return new("Email__Enabled", en ? "true" : "false");
        if (!string.IsNullOrEmpty(EmailMailboxAddress)) yield return new("Email__MailboxAddress", EmailMailboxAddress);
        if (!string.IsNullOrEmpty(EmailTenantId)) yield return new("Email__TenantId", EmailTenantId);
        if (!string.IsNullOrEmpty(EmailClientId)) yield return new("Email__ClientId", EmailClientId);
        if (!string.IsNullOrEmpty(EmailClientSecret)) yield return new("Email__ClientSecret", EmailClientSecret);
        if (EmailUseManagedIdentity is { } mi) yield return new("Email__UseManagedIdentity", mi ? "true" : "false");
        if (EmailInboundEnabled is { } ib) yield return new("Email__InboundEnabled", ib ? "true" : "false");
        if (!string.IsNullOrEmpty(EmailWebhookBaseUrl)) yield return new("Email__WebhookBaseUrl", EmailWebhookBaseUrl);
        if (!string.IsNullOrEmpty(EmailSubscriptionClientState)) yield return new("Email__SubscriptionClientState", EmailSubscriptionClientState);
    }
}
