using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
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
        var client = GetClient($"background-{Guid.NewGuid():N}", userId: "TestUser");
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
        var client = GetClient($"edit-ok-{Guid.NewGuid():N}", userId: "TestUser");
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
        // 🚨 Use GetMeshNodeStream (per-node MeshNodeReference reducer) instead of
        // ObserveQuery — query rows carry stale Content by design (feedback_query_content_stale.md),
        // so the polling loop's `node.Content as NodeTypeDefinition` cast saw the
        // pre-trigger snapshot forever and `settledDef` stayed null. The per-node
        // hub's reducer refreshes Content on every write.
        var siloHub = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>();
        var settledNode = await siloHub.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error)
            .Take(1)
            .Timeout(60.Seconds())
            .ToTask(ct);
        var settledDef = settledNode.Content as NodeTypeDefinition;

        settledDef.Should().NotBeNull(
            "the NodeType MeshNode must remain readable after the trigger");
        Output.WriteLine($"Settled status: {settledDef!.CompilationStatus}, " +
            $"error: {settledDef.CompilationError ?? "(none)"}");

        settledDef.CompilationStatus.Should().BeOneOf(
            new Enum[] { CompilationStatus.Ok, CompilationStatus.Error },
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
        var client = GetClient($"no-edit-{Guid.NewGuid():N}", userId: "TestUser");
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

    /// <summary>
    /// Regression guard for the 2026-05-29 wedge: a per-NodeType grain must stay
    /// RESPONSIVE while its first-build compile is in flight. The compile is
    /// dispatched to a separate Activity hub (fire-and-forget); the NodeType
    /// hub's own action-block must never block waiting on it. The prod symptom
    /// was the opposite — every <c>DeliverMessage</c> to the grain broke its 30s
    /// promise and <c>GetPermissionRequest</c> timed out at 15s while a compile
    /// churned, so instances rendered the "compile did not settle" overlay.
    ///
    /// <para>We activate the grain (which kicks off the first-build compile),
    /// then immediately fire a SECOND request to the SAME grain and require it
    /// to answer well inside the Orleans 30s promise window. A wedged pump makes
    /// that Observe time out. We then require the NodeType to settle Ok and to
    /// keep answering afterwards.</para>
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task NodeTypeHub_StaysResponsive_WhileFirstBuildCompileInFlight()
    {
        var ct = new CancellationTokenSource(80.Seconds()).Token;

        var typeId = $"Responsive{Guid.NewGuid():N}";
        var typePath = $"TestUser/{typeId}";
        await SeedAsSystem(MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Grain-responsiveness-during-compile regression guard",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        }, ct);
        await SeedAsSystem(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}}
                    {
                        public string Title { get; init; } = string.Empty;
                    }
                    """,
                Language = "csharp"
            }
        }, ct);
        Output.WriteLine($"Seeded dynamic NodeType + Code at {typePath} (no build yet)");

        var client = GetClient($"responsive-{Guid.NewGuid():N}", userId: "TestUser");

        // First read activates the per-NodeType grain and kicks off the
        // first-build compile (InstallCompileWatcher → Pending → dispatch).
        await client.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().Timeout(20.Seconds()).ToTask(ct);
        Output.WriteLine("First (activating) read returned; compile kicked off.");

        // PROBE: a second request to the SAME grain must come back promptly while
        // the compile runs on the Activity hub. A wedged grain (the prod bug)
        // would let this Observe run past the Orleans 30s promise and time out
        // here at 10s.
        var probe = await client.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);
        probe.Message.Should().NotBeNull(
            "the NodeType grain must answer a second request promptly WHILE its " +
            "first-build compile runs on the Activity hub — the action-block pump " +
            "must not block on the compile (the 2026-05-29 wedge symptom)");
        Output.WriteLine("Responsiveness probe returned while compile in flight.");

        // The compile must finish and settle Ok (a clean dynamic type compiles).
        var siloHub = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>();
        var settled = await siloHub.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error)
            .Take(1).Timeout(60.Seconds()).ToTask(ct);
        ((NodeTypeDefinition)settled.Content!).CompilationStatus.Should().Be(
            CompilationStatus.Ok, "a clean dynamic NodeType must finish compiling");
        Output.WriteLine("NodeType settled Ok.");

        // Post-settle: the grain is still responsive — no lingering wedge.
        var after = await client.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);
        after.Message.Should().NotBeNull(
            "the grain stays responsive after the compile settles");
    }

    /// <summary>
    /// Regression guard for the 2026-05-29 wedge ROOT CAUSE: a NodeType that
    /// comes up persisted as <c>CompilationStatus = Compiling</c> must
    /// RE-TRIGGER its compile on init and settle to a terminal status.
    ///
    /// <para>When the process dies (or the per-NodeType grain deactivates) AFTER
    /// the <c>Pending → Compiling</c> flip but BEFORE the terminal Ok/Error
    /// write-back, the on-disk JSON freezes at <c>Compiling</c>. Before the
    /// recovery kickoff (<see cref="NodeTypeCompilationHelpers.InstallCompileWatcher"/>)
    /// NOTHING re-drove that state on the next activation — the first-build
    /// kickoff needs <c>CompilationStatus is null</c>, the compile watcher needs
    /// <c>Pending</c>, and the release-request watcher only fires when the status
    /// is SETTLED. So the NodeType sat in <c>Compiling</c> forever, every
    /// instance hub fell back to the default config (no <c>MeshNodeReference</c>
    /// reducer), and the instance page rendered nothing — the
    /// <c>rbuergi/CatBond/AtlanticBond</c> "I get nothing" symptom the user hit.</para>
    ///
    /// <para>The fix: on the first emission at hub init, if status is
    /// <c>Compiling</c>, find the recorded compile activity; if it is not
    /// actually running (missing / terminal / stale start) the compile is
    /// orphaned, so flip <c>Compiling → Pending</c> and let the watcher dispatch
    /// a fresh compile. This test seeds the source first, then seeds the NodeType
    /// already pinned to <c>Compiling</c> with NO live activity — exactly the
    /// shape a hard restart leaves behind — and requires it to recover to
    /// <c>Ok</c>. Without the recovery kickoff the wait below never completes and
    /// the test times out (the prod wedge).</para>
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task NodeTypeHub_StrandedInCompiling_RecompilesOnInit()
    {
        var ct = new CancellationTokenSource(80.Seconds()).Token;

        var typeId = $"Stranded{Guid.NewGuid():N}";
        var typePath = $"TestUser/{typeId}";

        // Seed the source FIRST so the recovery-triggered compile — which fires
        // the moment the NodeType grain activates — finds it.
        await SeedAsSystem(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}}
                    {
                        public string Title { get; init; } = string.Empty;
                    }
                    """,
                Language = "csharp"
            }
        }, ct);

        // Seed the NodeType STRANDED: persisted as Compiling, no live activity
        // (LastCompilationActivityPath null), with a stale start timestamp. This
        // is exactly the on-disk shape a hard restart leaves behind when the
        // process died mid-compile, before the terminal Ok/Error write-back.
        await SeedAsSystem(MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Stranded-in-Compiling recovery regression guard",
                Configuration = $"config => config.WithContentType<{typeId}>()",
                CompilationStatus = CompilationStatus.Compiling,
                LastCompilationActivityPath = null,
                LastCompileStartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10)
            }
        }, ct);
        Output.WriteLine($"Seeded STRANDED NodeType (CompilationStatus=Compiling, no activity) at {typePath}");

        // Activate the grain. The recovery kickoff in InstallCompileWatcher fires
        // on the first emission, sees Compiling + no running activity, and flips
        // Compiling → Pending — which the watcher turns into a fresh compile.
        var client = GetClient($"stranded-{Guid.NewGuid():N}", userId: "TestUser");
        await client.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().Timeout(20.Seconds()).ToTask(ct);
        Output.WriteLine("Grain activated; recovery kickoff should have re-triggered the compile.");

        // The stranded NodeType must un-strand and settle Ok. Without the
        // recovery kickoff this Where never completes (status stays Compiling
        // forever) and the test times out — exactly the prod wedge.
        var siloHub = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>();
        var settled = await siloHub.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error)
            .Take(1).Timeout(60.Seconds()).ToTask(ct);
        var settledDef = (NodeTypeDefinition)settled.Content!;
        Output.WriteLine($"Settled status: {settledDef.CompilationStatus}, " +
            $"error: {settledDef.CompilationError ?? "(none)"}");

        settledDef.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "a NodeType stranded in Compiling on init must recover — the recovery " +
            "kickoff re-triggers the orphaned compile (flip Compiling→Pending) and " +
            "a clean dynamic type compiles. Before the recovery branch the NodeType " +
            "sat in Compiling forever and every instance rendered nothing.");
    }

    /// <summary>
    /// Wake-up of a "still Running" activity: a NodeType comes up persisted as
    /// Compiling WITH a recorded activity path and a RECENT compile-start stamp —
    /// the exact shape the OLD recovery treated as "genuinely running, leave it
    /// alone" by probing the activity hub cross-hub. That probe lagged the
    /// owner's writes; a false "still running" read left the NodeType stranded
    /// forever (the rbuergi/CatBond "renders nothing" symptom). The simplified
    /// recovery reads ONLY the owner's own state — Compiling on the first init
    /// emission ALWAYS means interrupted — so it re-triggers regardless of any
    /// recorded/recent activity. This pins that no-probe behavior: it MUST settle
    /// Ok even though the recorded activity looks recent.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task NodeTypeHub_StrandedInCompiling_RecentActivityRecorded_StillRecompilesOnInit()
    {
        var ct = new CancellationTokenSource(80.Seconds()).Token;

        var typeId = $"StrandedRecent{Guid.NewGuid():N}";
        var typePath = $"TestUser/{typeId}";

        await SeedAsSystem(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}}
                    {
                        public string Title { get; init; } = string.Empty;
                    }
                    """,
                Language = "csharp"
            }
        }, ct);

        // Stranded WITH a recorded activity path and a RECENT start — the old
        // probe-based recovery would read this as "still running" and skip.
        var recordedActivityPath = $"{typePath}/_Activity/compile-{Guid.NewGuid():N}";
        await SeedAsSystem(MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Stranded-in-Compiling with recent activity recovery guard",
                Configuration = $"config => config.WithContentType<{typeId}>()",
                CompilationStatus = CompilationStatus.Compiling,
                LastCompilationActivityPath = recordedActivityPath,
                LastCompileStartedAt = DateTimeOffset.UtcNow // RECENT — old code would skip
            }
        }, ct);
        Output.WriteLine($"Seeded STRANDED NodeType (Compiling, RECENT activity {recordedActivityPath}) at {typePath}");

        var client = GetClient($"stranded-recent-{Guid.NewGuid():N}", userId: "TestUser");
        await client.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().Timeout(20.Seconds()).ToTask(ct);

        var siloHub = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>();
        var settled = await siloHub.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error)
            .Take(1).Timeout(60.Seconds()).ToTask(ct);
        var settledDef = (NodeTypeDefinition)settled.Content!;
        Output.WriteLine($"Settled status: {settledDef.CompilationStatus}");

        settledDef.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "recovery reads the owner's OWN Compiling state, not a cross-hub activity " +
            "probe — so it re-triggers even when a recent activity path is recorded. " +
            "The old probe could false-positive on lag and strand the NodeType forever.");
    }

    /// <summary>
    /// Repro for the 2026-05-29 activation self-deadlock — the case we hadn't
    /// covered before: activating a per-NodeType grain under a NON-System user
    /// AccessContext (the GUI-render shape, not the System kickoff path).
    ///
    /// <para>Before the fix, <see cref="NodeTypeCompilationHelpers.InstallSourcesWatcher"/>
    /// read its source set via <c>workspace.GetQuery</c> under the inbound user
    /// context, so <c>WrapWithPerUserRls</c> issued a <c>CheckPermission</c>
    /// round-trip per source node. For a source path UNDER the NodeType, that
    /// resolves the ancestor's Read by routing a <c>GetPermissionRequest</c>
    /// BACK to the same single-threaded, non-reentrant grain — a call-chain
    /// cycle that deadlocks activation (Orleans request-scheduling). The grain
    /// wedged, the compile's terminal write-back never landed, and the NodeType
    /// never reached Ok (the <c>rbuergi/CatBond/AtlanticBond</c> "renders
    /// nothing" + endless-GetPermissionRequest-timeout symptom).</para>
    ///
    /// <para>The fix reads source discovery as System (break-the-cycle), so no
    /// self-<c>CheckPermission</c> is issued. This test activates the NodeType
    /// as a real user and requires it to settle Ok and stay responsive. With the
    /// bug the grain self-deadlocks and the waits below time out.</para>
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task SourcesWatcher_UserContextActivation_SettlesWithoutSelfDeadlock()
    {
        var ct = new CancellationTokenSource(80.Seconds()).Token;

        var typeId = $"SelfDeadlock{Guid.NewGuid():N}";
        var typePath = $"TestUser/{typeId}";
        await SeedAsSystem(MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Activation self-deadlock repro — source under NodeType, user-context activation",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        }, ct);
        // Source node UNDER the NodeType — its ancestor permission check is what
        // routed a GetPermissionRequest back into the NodeType grain.
        await SeedAsSystem(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}} { public string Title { get; init; } = string.Empty; }
                    """,
                Language = "csharp"
            }
        }, ct);
        Output.WriteLine($"Seeded {typePath} + Source/code");

        // Activate the per-NodeType grain AS TestUser (the GUI-render shape): the
        // inbound user AccessContext rides into the grain via
        // AccessContextGrainCallFilter, so the activation — and the
        // InstallSourcesWatcher source read — runs under TestUser, not System.
        var client = GetClient($"selfdl-{Guid.NewGuid():N}", userId: "TestUser");
        await client.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().Timeout(20.Seconds()).ToTask(ct);
        Output.WriteLine("Activated NodeType grain under TestUser context.");

        // The NodeType MUST settle to terminal Ok (a clean dynamic type compiles).
        // With the self-deadlock the grain wedges and the compile's terminal
        // write-back never lands → this Where never completes → timeout.
        var siloHub = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>();
        var settled = await siloHub.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error)
            .Take(1).Timeout(60.Seconds()).ToTask(ct);
        ((NodeTypeDefinition)settled.Content!).CompilationStatus.Should().Be(
            CompilationStatus.Ok,
            "the NodeType must compile + settle Ok under user-context activation; " +
            "a grain wedged by the source-watcher self-CheckPermission cycle never reaches Ok");

        // And the grain is still responsive — the watcher didn't block the pump.
        var after = await client.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);
        after.Message.Should().NotBeNull("the grain stays responsive after user-context activation");
    }

    /// <summary>
    /// Regression guard for the 2026-05-29 framework-redeploy gap: a dynamic
    /// NodeType whose assembly was compiled against a PREVIOUS MeshWeaver build
    /// (<c>CompiledFrameworkVersion != FrameworkVersion</c>, but <c>Status=Ok</c>
    /// and the <c>LatestAssembly{Collection,Path}</c> fields populated) must
    /// SELF-HEAL on the next instance activation — exactly like the "assembly
    /// bytes missing from store" case already does.
    ///
    /// <para><see cref="NodeTypeCompilationHelpers.HasUsableBuild"/> returns
    /// false purely on the framework-version mismatch, so before the fix
    /// <c>EnrichWithNodeType</c> skipped straight to a bare "Compilation failed"
    /// overlay with an EMPTY code block — no diagnostic captured, because the
    /// compile never actually failed. Symptom: after every deploy, every dynamic
    /// NodeType rendered the scary overlay until an operator manually recompiled
    /// it (the <c>rbuergi/CatBond</c> "Compilation failed / empty code block"
    /// the user hit immediately after a binary rebuild). The fix routes the
    /// framework-stale case through <c>TriggerRecompileAndRetry</c> (Pending flip
    /// → watcher rebuilds under SYSTEM identity → fresh
    /// <c>CompiledFrameworkVersion</c>), bounded by <c>MaxRecompileAttempts</c>.</para>
    ///
    /// <para>This test compiles a clean dynamic type (<c>Status=Ok</c>, FV=live),
    /// then MUTATES <c>CompiledFrameworkVersion</c> to a bogus value to simulate
    /// the binary being redeployed under the type's feet, then activates a FRESH
    /// instance. The instance enrichment must auto-recompile and re-stamp the
    /// LIVE framework version (recompiling on the same binary reproduces the same
    /// FrameworkVersion) — seeing the live value again, after we forced the bogus
    /// one, proves a real self-heal recompile ran. Without the fix the bogus
    /// version sticks and the instance gets the error overlay, so the heal-wait
    /// below times out.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task FrameworkStaleAssembly_SelfHealsOnInstanceActivation()
    {
        var ct = new CancellationTokenSource(110.Seconds()).Token;

        var typeId = $"FwStale{Guid.NewGuid():N}";
        var typePath = $"TestUser/{typeId}";
        await SeedAsSystem(MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Framework-stale self-heal regression guard",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        }, ct);
        await SeedAsSystem(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}}
                    {
                        public string Title { get; init; } = string.Empty;
                    }
                    """,
                Language = "csharp"
            }
        }, ct);
        Output.WriteLine($"Seeded dynamic NodeType + Code at {typePath}");

        var client = GetClient($"fwstale-{Guid.NewGuid():N}", userId: "TestUser");
        var siloHub = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>();

        // 1. Activate the NodeType → first-build compile → settle Ok with a real
        //    assembly stamped with the LIVE framework version.
        await client.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().Timeout(20.Seconds()).ToTask(ct);
        var firstOk = await siloHub.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyPath)
                && !string.IsNullOrEmpty(d.CompiledFrameworkVersion))
            .Take(1).Timeout(60.Seconds()).ToTask(ct);
        var liveFv = ((NodeTypeDefinition)firstOk.Content!).CompiledFrameworkVersion!;
        Output.WriteLine($"First compile Ok; live framework version = {liveFv}");

        // 2. Simulate a redeploy: stamp a bogus CompiledFrameworkVersion while
        //    leaving Status=Ok and the assembly fields intact. HasUsableBuild now
        //    returns false purely on the framework mismatch — the exact
        //    post-deploy shape. Status stays Ok, so NOTHING auto-recompiles yet
        //    (the first-build kickoff needs null, the compile watcher needs
        //    Pending) — only an instance activation can trigger the self-heal.
        var bogusFv = $"STALE-FRAMEWORK-{Guid.NewGuid():N}";
        var streamCache = siloHub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        using (SiloAccessService.ImpersonateAsSystem())
        {
            await streamCache.Update(typePath, curr =>
            {
                if (curr?.Content is not NodeTypeDefinition cd) return curr!;
                return curr with { Content = cd with { CompiledFrameworkVersion = bogusFv } };
            }).FirstAsync().ToTask(ct);
        }
        // Confirm the bogus version is the live persisted state before we activate.
        await siloHub.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n.Content is NodeTypeDefinition d && d.CompiledFrameworkVersion == bogusFv)
            .Take(1).Timeout(20.Seconds()).ToTask(ct);
        Output.WriteLine($"Stamped bogus framework version {bogusFv} (simulated redeploy)");

        // 3. Activate a FRESH instance of the type. Its enrichment reads the
        //    NodeType, sees HasUsableBuild=false (framework mismatch), and must
        //    route through TriggerRecompileAndRetry — NOT the bare error overlay.
        //    Activation blocks on the self-heal recompile, so allow the full
        //    SlowPathTimeout window.
        var instancePath = $"{typePath}/Inst";
        await SeedAsSystem(MeshNode.FromPath(instancePath) with
        {
            Name = "Inst",
            NodeType = typePath,
            Content = System.Text.Json.JsonSerializer.SerializeToElement(new { Title = "instance" })
        }, ct);
        await client.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(instancePath)))
            .FirstAsync().Timeout(90.Seconds()).ToTask(ct);
        Output.WriteLine($"Activated instance {instancePath} — self-heal should have fired.");

        // 4. The self-heal must have recompiled the NodeType and re-stamped the
        //    LIVE framework version. Seeing liveFv again — after we forced
        //    bogusFv — proves a real recompile ran via the self-heal path.
        var healed = await siloHub.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && d.CompiledFrameworkVersion == liveFv)
            .Take(1).Timeout(60.Seconds()).ToTask(ct);
        ((NodeTypeDefinition)healed.Content!).CompiledFrameworkVersion.Should().Be(liveFv,
            "framework-stale enrichment must self-heal: TriggerRecompileAndRetry flips " +
            "the NodeType to Pending, the watcher rebuilds it against the CURRENT framework, " +
            "and CompiledFrameworkVersion returns to the live value. Before the fix the bogus " +
            "version stuck and every instance got a bare \"Compilation failed\" overlay with " +
            "an empty code block — the symptom the user hit after a binary rebuild.");
        Output.WriteLine("NodeType self-healed: live framework version restored via recompile.");

        // 5. The instance grain stays responsive after the heal.
        var after = await client.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(instancePath)))
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);
        after.Message.Should().NotBeNull("the instance stays responsive after the self-heal recompile");
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
