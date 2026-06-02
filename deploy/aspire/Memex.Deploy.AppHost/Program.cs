// Dedicated, image-based deployment AppHost for the MeshWeaver Memex portal.
//
// Mirrors the conventions of the main memex/aspire/Memex.AppHost, but deploys the PUBLISHED
// GHCR images via the Aspire.Hosting.Memex integration (builder.AddMemex → AddContainer) rather
// than building the portal from source. One model → many artifacts via the Aspire publishers:
//
//   aspire publish --apphost deploy/aspire/Memex.Deploy.AppHost/Memex.Deploy.AppHost.csproj \
//       -o deploy/compose -- --mode compose          # Docker Compose (single)
//   aspire publish ... -o deploy/compose-ha -- --mode compose-ha     # Docker Compose (HA)
//   aspire publish ... -o deploy/helm    -- --mode kubernetes        # Kubernetes / Helm
//   aspire publish ... -o deploy/aca     -- --mode azure             # Azure Container Apps (bicep)
//
// Tunables (dotnet user-secrets / env / GitHub secrets), all optional:
//   Parameters:image-registry  (default ghcr.io/systemorph)
//   Parameters:image-tag       (default latest)
//   Parameters:include-ai-clis (default true → portal-ai image)
//   Parameters:key-protection-master-key  (REQUIRED for production)

var builder = DistributedApplication.CreateBuilder(args);

var mode = builder.Configuration["mode"]?.ToLowerInvariant() ?? "compose";
var ha = mode.EndsWith("-ha", StringComparison.Ordinal);

if (mode.StartsWith("kubernetes", StringComparison.Ordinal))
{
    builder.AddKubernetesEnvironment("k8s")
        .WithHelm(helm => helm
            .WithChartName("memex")
            .WithChartDescription("MeshWeaver Memex portal — Azure-free Kubernetes self-host."));
}
else if (mode == "azure")
{
    builder.AddAzureContainerAppEnvironment("memex-aca");
}
else
{
    builder.AddDockerComposeEnvironment("self-host");
}

var portal = builder.AddMemex("memex", o => o
    .WithImage(
        builder.Configuration["Parameters:image-registry"],
        builder.Configuration["Parameters:image-tag"])
    .WithAiClis(!string.Equals(builder.Configuration["Parameters:include-ai-clis"], "false",
        StringComparison.OrdinalIgnoreCase))
    .WithBackend("Filesystem")
    // Real, Postgres-backed cluster membership in every deployment (never Localhost in prod).
    // Works for a single silo or an HA replica set; the `ha` flag only drives replica count.
    .WithOrleansClustering("AdoNet")
    .WithMasterKey(builder.Configuration["Parameters:key-protection-master-key"])

    // Embeddings (vector search). With the endpoint + key set, the one-shot migration
    // vector-indexes the built-in documentation and the portal embeds search-bar queries.
    // Leave unset to ship docs as full-text-searchable only (no external AI dependency).
    .WithEmbeddings(
        builder.Configuration["Parameters:embedding-endpoint"],
        builder.Configuration["Parameters:embedding-key"],
        builder.Configuration["Parameters:embedding-model"])

    // External sign-in (OAuth) providers — deploy parameters. Provide via
    // `dotnet user-secrets` / env / GitHub secrets locally, or the Marketplace
    // createUiDefinition wizard for an Azure Application install. Each provider is
    // offered only when its ClientId is set. Register the redirect URI on each app:
    // {BaseUrl}/signin-{microsoft|google|linkedin}.
    .WithMicrosoftSignIn(
        builder.Configuration["Parameters:microsoft-client-id"],
        builder.Configuration["Parameters:microsoft-client-secret"],
        builder.Configuration["Parameters:microsoft-tenant-id"])
    .WithGoogleSignIn(
        builder.Configuration["Parameters:google-client-id"],
        builder.Configuration["Parameters:google-client-secret"])
    .WithLinkedIn(
        builder.Configuration["Parameters:linkedin-client-id"],
        builder.Configuration["Parameters:linkedin-client-secret"])

    // Outbound email (Microsoft Graph /sendMail) — invitations + script-triggered notifications.
    // On AKS the client secret comes from Key Vault (email-clientsecret → Email__ClientSecret via
    // the SecretProviderClass), so it is NOT passed here; the rest are non-secret parameters.
    .WithOutboundEmail(
        enabled: ParseBool(builder.Configuration["Parameters:email-enabled"]),
        mailboxAddress: builder.Configuration["Parameters:email-mailbox-address"],
        tenantId: builder.Configuration["Parameters:email-tenant-id"],
        clientId: builder.Configuration["Parameters:email-client-id"],
        clientSecret: builder.Configuration["Parameters:email-client-secret"],
        useManagedIdentity: ParseBool(builder.Configuration["Parameters:email-use-managed-identity"]))

    // Inbound email→agent channel (Graph subscription + webhook). Needs Mail.ReadWrite + a public URL.
    .WithInboundEmail(
        enabled: ParseBool(builder.Configuration["Parameters:email-inbound-enabled"]),
        webhookBaseUrl: builder.Configuration["Parameters:email-webhook-base-url"],
        clientState: builder.Configuration["Parameters:email-subscription-client-state"])

    // Invitation-only onboarding (Features:Onboarding:InvitationOnly).
    .WithInvitationOnly(ParseBool(builder.Configuration["Parameters:invitation-only"])));

// Self-host filesystem backend: the portal writes DataProtection keys, the NodeType
// assembly cache, and the NuGet cache under /data. The aspnet base image runs as the
// non-root `app` user, but a freshly-created Docker named volume is root-owned, so the
// app cannot create those directories — startup dies with
// `UnauthorizedAccessException: Access to the path '/data/dataprotection-keys' is denied`.
// Run the portal as root in the Compose targets so it owns its mounted data volume.
// (Kubernetes/AKS handles this via the platform overlay — Azure Files CSI mounts 0777 /
// uid-mapped — and ACA runs containers as root by default, so this is Compose-only.)
if (!mode.StartsWith("kubernetes", StringComparison.Ordinal) && mode != "azure")
{
    portal.PublishAsDockerComposeService((_, service) => service.User = "root");
}

builder.Build().Run();

// Parses an optional bool deploy parameter: null when unset (leave the portal default),
// otherwise true/false. Hoisted local function — usable from the AddMemex lambda above.
static bool? ParseBool(string? value) =>
    string.IsNullOrEmpty(value) ? null : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
