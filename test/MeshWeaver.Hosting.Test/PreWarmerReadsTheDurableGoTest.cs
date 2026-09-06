using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The pre-warmer's SECOND DOOR: a durable GO already recorded for THIS image's fingerprint
/// must grant readiness even when the coordination node cannot be subscribed to.</b> Issue #3404.
///
/// <para><b>The defect.</b> Everything the pre-warmer knew about the build arrived through ONE
/// door — a <c>SubscribeRequest</c> to <c>Admin/Build</c>. On <c>memex-cloud</c>, 2026-09-06, two
/// pods exhausted that door's attempt budget and refused readiness at 11:44:32Z and 11:49:37Z,
/// holding the rollout on the previous image. The GO for the fingerprint those pods were running
/// had been written on an already-<c>Ready</c> build root at <b>11:28:56Z</b> — sixteen minutes
/// earlier. They refused a build that had already been approved, because they could not open a
/// subscription to read a verdict that was already durable. <c>BuildProtocolDriver.FollowGo</c> has
/// had two doors since #1440 (<c>ReadBuildGo</c> on the durable row, merged with
/// <c>ObserveBuildGo</c> on this cluster's mirror); only the pre-warmer's ENTRY was single-doored.
/// </para>
///
/// <para><b>What these cases discriminate.</b> The subscription door is shut in EVERY case — the
/// arms differ only in what the durable witness holds — so a pass can only come from the durable
/// read, never from the subscription. The refusal arms are what make the grant arm non-vacuous: an
/// implementation that simply stopped failing closed passes the first case and fails the other
/// three.</para>
///
/// <list type="bullet">
/// <item><see cref="ADurableGoForThisFingerprint_GrantsReadiness"/> — GO present ⇒ granted.</item>
/// <item><see cref="NoDurableGo_StillRefusesReadiness"/> — fail-closed survives.</item>
/// <item><see cref="AGoForAnotherFingerprint_StillRefusesReadiness"/> — a GO is PER-FINGERPRINT;
/// accepting a foreign one would certify a build this process is not running.</item>
/// <item><see cref="AnUnreachableNodeIsStillReportedLoudly_EvenWhenTheDurableGoGrants"/> — the
/// transport fault stays visible. The durable door changes the readiness VERDICT, never the
/// visibility of the fault, because that fault is the only signal the pod↔hub path is broken.</item>
/// </list>
///
/// <para>🚨 <b>Nothing here touches the follower's stand-down.</b> That protocol has a live
/// load-sensitive defect under separate investigation, and the durable door deliberately does not
/// route through it: this path never registered a claim (the registration is a
/// <c>GetMeshNodeStream(path).Update(…)</c> whose stream never delivered state, so no patch was
/// ever computed) and therefore has nothing to hand back. The probe is shared with the follower;
/// the stand-down is not.</para>
///
/// <para>The mesh is REAL (<see cref="MonolithMeshTestBase"/>): the witness is read back through
/// the mesh's own <c>IStorageAdapter</c> and its own <c>JsonSerializerOptions</c>, so a
/// <c>BuildState</c> that failed to round-trip would read as "no GO" and fail the grant arm rather
/// than passing silently. Only the fault is injected, exactly as
/// <c>BuildCoordinationRetryTest</c> injects one into <c>RetryUnreachableCoordination</c>.</para>
/// </summary>
public class PreWarmerReadsTheDurableGoTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>This pod's own framework fingerprint — the shape the incident carried.</summary>
    private const string MyFingerprint = "s2f227642b1c4419aa1b1a7d5d21a2f11";

    /// <summary>Some OTHER image's fingerprint. Its GO must never satisfy this pod.</summary>
    private const string AnotherFingerprint = "s9911ccddee0844a1bb2c3d4e5f607182";

    /// <summary>When the GO was written on the live root, per the incident.</summary>
    private static readonly DateTime GoWrittenAt = new(2026, 9, 6, 11, 28, 56, DateTimeKind.Utc);

    /// <summary>
    /// One dynamic NodeType for the post-GO share probe, so the grant arm proves the door actually
    /// opened onto the probe rather than merely declining to throw.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, NodeTypeDefinition?> Definitions =
        ImmutableDictionary<string, NodeTypeDefinition?>.Empty
            .Add("TestData/Widget", new NodeTypeDefinition());

    /// <summary>
    /// The verbatim production refusal: a <see cref="BuildCoordinationUnreachableException"/> whose
    /// inner is the hub's own request-budget <see cref="TimeoutException"/> naming
    /// <c>Admin/Build</c> — what <c>RetryUnreachableCoordination</c> throws after its attempts are
    /// exhausted, copied from the incident's log lines.
    /// </summary>
    private static BuildCoordinationUnreachableException TheSubscriptionDoorIsShut() =>
        new(
            "BuildProtocol: could not reach the build coordination node 'Admin/Build' in 3 "
            + "attempt(s) — the pre-warm sweep never started, so this process has verified NOTHING "
            + "about its NodeTypes on this image. This is a refusal, not a pass: readiness stays "
            + "refused and the rollout holds the previous image. A restart re-attempts.",
            new TimeoutException(
                "No response received in hub cache/UE4Wtq7CgkiAqGLfYRiJPQ within 00:01:00 for "
                + "request SubscribeRequest (id=ASCknHcTgkSMVRR-dy6F3Q) → target Admin/Build."));

    /// <summary>
    /// Writes the DURABLE build root exactly as a builder in another process leaves it — the row
    /// <c>ReadBuildGo</c> reads, which is the one record a peer cluster shares with the builder.
    /// </summary>
    /// <param name="ready">The per-fingerprint GO history to record, or null for a root with none.</param>
    private Task WriteTheDurableBuildRoot(ImmutableDictionary<string, BuildGo>? ready)
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var root = new MeshNode("Build", "Admin")
        {
            NodeType = BuildNodeType.NodeType,
            Content = new BuildState
            {
                Status = BuildStatus.Ready,
                FrameworkVersion = ready is null ? null : ready.Keys.FirstOrDefault(),
                Ready = ready,
            },
        };
        return storage.Write(root, Mesh.JsonSerializerOptions).Await();
    }

    private static ImmutableDictionary<string, BuildGo> Go(params string[] fingerprints) =>
        fingerprints.Aggregate(
            ImmutableDictionary<string, BuildGo>.Empty,
            (map, fp) => map.Add(fp, new BuildGo(fp, GoWrittenAt, Detail: "baked by a peer process")));

    private Task<IList<PreWarmOutcome>> DriveTheDoor(ILogger? logger = null) =>
        DriveTheDoor(Definitions, logger);

    private Task<IList<PreWarmOutcome>> DriveTheDoor(
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions, ILogger? logger = null) =>
        BuildProtocolDriver.WhenTheSubscriptionDoorIsShut(
                Observable.Throw<PreWarmOutcome>(TheSubscriptionDoorIsShut()),
                Mesh,
                MyFingerprint,
                definitions,
                Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>(),
                logger)
            .ToList()
            .Await();

    // ── the grant ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE FIX. The subscription door is shut and the durable root already carries the GO for
    /// THIS pod's fingerprint — written, as in the incident, before this process ever asked. The
    /// sweep therefore COMPLETES (readiness granted) instead of faulting (readiness refused), and
    /// it completes having probed the share: every emitted outcome is non-gating, which is exactly
    /// what lets <c>NodeTypeBakeGateState</c> reach <c>Complete</c> rather than <c>Regressed</c>.
    ///
    /// <para>The root also carries a GO for another image — the <c>Ready</c> map is history, never
    /// a boolean — so this pins that the right entry is selected rather than that the map is
    /// merely non-empty.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ADurableGoForThisFingerprint_GrantsReadiness()
    {
        await WriteTheDurableBuildRoot(Go(AnotherFingerprint, MyFingerprint));

        var outcomes = await DriveTheDoor();

        outcomes.Should().NotBeEmpty(
            "the durable GO opens the door ONTO the share probe — a door that grants without "
            + "probing would certify a share it never looked at");
        outcomes.Should().OnlyContain(o => !BuildProtocolDriver.IsGatingFailure(o),
            "a post-GO probe reports a still-pending type as not-evaluated, never as a regression: "
            + "this process has no verdict about it, which is not a verdict against it");
    }

    // ── the refusals: fail-closed must survive the fix ───────────────────────────────────────────

    /// <summary>
    /// 🚨 FAIL CLOSED, UNCHANGED. Both doors shut ⇒ the ORIGINAL
    /// <see cref="BuildCoordinationUnreachableException"/> propagates, so the sweep still faults,
    /// readiness is still refused, the rollout still holds the previous image — and the health
    /// payload still tells "no verdict" from "a bad verdict" through
    /// <see cref="BuildProtocolDriver.DescribesUnreachableCoordination"/>. This is the case that
    /// fails against an implementation which bought readiness by weakening the refusal.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task NoDurableGo_StillRefusesReadiness()
    {
        await WriteTheDurableBuildRoot(ready: null);

        var refuse = () => DriveTheDoor();

        var thrown = await refuse.Should().ThrowAsync<BuildCoordinationUnreachableException>(
            "a process that reached NEITHER door has verified nothing and must never claim it did");
        thrown.Which.Message.Should().Contain("verified NOTHING",
            "the refusal keeps the message an operator reads, unrewritten by the second door");
        BuildProtocolDriver.DescribesUnreachableCoordination(thrown.Which).Should().BeTrue(
            "the readiness payload must still classify this as an unreachable node — the one "
            + "failure worth restarting on — rather than as a bad verdict about this image");
    }

    /// <summary>
    /// 🚨 A GO IS PER-FINGERPRINT. The root is <c>Ready</c> and carries a GO — for a DIFFERENT
    /// image. Granting on it would certify a build this process is not running, which is strictly
    /// worse than the refusal it replaces: the whole reason the <c>Ready</c> map is keyed by
    /// framework version is that an old-image pod stays ready while a new image is still unproven.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AGoForAnotherFingerprint_StillRefusesReadiness()
    {
        await WriteTheDurableBuildRoot(Go(AnotherFingerprint));

        var refuse = () => DriveTheDoor();

        await refuse.Should().ThrowAsync<BuildCoordinationUnreachableException>(
            "someone else's GO says nothing about whether THIS image's NodeTypes build");
    }

    /// <summary>
    /// 🚨 A REAL NEGATIVE IS STILL FAIL-CLOSED, AND THE SHARE DOES NOT OVERTURN IT (#3404's second
    /// half). The witness is READABLE and ANSWERS that it carries no GO for this fingerprint, while
    /// this pod's own assembly store already holds a build for every NodeType asked about. Readiness
    /// stays refused.
    ///
    /// <para>This is the arm that keeps <c>WhenTheWitnessCannotBeRead</c>'s grant honest. The share
    /// probe settles the UNDETERMINED reading only — where nobody said anything at all — never a
    /// coordination answer: <c>NoGo</c> means no process has certified a build for this image, which
    /// is the protocol owner's call on the coordination node, not one a pod may overturn from its own
    /// volume. An implementation that granted whenever the share looked complete passes every
    /// <c>UndeterminedWitnessIsNotNoGoTest</c> case and fails this one.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task NoDurableGo_StillRefuses_EvenWhenTheShareIsFullyBaked()
    {
        await WriteTheDurableBuildRoot(ready: null);

        const string bakedType = "TestData/BakedWidget";
        const long stagedVersion = 11;
        await Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>()
            .Put(bakedType, stagedVersion, [0x4D, 0x5A, 0x00, 0x00], null)
            .Await();

        // Recorded assembly + bytes on the share ⇒ the probe classifies this Baked, so nothing is
        // gate-relevant. The refusal below can therefore only come from the witness's answer.
        var baked = ImmutableDictionary<string, NodeTypeDefinition?>.Empty
            .Add(bakedType, new NodeTypeDefinition
            {
                CompilationStatus = CompilationStatus.Ok,
                LatestAssemblyCollection = "nodetype-cache",
                LatestAssemblyPath = "staged.dll",
                LastCompiledVersion = stagedVersion,
            });

        var refuse = () => DriveTheDoor(baked);

        await refuse.Should().ThrowAsync<BuildCoordinationUnreachableException>(
            "a witness that ANSWERED 'no GO for this image' is a real negative, and a pod does not "
            + "certify a build from its own volume that the coordination node never approved");
    }

    // ── the fault stays visible ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The durable door changes the READINESS VERDICT, never the visibility of the fault. The
    /// pod↔<c>Admin/Build</c>-hub silence is a real, unfixed transport defect and remains the only
    /// signal that it is broken, so granting readiness on durable evidence must NOT read as
    /// "everything was fine". A door that granted quietly would delete the only evidence anyone
    /// has that the mesh has a routing or wedged-hub problem.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnUnreachableNodeIsStillReportedLoudly_EvenWhenTheDurableGoGrants()
    {
        await WriteTheDurableBuildRoot(Go(MyFingerprint));
        var recorder = new RecordingLogger();

        await DriveTheDoor(recorder);

        recorder.Entries.Should().Contain(
            e => e.Level >= LogLevel.Warning && e.Message.Contains("UNREACHABLE"),
            "the grant must SAY that the coordination node could not be reached — the readiness "
            + "verdict came from the durable witness, and the transport fault is still live");
        recorder.Entries.Should().Contain(
            e => e.Message.Contains("Admin/Build"),
            "naming what could not be reached is what makes the line actionable");
    }

    /// <summary>
    /// Captures what the driver logged. An <see cref="ILogger"/> is diagnostics, not mesh
    /// machinery — every other case here passes <c>null</c> for it, exactly as the driver's
    /// existing tests do.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        // Lock-free and immutable: the driver logs from whatever thread the Rx pipeline is on, and
        // the assertion reads from the test's. ImmutableInterlocked is the house shape for that.
        private ImmutableList<(LogLevel Level, string Message)> entries =
            ImmutableList<(LogLevel, string)>.Empty;

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => Volatile.Read(ref entries);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Disposable.Empty;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            ImmutableInterlocked.Update(
                ref entries,
                (list, entry) => list.Add(entry),
                (logLevel, formatter(state, exception)));
    }
}
