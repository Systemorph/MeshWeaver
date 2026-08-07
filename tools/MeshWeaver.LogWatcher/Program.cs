using MeshWeaver.LogWatcher;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// mw-log-watcher — the red-log detector.
//
// Polls Loki for fail:/crit: lines in the watched namespaces, groups them into fingerprinted
// bursts, and reports each one to the portal's /api/log-incidents endpoint. The portal triages
// new fingerprints with an agent and opens the GitHub issue.
//
// It runs OUTSIDE the portal, in the cluster's monitoring namespace, precisely so that noticing
// "the portal is throwing errors" does not depend on the portal being healthy.
// See Doc/Architecture/LogWatchTriage.md.

var builder = Host.CreateApplicationBuilder(args);

var options = builder.Configuration.GetSection(LogWatcherOptions.SectionName).Get<LogWatcherOptions>()
              ?? new LogWatcherOptions();
builder.Services.AddSingleton(options);

// One I/O pool for every leaf (Loki HTTP, portal HTTP, state files) — the same bounded-off-the-
// scheduler discipline the portal uses (Doc/Architecture/ControlledIoPooling.md).
builder.Services.AddSingleton<IIoPool>(_ => new IoPool(maxConcurrency: 4));

builder.Services.AddHttpClient<LokiClient>(client =>
{
    client.BaseAddress = new Uri(options.LokiUrl);
    // A poll that cannot finish inside its own interval is a failure, not something to wait out.
    client.Timeout = TimeSpan.FromSeconds(45);
});

builder.Services.AddHttpClient<IncidentReporter>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<WatcherState>();
builder.Services.AddHostedService<LogWatchWorker>();

await builder.Build().RunAsync();
