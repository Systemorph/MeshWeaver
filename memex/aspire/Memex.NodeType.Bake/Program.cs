using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// NODETYPE BAKE — compile every dynamic NodeType against THIS image, into the shared assembly
// cache, BEFORE the portal rolls onto it. Runs as its own container, exactly like db-migration.
//
// WHY A SEPARATE SERVICE. Every image roll changes the framework identity (Graph's MVID), and that
// identity is baked into the assembly store's filename AND its lookup glob — so a new image misses
// the whole cache BY DESIGN (ABI safety). Left to the portal, that guaranteed miss is paid lazily,
// unordered, on user requests, after the pod is already serving. Paid here, it is paid once, on a
// container nobody is waiting for, and a NodeType that no longer compiles is discovered BEFORE any
// traffic moves — the same contract db-migration gives the schema.
//
// WHY IT DOES NOT JOIN THE CLUSTER. UseMonolithMesh, never UseOrleansMeshServer: this process must
// compile and upload assemblies without taking grain ownership from the pods currently serving
// production, and without vanishing mid-activation when it exits.
//
// WHY THIS IS SAFE TO RUN WHILE THE OLD IMAGE SERVES. The store key carries the framework tag, so
// old- and new-image assemblies COEXIST — this writes files the running pods never read. The one
// hazard was the compile write-back stamping CompiledFrameworkVersion onto the shared NodeType
// record: the live pods would see a mismatch, fire the framework-stale kickoff and recompile back,
// storming production. That kickoff now probes the store first and skips when bytes for its own
// framework exist, so the stamp is inert to them.
//
// EXIT CODE IS THE GATE. 0 = every type that was healthy going in has a usable build on the share.
// 1 = a REGRESSION: something that used to compile no longer does. Deploy on 0, hold on 1.
// A type that was ALREADY broken before this image cannot fail the bake — it is broken in
// production right now, and blocking every future rollout on it would freeze deploys entirely.
// ─────────────────────────────────────────────────────────────────────────────────────────────

Console.WriteLine("[Bake] Starting NodeType bake...");

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("memex") ?? "";
if (connectionString.Contains("database.azure.com"))
    builder.AddAzureNpgsqlDataSource("memex", configureDataSourceBuilder: dsb => dsb.UseVector());
else
    builder.AddNpgsqlDataSource("memex", configureDataSourceBuilder: dsb => dsb.UseVector());

// The shared volume the portal reads its assemblies from. Baking anywhere else would be a no-op
// with a green exit code — the worst possible outcome for a gate — so this is required, not
// defaulted: a bake with nowhere durable to write is a configuration error, and it fails loudly.
var dataRoot = builder.Configuration["Deployment:DataRoot"];
if (string.IsNullOrWhiteSpace(dataRoot))
{
    Console.Error.WriteLine(
        "[Bake] Deployment__DataRoot is not set. The bake would compile into a container-local "
        + "directory that dies with this pod, and report success. Refusing to run.");
    return 2;
}
var assemblyCache = Path.Combine(dataRoot, "assembly-cache");

// Block until the database migration has recorded a db_version.
//
// 🚨 The bake and the migration are separate Jobs with NO ordering between them, and both only gate
// on Postgres accepting TCP. Racing them is not a slow start — it is a SILENT PASS: the mesh comes
// up against an unmigrated database, the NodeType query returns nothing, and the bake reports
// "0 of 0 types" and exits 0 in a tenth of a second, while the portal goes on to compile everything
// lazily. Observed on the first real run (bake-test, 2026-07-28): four NodeTypes compiled in the
// portal AFTER a green bake.
//
// Same condition the portal's own DbVersionGate enforces, so the two cannot disagree about what
// "migrated" means.
static async Task<bool> WaitForMigratedDatabase(NpgsqlDataSource source, TimeSpan budget, ILogger log)
{
    var deadline = DateTimeOffset.UtcNow + budget;
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            await using var cmd = source.CreateCommand("""
                SELECT (content->>'Version')::int FROM admin.mesh_nodes
                 WHERE id = 'db_version' AND namespace = '' LIMIT 1
                """);
            if ((await cmd.ExecuteScalarAsync()) switch { int v => v, long l => (int)l, _ => 0 } is > 0 and var version)
            {
                log.LogInformation("Database is migrated (db_version={Version})", version);
                return true;
            }
            log.LogInformation("Waiting for the migration to record a db_version...");
        }
        catch (Exception ex)
        {
            // The admin schema may not exist yet — that IS the state we are waiting out.
            log.LogInformation("Waiting for the database schema ({Error}: {Message})",
                ex.GetType().Name, ex.Message);
        }
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
    return false;
}

builder.UseMeshWeaver(
    AddressExtensions.CreateMeshAddress(),
    mesh => mesh
        .UseMonolithMesh()
        .ConfigureServices(services => services
            .AddPartitionedPostgreSqlPersistence()
            .AddFileSystemAssemblyStore(assemblyCache)
            // Same lease the portal takes: if a pod is already baking this framework, follow its
            // work rather than compiling the same types into the same volume alongside it.
            .AddSingleton(new BakeCoordination(assemblyCache))
            .AddNodeTypeBakeGate())
        // The portal's own mesh configuration — the NodeType registrations and content wiring have
        // to match, or this would compile a different world than the one that will serve.
        .ConfigureMemexMesh(builder.Configuration));

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bake");

// Before StartAsync — resolving a singleton does not start hosted services, so the mesh never comes
// up against an unmigrated schema in the first place.
if (!await WaitForMigratedDatabase(
        host.Services.GetRequiredService<NpgsqlDataSource>(), TimeSpan.FromMinutes(15), logger))
{
    logger.LogCritical(
        "Database still not migrated after 15 minutes. Refusing to bake — an unmigrated database "
        + "yields an EMPTY NodeType set, which would look exactly like a clean bake.");
    return 2;
}

await host.StartAsync();

var exitCode = 0;
try
{
    var meshHub = host.Services.GetRequiredService<IMessageHub>();
    var startedAt = DateTimeOffset.UtcNow;
    logger.LogInformation("Bake starting — compiling dynamic NodeTypes into {Cache}", assemblyCache);

    var outcomes = await DynamicTypePreWarmer
        .WarmDynamicTypes(meshHub, logger)
        .ToList()
        .ToTask();

    // A regression is a type that WAS healthy going in and did not reach a usable build. Anything
    // already broken beforehand is reported and ignored — see the header.
    var regressions = outcomes
        .Where(o => !o.ReachedUsableBuild && o.WasHealthyBeforeBake)
        .ToList();
    var preexisting = outcomes
        .Where(o => !o.ReachedUsableBuild && !o.WasHealthyBeforeBake)
        .ToList();

    logger.LogInformation(
        "Bake finished in {Elapsed} — {Total} type(s): compiled={Compiled} alreadyBaked={Baked} "
        + "regressions={Regressions} preExistingFailures={PreExisting}",
        DateTimeOffset.UtcNow - startedAt, outcomes.Count,
        outcomes.Count(o => o.Status == PreWarmStatus.Compiled),
        outcomes.Count(o => o.Status == PreWarmStatus.AlreadyBaked),
        regressions.Count, preexisting.Count);

    foreach (var stale in preexisting)
        logger.LogWarning(
            "Bake: {TypePath} did not build ({Status}: {Detail}) — but it was ALREADY broken before "
            + "this image, so it does not fail the bake",
            stale.TypePath, stale.Status, stale.Detail);

    // 🚨 FINDING NOTHING IS NOT PASSING. An empty outcome set means the enumeration returned no
    // dynamic NodeTypes — and that is indistinguishable from the ways it FAILS: an unmigrated or
    // wrong database, a query that timed out (WarmDynamicTypes catches enumeration errors and
    // completes empty, by design, because in the portal a failed warm must not be fatal), a mesh
    // that has not finished seeding. A gate that certifies "I verified nothing" is worse than no
    // gate, because the rollout proceeds with a green light nobody earned.
    //
    // Real deployments always have dynamic NodeTypes. A genuinely empty mesh must say so explicitly.
    var allowEmpty = bool.TryParse(builder.Configuration["Bake:AllowEmpty"], out var parsed) && parsed;
    if (outcomes.Count == 0 && !allowEmpty)
    {
        logger.LogCritical(
            "Bake found NO dynamic NodeTypes. Refusing to report success: an empty result is what a "
            + "failed enumeration, an unseeded mesh and a wrong database all look like. If this mesh "
            + "genuinely has none, set Bake__AllowEmpty=true.");
        exitCode = 3;
    }
    else if (regressions.Count > 0)
    {
        exitCode = 1;
        foreach (var regression in regressions)
            logger.LogCritical(
                "Bake REGRESSION: {TypePath} used to have a usable build and no longer compiles "
                + "against this image — {Status}: {Detail}",
                regression.TypePath, regression.Status, regression.Detail);
        logger.LogCritical(
            "Bake FAILED with {Count} regression(s) — do NOT roll the portal onto this image",
            regressions.Count);
    }
}
catch (Exception ex)
{
    // An unprovable bake must not read as a green one.
    logger.LogCritical(ex, "Bake failed unexpectedly — treating as a failed bake");
    exitCode = 1;
}
finally
{
    await host.StopAsync();
}

Console.WriteLine($"[Bake] Exiting with code {exitCode}");
return exitCode;
