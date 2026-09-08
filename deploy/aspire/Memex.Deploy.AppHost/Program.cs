// Dedicated, image-based deployment AppHost for the MeshWeaver Memex portal.
//
// Mirrors the conventions of the main memex/aspire/Memex.AppHost, but deploys the PUBLISHED
// GHCR images via the MeshWeaver.Aspire.Hosting.Memex integration (builder.AddMemex) rather than
// building the portal from source. The instance is declared ONCE, as a Deployment record: every
// fluent call below is a pure transform of that record, the containers, volumes and environment
// are derived from it, and `--record-out <path>` writes the final record — the file the setup
// wizard or a Provision action on the control instance takes (Doc/Architecture/
// ConfiguringAnInstanceFromAspire). The Aspire publishers turn the same model into artifacts:
//
//   aspire publish --apphost deploy/aspire/Memex.Deploy.AppHost/Memex.Deploy.AppHost.csproj \
//       -o deploy/compose -- --mode compose          # Docker Compose (single)
//   aspire publish ... -o deploy/compose-ha -- --mode compose-ha     # Docker Compose (HA)
//   aspire publish ... -o deploy/aca     -- --mode azure             # Azure Container Apps (bicep)
//
// 🚨 The Kubernetes/Helm publisher is deliberately NOT wired: Aspire emits a RECORD, never a chart
// (maintainer, 2026-09-08). The chart is rendered from the record by the Hosting module.
//
// Tunables (dotnet user-secrets / env / GitHub secrets), all optional:
//   Parameters:image-registry  (default ghcr.io/systemorph)
//   Parameters:image-tag       (default <major>-latest)
//   Parameters:include-ai-clis (default true → portal-ai image)
//   Parameters:key-protection-master-key  (REQUIRED for production)
//   record-out                 (a path; writes the final Deployment record there on start)

var builder = DistributedApplication.CreateBuilder(args);
var cfg = builder.Configuration;

var mode = cfg["mode"]?.ToLowerInvariant() ?? "compose";
var ha = mode.EndsWith("-ha", StringComparison.Ordinal);

if (mode == "azure")
{
    builder.AddAzureContainerAppEnvironment("memex-aca");
}
else
{
    builder.AddDockerComposeEnvironment("self-host");
}

var registry = (cfg["Parameters:image-registry"] ?? MemexOptions.DefaultImageRegistry).TrimEnd('/');
var includeAiClis = !string.Equals(cfg["Parameters:include-ai-clis"], "false", StringComparison.OrdinalIgnoreCase);
var repository = $"{registry}/{(includeAiClis ? MemexOptions.DefaultPortalRepository : "memex-portal")}";

var portal = builder.AddMemex("memex")
    // A null tag keeps the default (<major>-latest); a stated one pins the record.
    .WithImage(repository, cfg["Parameters:image-tag"])
    // Real, Postgres-backed cluster membership in every deployment (never Localhost in prod).
    // Works for a single silo or an HA replica set; the `ha` flag only drives replica count.
    .WithOrleansClustering("AdoNet")
    .WithReplicas(ha ? 2 : 1)
    // External sign-in (OAuth) providers — client IDs are record fields; the secrets are below.
    // Each provider is offered only when its ClientId is set. Register the redirect URI on each
    // app: {BaseUrl}/signin-{microsoft|google|linkedin}.
    .WithSignIn(
        microsoftClientId: cfg["Parameters:microsoft-client-id"],
        microsoftTenantId: cfg["Parameters:microsoft-tenant-id"],
        googleClientId: cfg["Parameters:google-client-id"],
        linkedInClientId: cfg["Parameters:linkedin-client-id"])
    // The same LinkedIn app powers sign-in AND post publishing.
    .WithSocialLinkedIn(cfg["Parameters:linkedin-client-id"]);

// Outbound email (Microsoft Graph /sendMail) — invitations + script-triggered notifications — and
// the inbound email→agent channel (Graph subscription + webhook; needs Mail.ReadWrite + a public
// URL). Both are fields of the record's email block; the client secret is a secret (below).
if (ParseBool(cfg["Parameters:email-enabled"]) is { } emailEnabled)
    portal.WithEmail(
        enabled: emailEnabled,
        mailboxAddress: cfg["Parameters:email-mailbox-address"],
        clientId: cfg["Parameters:email-client-id"],
        tenantId: cfg["Parameters:email-tenant-id"],
        useManagedIdentity: ParseBool(cfg["Parameters:email-use-managed-identity"]),
        inboundEnabled: ParseBool(cfg["Parameters:email-inbound-enabled"]),
        webhookBaseUrl: cfg["Parameters:email-webhook-base-url"]);

// The advanced rung — portal configuration keys the typed surface does not carry, on the record's
// extraPortalConfig: embeddings (the migration vector-indexes the built-in docs with the same
// settings the portal embeds search-bar queries with), invitation-only onboarding, the Teams bot.
foreach (var (key, value) in new (string Key, string? Value)[]
{
    ("Embedding__Endpoint", cfg["Parameters:embedding-endpoint"]),
    ("Embedding__Model", cfg["Parameters:embedding-model"]),
    ("Features__Onboarding__InvitationOnly", Flag(cfg["Parameters:invitation-only"])),
    ("Teams__Enabled", Flag(cfg["Parameters:teams-enabled"])),
    ("Teams__AppId", cfg["Parameters:teams-app-id"]),
    ("Teams__TenantId", cfg["Parameters:teams-tenant-id"]),
})
{
    if (!string.IsNullOrEmpty(value))
        portal.WithPortalConfig(key, value);
}

// 🚨 Secrets are NEVER on the record (it syncs to git). Under Aspire they are values handed to the
// container directly; on AKS the same keys come from Key Vault through the record's
// keyVaultSecrets map (email-clientsecret → Email__ClientSecret, teams-apppassword →
// Teams__AppPassword, …). `--record-out` never writes any of these.
foreach (var (key, value) in new (string Key, string? Value)[]
{
    ("Ai__KeyProtection__MasterKey", cfg["Parameters:key-protection-master-key"]),
    ("Embedding__ApiKey", cfg["Parameters:embedding-key"]),
    ("Authentication__Microsoft__ClientSecret", cfg["Parameters:microsoft-client-secret"]),
    ("Authentication__Google__ClientSecret", cfg["Parameters:google-client-secret"]),
    ("Authentication__LinkedIn__ClientSecret", cfg["Parameters:linkedin-client-secret"]),
    ("Social__LinkedIn__ClientSecret", cfg["Parameters:linkedin-client-secret"]),
    ("Email__ClientSecret", cfg["Parameters:email-client-secret"]),
    ("Email__SubscriptionClientState", cfg["Parameters:email-subscription-client-state"]),
    ("Teams__AppPassword", cfg["Parameters:teams-app-password"]),
})
{
    if (!string.IsNullOrEmpty(value))
        portal.WithSecret(key, value);
}

// The record leaves the AppHost: the file a developer hands to the setup wizard or a Provision
// action on the control instance.
if (cfg["record-out"] is { Length: > 0 } recordOut)
    portal.PublishRecord(recordOut);

// Self-host filesystem backend: the portal writes DataProtection keys, the NodeType
// assembly cache, and the NuGet cache under /data. The aspnet base image runs as the
// non-root `app` user, but a freshly-created Docker named volume is root-owned, so the
// app cannot create those directories — startup dies with
// `UnauthorizedAccessException: Access to the path '/data/dataprotection-keys' is denied`.
// Run the portal as root in the Compose targets so it owns its mounted data volume.
// (Kubernetes/AKS handles this via the chart — Azure Files CSI mounts 0777 / uid-mapped — and
// ACA runs containers as root by default, so this is Compose-only.)
if (mode != "azure")
{
    portal.PublishAsDockerComposeService((_, service) => service.User = "root");
}

builder.Build().Run();

// Parses an optional bool deploy parameter: null when unset (leave the portal default),
// otherwise true/false.
static bool? ParseBool(string? value) =>
    string.IsNullOrEmpty(value) ? null : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

// The same, as the portal-config literal ("true"/"false") or null when unset.
static string? Flag(string? value) => ParseBool(value) is { } b ? (b ? "true" : "false") : null;
