using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#3390 — a compile dispatch RECORDS what it was dispatched for, whichever door opened it.</b>
///
/// <para>Twelve production sites flip <see cref="CompilationStatus.Pending"/>. Exactly ONE stamped
/// <see cref="NodeTypeDefinition.DispatchedBuildInputs"/>; four wrote an explicit <c>null</c> and
/// seven did not mention the field at all. An unstamped in-flight compile PARKS every release
/// request that arrives during it — by design, because "absorbing against an unknown input set is
/// a guess" — and the parked trigger re-fires on that compile's own terminal write-back and
/// compiles a second time. That is exactly the "one logical event → N sequential compiles" shape
/// #2544 removed, and it was still the outcome for every compile the other eleven doors started.</para>
///
/// <para><b>What this file pins is the OUTCOME, not the spelling.</b> Each assertion runs a door's
/// state transition and then asks
/// <see cref="NodeTypeCompilationHelpers.IsSatisfiedByInFlightCompile"/> the question the release
/// watcher asks — so it fails if a door stops stamping, and it fails if the stamp is
/// <i>wrong</i>. Which doors exist at all is pinned mechanically by
/// <see cref="DispatchedBuildInputsInvariantGuard"/>: there is exactly one
/// <c>CompilationStatus = …Pending</c> write in <c>src/</c>, inside
/// <see cref="NodeTypeCompilationHelpers.DispatchPending(NodeTypeDefinition, string?)"/>, so a
/// thirteenth door cannot be added without going through the function these tests exercise.</para>
///
/// <para>🚨 <b>The safe direction is preserved and asserted.</b> Absorbing a request whose inputs
/// do NOT match would serve bytes that are not what was asked for, so a request against moved
/// sources, a moved framework or a moved module set still PARKS — see
/// <see cref="A_request_against_MOVED_sources_still_parks"/> and
/// <see cref="A_request_against_a_MOVED_module_set_still_parks"/>, the second being the shape
/// #3395 makes reachable (one boot resolving two different module sets seconds apart).</para>
/// </summary>
public class DispatchedBuildInputsStampedAtEveryDoorTest
{
    private const string ModulesHash = "mod-1";
    private const string TypePath = "P/T";

    private static ImmutableDictionary<string, long> Sources(params (string Path, long Ticks)[] entries)
        => ImmutableDictionary.CreateRange(
            new Dictionary<string, long>(
                System.Linq.Enumerable.ToDictionary(entries, e => e.Path, e => e.Ticks)));

    private static readonly ImmutableDictionary<string, long> LiveSources =
        Sources(("P/T/Source/code", 638_000_000_000_000_000));

    /// <summary>The token a release request arriving right now would be built from — the exact
    /// expression <c>InstallReleaseRequestWatcher</c> uses before it calls the absorb predicate.</summary>
    private static string RequestedToken(
        NodeTypeDefinition def, string? modulesHash = ModulesHash)
        => NodeTypeCompilationHelpers.BuildInputsToken(modulesHash, def.CurrentSourceVersions);

    private static NodeTypeDefinition NeverCompiled() => new()
    {
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
        CurrentSourceVersions = LiveSources,
    };

    /// <summary>
    /// A definition with a compile genuinely in flight, written OUT — never taken from
    /// <see cref="NodeTypeCompilationHelpers.DispatchPending(NodeTypeDefinition, string?)"/>.
    /// The three terminal-write tests below assert that a stamp is CLEARED, so seeding them from
    /// the door would make them pass for free the moment the door stopped stamping: they would be
    /// asserting <c>null == null</c> about a state the defect never produces.
    /// </summary>
    private static NodeTypeDefinition InFlight() => NeverCompiled() with
    {
        CompilationStatus = CompilationStatus.Pending,
        DispatchedBuildInputs =
            NodeTypeCompilationHelpers.BuildInputsToken(ModulesHash, LiveSources),
    };

    // ── The door itself ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 THE discriminating assertion. A release request arriving while a compile dispatched for
    /// these exact inputs is in flight is ABSORBED. Before #3390 every door but one left the stamp
    /// null or untouched, so this composition answered FALSE and the request was parked.
    /// </summary>
    [Fact]
    public void A_request_for_the_inputs_the_door_dispatched_for_is_ABSORBED()
    {
        var dispatched = NodeTypeCompilationHelpers.DispatchPending(NeverCompiled(), ModulesHash);

        dispatched.CompilationStatus.Should().Be(CompilationStatus.Pending);
        NodeTypeCompilationHelpers
            .IsSatisfiedByInFlightCompile(dispatched, RequestedToken(dispatched))
            .Should().BeTrue(
                "the compile this door dispatched will produce byte-for-byte what the request "
                + "asks for, so parking it buys nothing and costs a second compile plus an "
                + "instance-hub invalidation on the first one's terminal write-back");
    }

    /// <summary>
    /// 🚨 The safe failure, unchanged. The sources moved after the dispatch, so the in-flight
    /// compile will NOT produce what this request asks for. Absorbing here would silently drop the
    /// user's latest edits — the one outcome worse than the storm #2544 removed.
    /// </summary>
    [Fact]
    public void A_request_against_MOVED_sources_still_parks()
    {
        var dispatched = NodeTypeCompilationHelpers.DispatchPending(NeverCompiled(), ModulesHash);
        var afterAnEdit = dispatched with
        {
            CurrentSourceVersions = Sources(("P/T/Source/code", 638_000_000_000_000_001)),
        };

        NodeTypeCompilationHelpers
            .IsSatisfiedByInFlightCompile(afterAnEdit, RequestedToken(afterAnEdit))
            .Should().BeFalse(
                "the source snapshot changed after the dispatch, so the running compile is not "
                + "the one this request is asking for");
    }

    /// <summary>
    /// 🚨 The #3395 shape, and the reason ACCURATE stamping is the fix rather than eager
    /// absorbing: one boot can resolve two different module sets seconds apart, and a dispatch
    /// stamped under the first must not absorb a request formed under the second.
    /// </summary>
    [Fact]
    public void A_request_against_a_MOVED_module_set_still_parks()
    {
        var dispatched = NodeTypeCompilationHelpers.DispatchPending(NeverCompiled(), ModulesHash);

        NodeTypeCompilationHelpers
            .IsSatisfiedByInFlightCompile(dispatched, RequestedToken(dispatched, "mod-2"))
            .Should().BeFalse(
                "the installed-module fingerprint moved between the dispatch and the request, so "
                + "the running compile does not produce what this request asks for — parking is "
                + "the answer, and it is only reachable because the dispatch recorded mod-1 "
                + "instead of recording nothing");
    }

    /// <summary>A door publishing a NEWER source snapshot in the same write stamps the set it is
    /// re-driving, never the one it is replacing — the sources watcher's parked auto-retry.</summary>
    [Fact]
    public void A_door_that_refreshes_the_snapshot_stamps_the_NEW_one()
    {
        var newSnapshot = Sources(("P/T/Source/code", 638_000_000_000_000_009));
        var stale = NeverCompiled();

        var dispatched = NodeTypeCompilationHelpers.DispatchPending(
            stale with { CurrentSourceVersions = newSnapshot }, ModulesHash, newSnapshot);

        dispatched.DispatchedBuildInputs.Should().Be(
            NodeTypeCompilationHelpers.BuildInputsToken(ModulesHash, newSnapshot),
            "the dispatch is FOR the fixed sources; stamping the broken set it is replacing would "
            + "park the very retry the user is waiting for and absorb one asking for the old code");
    }

    // ── The terminal write: the stamp must stop describing anything ─────────────────────────────

    /// <summary>
    /// 🔴 After completion the record must no longer report in-flight inputs. A stamp that
    /// survives a terminal write makes non-null mean "in flight OR finished at some point", and
    /// the absorb read branches on exactly that field.
    /// </summary>
    [Fact]
    public void After_a_SUCCESSFUL_compile_the_stamp_reports_nothing_in_flight()
    {
        var dispatched = InFlight();
        var token = RequestedToken(dispatched);

        var settled = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            dispatched,
            new NodeCompilationResult(
                AssemblyLocation: "/cache/T_1/T.dll",
                NodeTypeConfigurations: [],
                CompiledSources: LiveSources,
                Collection: "assemblies",
                ContentPath: "P_T/v7.dll",
                Version: 7),
            currentNodeVersion: 3,
            activityPath: null,
            releasePath: null,
            modulesHash: ModulesHash);

        settled.CompilationStatus.Should().Be(CompilationStatus.Ok);
        settled.DispatchedBuildInputs.Should().BeNull(
            "the invariant is `non-null ⇔ Pending/Compiling` — a finished compile is not one");
        NodeTypeCompilationHelpers.IsSatisfiedByInFlightCompile(settled, token)
            .Should().BeFalse("there is no compile in flight to absorb against");
    }

    /// <summary>
    /// The failure twin — and the one the first half of #3390 MISSED, because
    /// <c>ApplyCompileFailure</c> writes its status as a ternary
    /// (<c>Unavailable</c> / <c>Error</c>) rather than the literal the static guard matched on.
    /// </summary>
    [Fact]
    public void After_a_FAILED_compile_the_stamp_reports_nothing_in_flight()
    {
        var dispatched = InFlight();
        var token = RequestedToken(dispatched);

        var settled = NodeTypeCompilationHelpers.ApplyCompileFailure(
            dispatched, result: null, error: new System.InvalidOperationException("CS0103"),
            activityPath: null, modulesHash: ModulesHash);

        settled.CompilationStatus.Should().Be(CompilationStatus.Error);
        settled.DispatchedBuildInputs.Should().BeNull(
            "a failure is as terminal as a success — the compile it described has ended");
        NodeTypeCompilationHelpers.IsSatisfiedByInFlightCompile(settled, token)
            .Should().BeFalse("there is no compile in flight to absorb against");
    }

    /// <summary>The parked short-circuit's re-settle: a Pending flip the watcher REFUSES to
    /// dispatch never became a compile, so its stamp must not outlive the refusal either.</summary>
    [Fact]
    public void A_REFUSED_dispatch_clears_the_stamp_when_it_settles()
    {
        var dispatched = InFlight();

        var settled = NodeTypeCompilationHelpers.ApplyGateSettle(
            dispatched, reason: "parked", formedUnderLiveInputs: true, modulesHash: ModulesHash);

        settled.DispatchedBuildInputs.Should().BeNull(
            "no Roslyn ran, so nothing is in flight — and this is the path a stamped flip takes "
            + "when the type is parked, which is why it may not leave the token standing");
    }

    // ── Two doors whose transition is a pure function, checked end-to-end ───────────────────────

    /// <summary>
    /// The adoption REFUSAL door (#2813). It dispatches a compile of the LIVE source, so it stamps
    /// the live snapshot — the one it was handed, never <c>def.CurrentSourceVersions</c>, which on
    /// the sources-watcher path is the value the same write is replacing.
    /// </summary>
    [Fact]
    public void The_adoption_refusal_door_stamps_the_live_snapshot()
    {
        var refused = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            new NodeTypeDefinition
            {
                CompilationStatus = CompilationStatus.Ok,
                RequestedSourceStampAt = System.DateTimeOffset.UtcNow,
                AdoptedSourceFingerprint = "aaaaaaaaaaaaaaaa",
                CurrentSourceFingerprint = "bbbbbbbbbbbbbbbb",
                // #3583 — a refusal is a MAJOR bump now; a fingerprint that merely differs holds
                // the build as StaleAdopted and dispatches through the same door (pinned in
                // BuildDeliveryHoldTest). This test is about the door's stamp, so it stays on the
                // refusal row.
                AdoptedModuleVersion = "1.0.0",
                CurrentModuleVersion = "2.0.0",
                CurrentSourceVersions = LiveSources,
                LatestAssemblyCollection = "assemblies",
                LatestAssemblyPath = "adopted/T.dll",
            },
            LiveSources,
            canCompileLocally: true,
            modulesHash: ModulesHash);

        refused.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused);
        refused.CompilationStatus.Should().Be(CompilationStatus.Pending);
        refused.DispatchedBuildInputs.Should().Be(
            NodeTypeCompilationHelpers.BuildInputsToken(ModulesHash, LiveSources),
            "a refusal drives a real compile of the live source — a release request arriving "
            + "during it asks for exactly that, so it is absorbable rather than a second compile");
    }

    /// <summary>
    /// The failed-verdict re-drive door (#1793). Two stamps, two different facts over the same
    /// token shape: <c>FailedBuildInputs</c> is what the standing failure was formed under,
    /// <c>DispatchedBuildInputs</c> is what THIS dispatch is for. Both are written together.
    /// </summary>
    [Fact]
    public void The_failed_verdict_redrive_door_stamps_the_dispatch_AND_keeps_the_verdict_stamp()
    {
        var node = new MeshNode("P", "T")
        {
            NodeType = MeshNode.NodeTypePath,
            Content = NeverCompiled() with
            {
                CompilationStatus = CompilationStatus.Error,
                CompilationError = "CS0103",
                // Formed under an OLDER module set, which is what makes the verdict stale and the
                // re-drive fire in the first place.
                FailedBuildInputs = NodeTypeCompilationHelpers.BuildInputsToken(
                    "mod-OLD", LiveSources),
            },
        };

        var redriven = NodeTypeCompilationHelpers.ApplyFailedVerdictRedrive(
            node, TypePath, ModulesHash, parkRegistry: null);

        var def = (NodeTypeDefinition)redriven.Content!;
        def.CompilationStatus.Should().Be(CompilationStatus.Pending);
        def.DispatchedBuildInputs.Should().Be(
            NodeTypeCompilationHelpers.BuildInputsToken(ModulesHash, LiveSources),
            "the re-drive dispatches against the LIVE inputs — recording nothing made every "
            + "release request arriving during the recovery compile park and compile again");
        def.FailedBuildInputs.Should().Be(
            NodeTypeCompilationHelpers.BuildInputsToken(ModulesHash, LiveSources),
            "the verdict stamp still moves to the live inputs in the same write — that is what "
            + "stops the re-drive scheduling another pass if the compile never writes back");
    }

    /// <summary>
    /// Force is still the escape hatch. Stamping more doors must not make an explicit user
    /// Compile absorbable — it always compiles.
    /// </summary>
    [Fact]
    public void A_FORCED_request_is_never_absorbed_by_a_stamped_door()
    {
        var dispatched = NodeTypeCompilationHelpers.DispatchPending(
            NeverCompiled() with { RequestedReleaseForce = true }, ModulesHash);

        NodeTypeCompilationHelpers
            .IsSatisfiedByInFlightCompile(dispatched, RequestedToken(dispatched))
            .Should().BeFalse("RequestedReleaseForce is the escape hatch every UI and MCP path sets");
    }
}
