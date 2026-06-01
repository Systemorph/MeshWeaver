using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Aspire hosting integration for the MeshWeaver Memex portal. A single <c>builder.AddMemex()</c>
/// wires the full runnable topology — Postgres (pgvector), a one-shot DB migration that gates
/// portal startup, and the portal itself — from published GHCR images, so ANY AppHost can add
/// Memex as one participant and then generate Docker Compose / Kubernetes-Helm / Azure-ACA
/// artifacts with the standard Aspire publishers. Object storage, the NodeType compile cache,
/// the NuGet cache, and DataProtection keys live on mounted volumes (the filesystem backend);
/// mesh data lives in the Postgres database.
/// </summary>
public static class MemexHostingExtensions
{
    /// <summary>
    /// Adds the Memex portal — plus its Postgres database and one-shot migration — to the
    /// application model. Returns the portal container resource so the caller can layer on
    /// deployment-specific configuration (LLM provider keys, OAuth secrets, scaling, etc.).
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">Resource name prefix (default <c>memex</c>). The portal resource takes this exact name.</param>
    /// <param name="configure">Optional <see cref="MemexOptions"/> customization.</param>
    public static IResourceBuilder<ContainerResource> AddMemex(
        this IDistributedApplicationBuilder builder,
        string name = "memex",
        Action<MemexOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var options = new MemexOptions();
        configure?.Invoke(options);

        var registry = options.ImageRegistry.TrimEnd('/');
        var tag = options.ImageTag;
        var portalRepo = options.IncludeAiClis ? "memex-portal-ai" : "memex-portal";

        // --- Postgres (pgvector) — mesh data, in every topology ---
        var postgres = builder.AddPostgres($"{name}-postgres")
            .WithImage("pgvector/pgvector", "pg17")
            .WithDataVolume($"{name}-pgdata");
        var db = postgres.AddDatabase("memex");

        // --- One-shot DB migration; the portal waits for it to complete (mirrors DbVersionGate) ---
        // The migration also mirrors the built-in documentation into the `doc` Postgres schema for
        // search; with the embedding endpoint/key set it vector-indexes them too.
        var migration = builder.AddContainer($"{name}-migration", $"{registry}/memex-migration", tag)
            .WithReference(db)
            .WaitFor(db);

        foreach (var kv in options.EmbeddingEnvironment())
            migration.WithEnvironment(kv.Key, kv.Value);

        // --- Portal (co-hosted Orleans silo + Blazor web) ---
        // Resource name is "{name}-portal" so it never collides with the "memex" database
        // resource that AddDatabase("memex") creates (Aspire resource names are case-insensitive
        // and must be unique). The DB resource name stays "memex" because WithReference injects
        // ConnectionStrings__memex, which the portal reads as ConnectionStrings:memex.
        var portal = builder.AddContainer($"{name}-portal", $"{registry}/{portalRepo}", tag)
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithExternalHttpEndpoints()
            .WithReference(db)
            .WaitFor(db)
            .WaitForCompletion(migration)
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", "8080")
            // Backend axis (Phase-0 switch in Memex.Portal.Distributed/Program.cs).
            .WithEnvironment("Deployment__Backend", options.Backend)
            .WithEnvironment("Deployment__DataRoot", "/data")
            .WithEnvironment("Deployment__Orleans__Clustering", options.OrleansClustering)
            // Content storage + graph base paths (filesystem backend).
            .WithEnvironment("Storage__Name", "content")
            .WithEnvironment("Storage__SourceType", "FileSystem")
            .WithEnvironment("Storage__BasePath", "/data/content")
            .WithEnvironment("Graph__Storage__Type", "PostgreSql")
            .WithEnvironment("Graph__Storage__BasePath", "/data/graph")
            // Object storage / NodeType compile cache / NuGet cache / DataProtection keys.
            .WithVolume($"{name}-data", "/data")
            // Per-user co-hosted-CLI config (.claude / copilot) — a shared volume in HA.
            .WithVolume($"{name}-users", "/mnt/users");

        if (!string.IsNullOrEmpty(options.MasterKey))
            portal.WithEnvironment("Ai__KeyProtection__MasterKey", options.MasterKey);

        // Observability: export OTLP traces/metrics to a collector when one is configured.
        // Logs are scraped from container stdout by the cluster log agent (Promtail), so this
        // only wires the OTLP push path; ServiceDefaults no-ops the exporter when it's unset.
        if (!string.IsNullOrEmpty(options.OtlpEndpoint))
            portal.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", options.OtlpEndpoint);

        // Embeddings — the portal embeds search-bar queries so they hit the HNSW index that the
        // migration populated. Same config flows to both so the vector dimensions line up.
        foreach (var kv in options.EmbeddingEnvironment())
            portal.WithEnvironment(kv.Key, kv.Value);

        foreach (var kv in options.FeatureEnvironment())
            portal.WithEnvironment(kv.Key, kv.Value);

        // External sign-in (OAuth) providers — only the ones whose ClientId is set.
        foreach (var kv in options.AuthEnvironment())
            portal.WithEnvironment(kv.Key, kv.Value);

        // MCP back-connection base URL for the co-hosted CLIs ({BaseUrl}/mcp). Defaults to the
        // portal's own allocated external endpoint (Aspire substitutes the real URL at publish).
        if (!string.IsNullOrEmpty(options.BaseUrl))
            portal.WithEnvironment("Mcp__BaseUrl", options.BaseUrl);
        else
            portal.WithEnvironment("Mcp__BaseUrl", portal.GetEndpoint("http"));

        return portal;
    }
}
