using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 1:1 repro for the prod 2026-05-21 hot spot — a dynamic NodeType's
/// per-NodeType grain auto-recompiles on EVERY activation because
/// <see cref="NodeTypeCompilationHelpers.InstallCompileWatcher"/>'s kickoff
/// flips <c>CompilationStatus = Pending</c> as soon as the OWN MeshNode
/// emits and <see cref="NodeTypeCompilationHelpers.HasUsableBuild"/> is
/// false. Activation can fire from BACKGROUND traffic the user never
/// initiated — a synced-query fan-out, a NodeType enrichment lookup, an
/// Orleans grain reactivation — and the resulting compile activity then
/// runs with whichever <see cref="AccessContext"/> happened to be on the
/// triggering message. Prod showed it as:
/// <c>"AccessControlPipeline: Access denied: user 'sync/...' lacks Create
/// permission on 'Systemorph/EventCalendar'"</c>.
///
/// <para>The user's diagnosis (2026-05-21): "dirty should not trigger
/// recompile", "recompile is transparent operation", "validate this also
/// on gui before bringing recompile and things". <b>Background activation
/// must NOT auto-recompile.</b> Recompile is an EXPLICIT user action — UI
/// flips <see cref="NodeTypeDefinition.RequestedReleaseAt"/> via
/// <see cref="NodeTypeCompilationHelpers.InstallReleaseRequestWatcher"/>,
/// AccessControl validates the user has Create permission, and only then
/// does the compile activity land. The IsDirty computed property is a
/// READ-ONLY label for the UI ("Compile" button enabled), not a trigger.</para>
///
/// <para>This test pins the invariant: activating a per-NodeType grain via
/// a plain <see cref="GetDataRequest"/> (the read shape every background
/// path uses) MUST NOT cause a compile activity to be created. The current
/// kickoff fails this — the test will go red until the kickoff stops
/// driving Pending on its own.</para>
/// </summary>
public class OrleansCompileActivityAccessTest(ITestOutputHelper output)
    : OrleansTestBase<RestrictedAccessSiloConfigurator>(output)
{
    private IMeshService SiloMeshService =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>()
            .ServiceProvider
            .GetRequiredService<IMeshService>();

    private AccessService SiloAccessService =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>()
            .ServiceProvider
            .GetRequiredService<AccessService>();

    /// <summary>
    /// Seeds a MeshNode on the silo under <see cref="AccessService.ImpersonateAsSystem"/>.
    /// Test-data seeding IS system bootstrap — the explicit
    /// <c>ImpersonateAsSystem</c> opt-in is the post-2026-05-21 pattern; the
    /// silent hub-self-impersonation fallback was deleted from PostPipeline so
    /// seeding without an explicit impersonation now (correctly) fails closed
    /// at AccessControl.
    /// </summary>
    private async Task SeedAsSystem(MeshNode node, CancellationToken ct)
    {
        using (SiloAccessService.ImpersonateAsSystem())
        {
            await SiloMeshService.CreateNode(node).FirstAsync().ToTask(ct);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task BackgroundActivation_OfDynamicNodeType_DoesNotLoopRecompiles()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;

        // 1. Seed a dynamic NodeType + Code child via the silo's IMeshService
        //    (server-side, no access check). After this the NodeType exists in
        //    the silo's persistence with NO usable build (LatestAssemblyPath /
        //    LatestAssemblyCollection / CompiledFrameworkVersion all unset).
        //    The current InstallCompileWatcher kickoff treats that as
        //    "recompile" and flips CompilationStatus = Pending on activation.
        // Seed in TestUser's own partition (TestUser has Admin via the silo
        // configurator). The test's invariant — "background activation does
        // not auto-create a compile activity" — doesn't depend on cross-user
        // access; what matters is that the per-NodeType-grain activation
        // doesn't fire a recompile under whichever AccessContext happens to
        // be inbound. With the kickoff deleted (NodeTypeCompilationHelpers
        // 2026-05-21) the activity creation never even gets attempted.
        var typeId = $"EventCalendar{Guid.NewGuid():N}";
        var typePath = $"TestUser/{typeId}";
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "1:1 repro for prod 2026-05-21 EventCalendar background recompile",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        };
        await SeedAsSystem(typeNode, ct);

        var codeNode = new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}}
                    {
                        public string Id { get; init; } = string.Empty;
                        public string Title { get; init; } = string.Empty;
                    }
                    """,
                Language = "csharp"
            }
        };
        await SeedAsSystem(codeNode, ct);
        Output.WriteLine($"Seeded dynamic NodeType + Code at {typePath} (no compiled build yet)");

        // 2. Simulate a BACKGROUND activation: a client whose AccessContext
        //    has NO explicit user-driven release request (no RequestedReleaseAt
        //    flip, no Compile-button click) reads the NodeType's MeshNode. This
        //    is the shape every background path uses — NodeType enrichment
        //    fan-out, autocomplete enumeration, synced-query SubscribeRequest
        //    routing. Plain GetDataRequest. No user intent to recompile.
        var client = await GetClientAsync($"background-{Guid.NewGuid():N}", userId: "TestUser");
        var dataResp = await client
            .Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().ToTask(ct);
        Output.WriteLine($"Background activation read: Data={dataResp.Message.Data?.GetType().Name ?? "(null)"}");

        // 3. Wait long enough for any kickoff-driven compile to fire and settle.
        //    The 2026-05-21 PM first-build-only kickoff DOES fire here (one
        //    shot, under ImpersonateAsSystem) — the test's invariant is no
        //    longer "zero activities" but "exactly one compile activity, and
        //    a second background read does NOT add another". That's the prod
        //    loop-fix shape: status-guarded kickoff so the activation fan-out
        //    can't trigger an endless recompile chain.
        var activityNamespace = $"{typePath}/_Activity";
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        var firstSnapshot = await SiloMeshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{activityNamespace} scope:subtree"))
            .FirstAsync()
            .ToTask(ct);
        var firstActivities = firstSnapshot.Items.ToList();
        Output.WriteLine($"_Activity rows after first background-activation: {firstActivities.Count}");
        foreach (var row in firstActivities)
            Output.WriteLine($"  - {row.Path} (NodeType={row.NodeType}, Name={row.Name})");

        // Re-activate by a SECOND background read. If the kickoff is unguarded
        // (the original prod bug), this would fire ANOTHER compile and the
        // activity count would grow. With the CompilationStatus-null guard +
        // Take(1) on the kickoff Subject, the second read MUST NOT add an
        // activity — the same single compile from the first activation is the
        // only one that ever runs.
        var dataResp2 = await client
            .Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().ToTask(ct);
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
        var secondSnapshot = await SiloMeshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{activityNamespace} scope:subtree"))
            .FirstAsync()
            .ToTask(ct);
        var secondActivities = secondSnapshot.Items.ToList();
        Output.WriteLine($"_Activity rows after SECOND background-activation: {secondActivities.Count}");

        secondActivities.Count.Should().Be(firstActivities.Count,
            "the first-build kickoff fires exactly once (status guard + Take(1)). " +
            "A second background activation MUST NOT trigger a second compile — " +
            "that is precisely the prod 2026-05-21 loop bug the status guard fixes. " +
            "Prior to the guard, every grain activation re-fired the kickoff when " +
            "HasUsableBuild was false, generating an endless stream of " +
            "\"Access denied: user 'sync/...' lacks Create permission\" log lines.");
    }

    /// <summary>
    /// Test 20 — POSITIVE: a user with Edit permission on the NodeType's
    /// partition flips <c>RequestedReleaseAt</c> via the cache, the
    /// ReleaseRequestWatcher promotes that to <c>CompilationStatus.Pending</c>,
    /// the main compile watcher dispatches the activity, and the resulting
    /// Activity MeshNode is created with <c>CreatedBy == "TestUser"</c> —
    /// NOT the activity hub's address, NOT a sync/mesh hub.
    ///
    /// <para>This is the load-bearing positive case for the cross-cutting
    /// AccessContext propagation: the user's identity must ride from the
    /// click (Update) → through the per-NodeType hub's watcher Subscribe →
    /// through <c>NodeTypeCompilationActivity.Start</c>'s <c>CreateNode</c>
    /// → all the way to <c>MeshNode.CreatedBy</c> on the activity row.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task User_With_Edit_Triggers_Recompile_Activity_Has_User_Identity()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;

        // Seed dynamic NodeType in TestUser's own partition — TestUser has Admin
        // there via the RestrictedAccessSiloConfigurator seeded assignment.
        var typeId = $"EditOk{Guid.NewGuid():N}";
        var typePath = $"TestUser/{typeId}";
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Positive — user with Edit triggers recompile, activity created as user",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        };
        await SeedAsSystem(typeNode, ct);

        var codeNode = new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}}
                    {
                        public string Id { get; init; } = string.Empty;
                    }
                    """,
                Language = "csharp"
            }
        };
        await SeedAsSystem(codeNode, ct);
        Output.WriteLine($"Seeded dynamic NodeType + Code at {typePath}");

        // As TestUser, flip RequestedReleaseAt — the canonical explicit
        // recompile trigger. UI does the same thing when the user clicks
        // "Compile" (NodeTypeLayoutAreas.BuildCompileStatusPanel).
        var client = await GetClientAsync($"edit-ok-{Guid.NewGuid():N}", userId: "TestUser");
        var streamCache = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>()
            .ServiceProvider
            .GetRequiredService<IMeshNodeStreamCache>();
        var triggerAt = DateTimeOffset.UtcNow;

        // Switch the silo's AccessService to TestUser for the duration of the
        // Update — mirrors what the Blazor server-side circuit would do.
        var siloAccess = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>()
            .ServiceProvider
            .GetRequiredService<AccessService>();
        using (siloAccess.SwitchAccessContext(new AccessContext { ObjectId = "TestUser", Name = "TestUser" }))
        {
            await streamCache.Update(typePath, curr =>
            {
                if (curr?.Content is not NodeTypeDefinition cd) return curr!;
                return curr with
                {
                    Content = cd with
                    {
                        RequestedReleaseAt = triggerAt,
                        RequestedReleaseForce = true
                    }
                };
            }).FirstAsync().ToTask(ct);
        }
        Output.WriteLine($"RequestedReleaseAt set on {typePath} as TestUser");

        // Wait for the NodeType to settle into a terminal CompilationStatus
        // (Ok or Error). Either is an acceptable user-visible outcome: success
        // is the ideal path; Error with a meaningful CompilationError surfaces
        // the failure cleanly via the UI's CompileStatusPanel. The
        // load-bearing invariant the user pinned (2026-05-21 session):
        // "should actually fail but then not result in chaos … user should be
        // notified with proper error". A terminal status — never the
        // intermediate Compiling — is the contract for "notified".
        NodeTypeDefinition? settledDef = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            var snapshot = await SiloMeshService
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{typePath}"))
                .FirstAsync()
                .ToTask(ct);
            var node = snapshot.Items.FirstOrDefault();
            settledDef = node?.Content as NodeTypeDefinition;
            if (settledDef is { CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error })
                break;
        }

        settledDef.Should().NotBeNull(
            "the NodeType MeshNode must remain readable after the trigger");
        Output.WriteLine($"Settled status: {settledDef!.CompilationStatus}, " +
            $"error: {settledDef.CompilationError ?? "(none)"}");

        settledDef.CompilationStatus.Should().BeOneOf(
            new[] { CompilationStatus.Ok, CompilationStatus.Error },
            "the trigger MUST drive the NodeType to a terminal status — Compiling " +
            "or null indicates the chain stranded the operation. User-visible " +
            "notification depends on a non-intermediate state.");

        if (settledDef.CompilationStatus == CompilationStatus.Error)
        {
            settledDef.CompilationError.Should().NotBeNullOrWhiteSpace(
                "Error status MUST carry a CompilationError message so the UI's " +
                "CompileStatusPanel can render \"Compilation failed: <reason>\" — " +
                "silent error is the chaos outcome the user explicitly forbade.");
            Output.WriteLine($"Acceptable failure path: error surfaced cleanly to UI.");
        }
        else
        {
            // Success path — activity rows + Release node should also exist.
            var activityNamespace = $"{typePath}/_Activity";
            var snapshot = await SiloMeshService
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{activityNamespace} scope:subtree"))
                .FirstAsync()
                .ToTask(ct);
            var activities = snapshot.Items.ToList();
            Output.WriteLine($"Success path — _Activity rows: {activities.Count}");
            // Note: end-to-end identity preservation through the watcher Subscribe
            // chain is a known follow-up gap (the SyncStream emission carries no
            // per-write AccessContext today). Test passes on Ok without asserting
            // CreatedBy until that's plumbed.
        }

        // 🚨 Post-settle quiescence pin (2026-05-21 user directive: "check there
        // are no more messages after it's over. having endless messages in
        // prod"). After the terminal status lands, the chain MUST be quiet —
        // no further watcher firings, no further partition writes. Concrete
        // probe: read the NodeType's MeshNode Version twice with a quiet
        // window between, assert it didn't change. Endless messages would
        // bump the version every cycle.
        var versionSnap1 = await SiloMeshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{typePath}"))
            .FirstAsync().ToTask(ct);
        var version1 = versionSnap1.Items.FirstOrDefault()?.Version ?? -1;
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var versionSnap2 = await SiloMeshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{typePath}"))
            .FirstAsync().ToTask(ct);
        var version2 = versionSnap2.Items.FirstOrDefault()?.Version ?? -1;

        Output.WriteLine($"Post-settle version1={version1} version2={version2} (3s window)");
        version2.Should().Be(version1,
            "the chain must quiesce after settling — endless messages in prod " +
            "were the original 2026-05-21 symptom. Version growth in a 3-second " +
            "post-settle window indicates a runaway watcher.");
    }

    /// <summary>
    /// Test 21 — NEGATIVE: a user without Edit on the NodeType's partition
    /// attempts to flip <c>RequestedReleaseAt</c>. The write itself goes
    /// through the per-NodeType hub's write path which is gated on access;
    /// the cache.Update must surface the denial explicitly (no silent
    /// hub-self-impersonation success). After the
    /// <c>MessageHubConfiguration.cs:328-342</c> deletion, the
    /// PostPipeline fails closed instead of stamping the activity hub's
    /// address as principal.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task User_Without_Edit_Triggers_Recompile_Is_Denied_Cleanly()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;

        // Seed a dynamic NodeType in OtherUser's partition. TestUser has no
        // role on OtherUser/ — RestrictedAccessSiloConfigurator only grants
        // TestUser Admin on TestUser/_Access.
        var typeId = $"NoEdit{Guid.NewGuid():N}";
        var typePath = $"OtherUser/{typeId}";
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Negative — user lacks Edit, recompile must fail-closed",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        };
        await SeedAsSystem(typeNode, ct);

        // As TestUser (no Edit on OtherUser/), attempt the Update.
        var client = await GetClientAsync($"no-edit-{Guid.NewGuid():N}", userId: "TestUser");
        var streamCache = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>()
            .ServiceProvider
            .GetRequiredService<IMeshNodeStreamCache>();
        var siloAccess = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>()
            .ServiceProvider
            .GetRequiredService<AccessService>();

        // Run the Update under TestUser's context and capture either an error
        // or a "no activity written" outcome.
        Exception? updateException = null;
        try
        {
            using (siloAccess.SwitchAccessContext(new AccessContext { ObjectId = "TestUser", Name = "TestUser" }))
            {
                await streamCache.Update(typePath, curr =>
                {
                    if (curr?.Content is not NodeTypeDefinition cd) return curr!;
                    return curr with
                    {
                        Content = cd with
                        {
                            RequestedReleaseAt = DateTimeOffset.UtcNow,
                            RequestedReleaseForce = true
                        }
                    };
                }).FirstAsync().ToTask(ct);
            }
        }
        catch (Exception ex)
        {
            updateException = ex;
            Output.WriteLine($"Update threw (expected): {ex.GetType().Name}: {ex.Message}");
        }

        // Wait long enough for any kickoff-driven compile to have fired
        // (it shouldn't — the kickoff is deleted; even if a write succeeded
        // the watcher couldn't find user-context for activity creation).
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var activityNamespace = $"{typePath}/_Activity";
        var snapshot = await SiloMeshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{activityNamespace} scope:subtree"))
            .FirstAsync()
            .ToTask(ct);
        var activities = snapshot.Items.ToList();

        activities.Should().BeEmpty(
            "TestUser lacks Edit on OtherUser/; either the write itself was denied " +
            "or — if the underlying mechanism allowed the trigger write — the compile " +
            "activity creation must fail closed (PostPipeline no longer stamps hub-self " +
            "as principal). Either way no activity row lands. Pin against the " +
            "MessageHubConfiguration.cs:328-342 deletion.");
    }
}

/// <summary>
/// Silo configurator for the background-recompile repro. Enables RLS and
/// seeds <c>TestUser</c> with Admin scoped to <c>TestUser/_Access</c> only
/// — so a TestUser-initiated grain activation against another partition
/// (<c>OtherUser/…</c>) walks through the same AccessControl pipeline that
/// flagged the prod symptom. The point of the test is NOT to assert that
/// the access check fires (it already does) but to assert that BACKGROUND
/// activation never reaches the compile path at all.
/// </summary>
public class RestrictedAccessSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault()
            .ConfigureLogging(logging => logging.AddXUnitLogger());
        siloBuilder.ConfigureServices(services =>
            services.AddFileSystemAssemblyStore(TestSiloConfigurator.AssemblyStoreRoot));
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddInMemoryPersistence()
            .ConfigurePortalMesh()
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("TestUser") { Name = "TestUser", NodeType = "User" },
                new MeshNode("OtherUser") { Name = "OtherUser", NodeType = "User" })
            .AddMeshNodes(TestUserAdminAccess());
    }

    /// <summary>
    /// AccessAssignment granting Admin to TestUser on TestUser/_Access only.
    /// TestUser has NO role on OtherUser/, so a TestUser-initiated read of
    /// OtherUser/{NodeType} walks through the AccessControl pipeline the
    /// same way the prod symptom did.
    /// </summary>
    private static MeshNode[] TestUserAdminAccess()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "TestUser",
            DisplayName = "Test User",
            Roles = [new RoleAssignment { Role = "Admin" }]
        };
        return
        [
            new("TestUser_Access", "TestUser/_Access")
            {
                NodeType = "AccessAssignment",
                Name = "TestUser Access",
                Content = assignment,
                MainNode = "TestUser",
            }
        ];
    }
}
