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

builder.AddMemex("memex", o =>
{
    o.ImageRegistry = builder.Configuration["Parameters:image-registry"] ?? "ghcr.io/systemorph";
    o.ImageTag = builder.Configuration["Parameters:image-tag"] ?? "latest";
    o.IncludeAiClis = !string.Equals(builder.Configuration["Parameters:include-ai-clis"], "false",
        StringComparison.OrdinalIgnoreCase);
    o.Backend = "Filesystem";
    o.OrleansClustering = ha ? "AdoNet" : "Localhost";
    o.MasterKey = builder.Configuration["Parameters:key-protection-master-key"];
});

builder.Build().Run();
