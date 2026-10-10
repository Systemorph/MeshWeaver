# MeshWeaver.Hosting.Orleans

## Overview
MeshWeaver.Hosting.Orleans provides a distributed hosting model for MeshWeaver using Microsoft Orleans. Each message hub is represented as a virtual actor (grain) in the Orleans cluster, enabling automatic distribution, scalability, and fault tolerance.

## How It Works
- Each message hub is mapped to an Orleans grain
- Messages are dispatched through Orleans silos
- Grains are automatically distributed across the cluster
- Virtual actor model ensures hubs are always addressable
- Orleans handles activation/deactivation and placement of hubs

## Usage
```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Orleans cluster
builder.Host.UseOrleans(orleans =>
{
    orleans.UseLocalhostClustering();
    // Configure other Orleans options
});

// Configure MeshWeaver with Orleans hosting
builder.UseMeshWeaver(
    new MeshAddress(),
    config => config
        .ConfigureWebPortal()
        .ConfigurePortalMesh()
        .UseOrleansMesh()
        .ConfigureServices(services => services.AddArticles())
);

var app = builder.Build();
app.StartPortalApplication();
```

## Features
- Distributed message processing
- Automatic scalability through Orleans clustering
- Fault tolerance and automatic recovery
- Virtual actor model for message hubs
- Transparent hub activation/deactivation
- Location transparency for message routing

## Benefits
- **Scalability**: Automatically scales across multiple servers
- **Reliability**: Built-in fault tolerance through Orleans
- **Persistence**: Optional state persistence for hubs
- **Distribution**: Automatic workload distribution
- **Recovery**: Automatic failure recovery

## Integration
- Built on [MeshWeaver.Hosting](../MeshWeaver.Hosting/README.md)
- Uses [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/overview) for distribution
- Compatible with all mesh message patterns

## Silo departure and cancellation timeouts (#6392, log fingerprint `97b80f6a91dbdb74`)

`Orleans.Runtime.GrainCallCancellationManager` logs `System.TimeoutException` ("Error while cancelling N requests to S...") when a peer's cancellation batch to a silo's `sys.svc.canceler` gets no answer within the 30 s `ResponseTimeout`. An answer, or a refusal because the target is already known `Dead`, would return in milliseconds; a full 30 s timeout means the target was neither answering nor yet known `Dead` to the sender, and every batch queued in that window costs every peer one 30 s timeout.

What the repo shows (read from the code; not yet confirmed by the target silos' own logs):

- A silo stop here chains three independent bounded holds: `RoutingQuiescenceSiloParticipant` (stage `Active`, 30 s), `IoPoolSiloTeardown` (stage `First`, stops last, 30 s) and `MeshTeardownHostedService.StoppedAsync` (30 s). The last one's remarks record 55-61 s shutdowns measured on memex-cloud.
- A stop longer than the host's `HostOptions.ShutdownTimeout` or the pod's `terminationGracePeriodSeconds` (30 s each unless configured) is cut mid-stop, so the process leaves while its peers still list it as non-`Dead`. That is the shape of the incident: one silo unreachable from several pods for more than a minute, again with every new silo identity (2026-10-07 09:54 against `10.244.2.27`, 2026-10-09 20:39 against `10.244.17.110`).

Not yet shown: the departing pods' stop-line timeline and the effective `ShutdownTimeout` / grace period (deploy configuration, outside this project). Until then this is a hypothesis; the probe is the stop-line timeline of the departing pod next to the fingerprint's timestamps.

## See Also
- [Orleans Documentation](https://learn.microsoft.com/en-us/dotnet/orleans/overview) - Learn more about the Orleans virtual actor model
- [Main MeshWeaver Documentation](../../Readme.md) - More about MeshWeaver hosting options
