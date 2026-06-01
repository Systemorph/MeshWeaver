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

var portal = builder.AddMemex("memex", o =>
{
    o.ImageRegistry = builder.Configuration["Parameters:image-registry"] ?? "ghcr.io/systemorph";
    o.ImageTag = builder.Configuration["Parameters:image-tag"] ?? "latest";
    o.IncludeAiClis = !string.Equals(builder.Configuration["Parameters:include-ai-clis"], "false",
        StringComparison.OrdinalIgnoreCase);
    o.Backend = "Filesystem";
    o.OrleansClustering = ha ? "AdoNet" : "Localhost";
    o.MasterKey = builder.Configuration["Parameters:key-protection-master-key"];

    // Embeddings (vector search). With the endpoint + key set, the one-shot migration
    // vector-indexes the built-in documentation and the portal embeds search-bar queries.
    // Leave unset to ship docs as full-text-searchable only (no external AI dependency).
    o.EmbeddingEndpoint = builder.Configuration["Parameters:embedding-endpoint"];
    o.EmbeddingApiKey = builder.Configuration["Parameters:embedding-key"];
    var embeddingModel = builder.Configuration["Parameters:embedding-model"];
    if (!string.IsNullOrEmpty(embeddingModel))
        o.EmbeddingModel = embeddingModel;

    // External sign-in (OAuth) providers — deploy parameters. Provide via
    // `dotnet user-secrets` / env / GitHub secrets locally, or the Marketplace
    // createUiDefinition wizard for an Azure Application install. Each provider is
    // offered only when its ClientId is set. Register the redirect URI on each app:
    // {BaseUrl}/signin-{microsoft|google|linkedin}.
    o.MicrosoftClientId = builder.Configuration["Parameters:microsoft-client-id"];
    o.MicrosoftClientSecret = builder.Configuration["Parameters:microsoft-client-secret"];
    o.MicrosoftTenantId = builder.Configuration["Parameters:microsoft-tenant-id"];
    o.GoogleClientId = builder.Configuration["Parameters:google-client-id"];
    o.GoogleClientSecret = builder.Configuration["Parameters:google-client-secret"];
    o.LinkedInClientId = builder.Configuration["Parameters:linkedin-client-id"];
    o.LinkedInClientSecret = builder.Configuration["Parameters:linkedin-client-secret"];
});

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
