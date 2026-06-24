using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Memex.AppHost;

/// <summary>
/// Mac dev-box extras for the LOCAL cluster (<c>mode=local</c>). Both are OPT-IN so a plain
/// <c>aspire run</c> stays fast and untouched:
/// <list type="bullet">
///   <item><c>--observability true</c> — the LGTM stack (Loki/Tempo/Prometheus/Grafana + an OpenTelemetry
///   Collector). The portal + db-migration already emit OTLP via
///   <c>ServiceDefaults.ConfigureOpenTelemetry()</c>; this routes that OTLP into the collector, which fans
///   it out to Loki (logs), Tempo (traces) and Prometheus (metrics), all viewable in Grafana.</item>
///   <item><c>--localai true</c> — points the portal's <c>OpenAICompatible</c> provider at <b>native host
///   Ollama</b> (<c>http://localhost:11434/v1</c>, model <c>qwen3-coder:30b</c>). Native, NOT a container:
///   Docker on macOS has no Metal GPU passthrough, so a containerized Ollama would run on the CPU — the
///   portal is a host process in local mode, so it reaches the host's GPU-accelerated Ollama directly.</item>
/// </list>
/// Full rationale + run steps: <c>Doc/Architecture/MacLocalStack</c>.
/// </summary>
internal static class MemexLocalStack
{
    public static void AddMacLocalStack(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> portal,
        IResourceBuilder<ProjectResource> dbMigration)
    {
        if (IsOn(builder, "observability"))
            AddObservability(builder, portal, dbMigration);
        if (IsOn(builder, "localai"))
            AddLocalAi(portal);
    }

    private static bool IsOn(IDistributedApplicationBuilder builder, string key) =>
        (builder.Configuration[key] ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);

    private static void AddObservability(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> portal,
        IResourceBuilder<ProjectResource> dbMigration)
    {
        // Backends. They talk to each other on the Aspire container network by resource name (loki / tempo
        // / prometheus on their container ports). Persistent volumes keep history across restarts.
        var loki = builder.AddContainer("loki", "grafana/loki", "3.3.2")
            .WithBindMount("observability/loki.yaml", "/etc/loki/local-config.yaml", isReadOnly: true)
            .WithArgs("-config.file=/etc/loki/local-config.yaml")
            .WithVolume("memex-loki", "/loki")
            .WithHttpEndpoint(targetPort: 3100, name: "http")
            .WithLifetime(ContainerLifetime.Persistent);

        var tempo = builder.AddContainer("tempo", "grafana/tempo", "2.6.1")
            .WithBindMount("observability/tempo.yaml", "/etc/tempo.yaml", isReadOnly: true)
            .WithArgs("-config.file=/etc/tempo.yaml")
            .WithVolume("memex-tempo", "/var/tempo")
            .WithHttpEndpoint(targetPort: 3200, name: "http")
            .WithLifetime(ContainerLifetime.Persistent);

        var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "v3.1.0")
            .WithBindMount("observability/prometheus.yaml", "/etc/prometheus/prometheus.yml", isReadOnly: true)
            .WithArgs(
                "--config.file=/etc/prometheus/prometheus.yml",
                "--web.enable-remote-write-receiver",
                "--enable-feature=otlp-write-receiver")
            .WithVolume("memex-prometheus", "/prometheus")
            .WithHttpEndpoint(targetPort: 9090, name: "http")
            .WithLifetime(ContainerLifetime.Persistent);

        var collector = builder.AddContainer("otel-collector", "otel/opentelemetry-collector-contrib", "0.116.1")
            .WithBindMount("observability/otelcol-config.yaml", "/etc/otelcol-contrib/config.yaml", isReadOnly: true)
            .WithHttpEndpoint(targetPort: 4318, name: "otlp-http")
            .WaitFor(loki).WaitFor(tempo).WaitFor(prometheus);

        builder.AddContainer("grafana", "grafana/grafana", "11.4.0")
            .WithBindMount("observability/grafana", "/etc/grafana/provisioning/datasources", isReadOnly: true)
            .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
            .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Admin")
            .WithEnvironment("GF_AUTH_DISABLE_LOGIN_FORM", "true")
            .WithEnvironment("GF_FEATURE_TOGGLES_ENABLE", "traceqlEditor")
            .WithHttpEndpoint(targetPort: 3000, name: "http")
            .WithLifetime(ContainerLifetime.Persistent)
            .WaitFor(loki).WaitFor(tempo).WaitFor(prometheus);

        // Route the .NET services' OTLP to the collector (OTLP/HTTP — proxy-friendlier than gRPC). They
        // already emit via ServiceDefaults; this overrides the default (Aspire-dashboard) endpoint so the
        // telemetry also lands in Grafana. Opt-in, so the dashboard's own view is untouched by default.
        var otlp = collector.GetEndpoint("otlp-http");
        portal
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlp)
            .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
            .WaitFor(collector);
        dbMigration
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlp)
            .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
    }

    private static void AddLocalAi(IResourceBuilder<ProjectResource> portal)
    {
        // NATIVE host Ollama (Metal GPU). Do NOT containerize — Docker-on-macOS has no GPU passthrough.
        // The portal already registers AddOpenAICompatible() behind this feature flag; BuiltInLanguageModel-
        // Provider reads the Endpoint + Models from these config keys and emits the model node.
        portal
            .WithEnvironment("Features__Ai__Providers__OpenAICompatible", "true")
            .WithEnvironment("OpenAICompatible__Endpoint", "http://localhost:11434/v1")
            .WithEnvironment("OpenAICompatible__ApiKey", "ollama")    // Ollama ignores it; factory needs non-empty
            .WithEnvironment("OpenAICompatible__Models__0", "qwen3-coder:30b");
    }
}
