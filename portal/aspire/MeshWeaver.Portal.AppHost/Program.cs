using MeshWeaver.Portal.AppHost.OpenTelemetryCollector;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Application storage (for tables and blobs)
var appStorage = builder.AddAzureStorage("meshweaverblobs");
if (builder.Environment.IsDevelopment())
{
    appStorage = appStorage.RunAsEmulator(
        azurite =>
        {
            azurite.WithDataBindMount("../Azurite/Data")
                .WithExternalHttpEndpoints();
        });

}

var postgres = builder
    .AddPostgres("postgres")
    .WithPgAdmin()
    .WithDataVolume()
;
var meshweaverdb = postgres.AddDatabase("meshweaverdb");

var prometheus = builder.AddContainer("prometheus", "prom/prometheus")
    .WithBindMount("../prometheus", "/etc/prometheus", isReadOnly: true)
    .WithArgs("--web.enable-otlp-receiver", "--config.file=/etc/prometheus/prometheus.yml")
    .WithHttpEndpoint(targetPort: 9090, name: "http")
    .WithExternalHttpEndpoints();

var grafana = builder.AddContainer("grafana", "grafana/grafana")
    .WithBindMount("../grafana/config", "/etc/grafana", isReadOnly: true)
    .WithBindMount("../grafana/dashboards", "/var/lib/grafana/dashboards", isReadOnly: true)
    .WithEnvironment("PROMETHEUS_ENDPOINT", prometheus.GetEndpoint("http"))
    .WithHttpEndpoint(targetPort: 3000, name: "http")
    .WithExternalHttpEndpoints();

builder.AddOpenTelemetryCollector("otelcollector", "../otelcollector/config.yaml")
    .WithEnvironment("PROMETHEUS_ENDPOINT", $"{prometheus.GetEndpoint("http")}/api/v1/otlp");

var redis = builder.AddRedis("orleans-redis");
var orleans = builder.AddOrleans("mesh")
    .WithClustering(redis)
    .WithGrainStorage("address-registry", redis)
    .WithGrainStorage("mesh-catalog", appStorage.AddTables("mesh-catalog"))
    .WithGrainStorage("activity", appStorage.AddTables("activity"));

builder.AddProject<Projects.MeshWeaver_Portal_Orleans>("silo")
    .WithReference(orleans)
    .WithReference(meshweaverdb)
    .WaitFor(redis)
    .WaitFor(meshweaverdb)
    .WithReplicas(2)
    .WithEnvironment("GRAFANA_URL", grafana.GetEndpoint("http"));


var frontend = builder.AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
    .WithExternalHttpEndpoints()
    .WithReference(orleans.AsClient())
    .WithReference(appStorage.AddBlobs("articles"))
    .WithReference(meshweaverdb)
    .WithEnvironment("GRAFANA_URL", grafana.GetEndpoint("http"))
    .WaitFor(redis)
    .WaitFor(meshweaverdb)
    ;

if (builder.Environment.IsProduction())
    frontend = frontend
        .WithEndpoint(targetPort: 443, scheme: "https", name: "https-external")
        .WithEndpoint(targetPort: 80, scheme: "http", name: "http-external")
        .WithEnvironment("ASPNETCORE_URLS", "https://+:443;http://+:80")
        .WithEnvironment("ASPNETCORE_HTTPS_PORT", "443")
        .WithEnvironment("VIRTUAL_HOST", "portal.meshweaver.cloud");

var app = builder.Build();


app.Run();
