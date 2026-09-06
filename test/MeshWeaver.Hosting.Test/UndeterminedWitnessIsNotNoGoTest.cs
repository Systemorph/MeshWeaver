using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>An UNDETERMINED witness is not a witness that said "no GO".</b> Issue #3404, the half
/// #3410 left open.
///
/// <para><b>The defect.</b> #3410 gave the pre-warmer a second door: when the
/// <c>SubscribeRequest</c> to <c>Admin/Build</c> cannot be answered, read the durable build root
/// instead. That door had TWO branches — a GO, or no GO — and <c>ReadBuildGo</c> deliberately folds
/// THREE outcomes into its <c>null</c>: the row carries no GO, there is no durable store, and
/// <b>the read FAILED</b>. So a read that never completed was acted on, and LOGGED, as a definitive
/// negative: <i>"the durable witness carries no GO for framework X"</i>. The pod refused a build it
/// had not asked about, and told the operator the build had not been approved.</para>
///
/// <para>🚨 <b>Why that is the live case and not a corner.</b> The second door is NOT in a different
/// failure domain from the first. In the fleet's portal wiring <c>AddPartitionStorageHubs</c>
/// replaces <see cref="IStorageAdapter"/> with <c>RoutingProxyAdapter</c>, which serves the durable
/// read as <c>hub.Observe&lt;ReadNodeResponse&gt;(…)</c> — the SAME hub transport a
/// <c>SubscribeRequest</c> travels on, with the same 60 s request budget. Three of the five
/// candidates for #3404's silence (a routing loss, the deferred-queue ordering defect #3408, and a
/// root that stops emitting) take both doors down together, and the durable read then fails with
/// the SAME <see cref="TimeoutException"/> the subscription did. That is what these cases stage.
/// </para>
///
/// <para><b>The third state, and what the rollout does with it.</b> <c>BuildGoWitness</c> now has
/// three values and the door has three branches. On <c>Undetermined</c> the process does not guess
/// in either direction — it MEASURES, on the one witness the broken transport cannot touch: its own
/// <see cref="IAssemblyStore"/>, probed against the live framework identity. Grant only when every
/// previously-healthy NodeType is already baked there; refuse otherwise. That is STRICTER than the
/// GO branch, which grants and then reports still-pending types as non-gating.</para>
///
/// <list type="bullet">
/// <item><see cref="AnUnreadableWitness_WithTheShareAlreadyBaked_GrantsReadiness"/> — the fix.</item>
/// <item><see cref="AnUnreadableWitness_WithAPreviouslyHealthyTypeStillPending_RefusesReadiness"/>
/// — fail-closed survives where the measurement comes up short.</item>
/// <item><see cref="AnUnreadableWitness_IsNeverReportedAsAWitnessThatSaidNoGo"/> — the reporting
/// half: an undetermined read must never be narrated as an answer.</item>
/// <item><see cref="AFailedRead_ClassifiesAsUndetermined_NeverAsNoGo"/> — the classification at the
/// seam, pinned apart from what the door does with it.</item>
/// </list>
///
/// <para>The arm that makes the grant NON-VACUOUS lives next door:
/// <c>PreWarmerReadsTheDurableGoTest.NoDurableGo_StillRefuses_EvenWhenTheShareIsFullyBaked</c> — a
/// witness that ANSWERS "no GO" still refuses even with a fully-baked share, so an implementation
/// that simply granted whenever the share looked complete passes every case here and fails that one.
/// It has to live there because the witness must be READABLE, and this class's whole point is that
/// it is not.</para>
///
/// <para>The mesh is REAL (<see cref="MonolithMeshTestBase"/>) and so is the assembly store: the
/// baked arm PUTS bytes into this test class's own <see cref="IAssemblyStore"/> and the probe finds
/// them by the same key the production probe uses. Only ONE thing is injected — a
/// <see cref="TimeoutException"/> on the durable read of <c>Admin/Build</c>, shaped exactly like
/// the incident's — which is the same fault-injection discipline
/// <c>PreWarmerReadsTheDurableGoTest</c> and <c>BuildCoordinationRetryTest</c> already use.</para>
/// </summary>
public class UndeterminedWitnessIsNotNoGoTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>This pod's own framework fingerprint — the shape the incident carried.</summary>
    private const string MyFingerprint = "s2f227642b1c4419aa1b1a7d5d21a2f11";

    /// <summary>A dynamic NodeType whose bytes this test can actually stage in the store.</summary>
    private const string BakedType = "TestData/AlreadyBakedWidget";

    /// <summary>A dynamic NodeType that was healthy before this image and is NOT on the share.</summary>
    private const string PendingType = "TestData/PendingWidget";

    /// <summary>The store key's version half — arbitrary, but the same on Put and on probe.</summary>
    private const long StagedVersion = 7;

    /// <summary>
    /// 🚨 The durable read of <c>Admin/Build</c> fails, exactly as it does when the hub transport is
    /// the thing that is broken. Registered by <see cref="ConfigureMesh"/> over the REAL adapter, so
    /// every other read and every write in this mesh behaves normally — including the writes these
    /// tests use to stage the build root.
    /// </summary>
    private sealed class TheDurableReadFailsLikeTheTransportDid(IStorageAdapter inner) : IStorageAdapter
    {
        /// <summary>Set once the injected fault has actually been served — the positive signal that
        /// the decorator is live rather than shadowed by a later registration.</summary>
        private int served;

        /// <summary>Whether the injected fault was actually served at least once.</summary>
        public bool Served => Volatile.Read(ref served) > 0;

        public IObservable<DataChangeNotification> Changes => inner.Changes;

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        {
            if (!string.Equals(path, BuildNodeType.RootPath, StringComparison.OrdinalIgnoreCase))
                return inner.Read(path, options);

            // Deferred so the counter moves on SUBSCRIBE, not on composition — the door reads the
            // witness lazily and a test that counted at composition time would count a read that
            // never happened.
            return Observable.Defer(() =>
            {
                Interlocked.Increment(ref served);
                return Observable.Throw<MeshNode?>(new TimeoutException(
                    "No response received in hub cache/UE4Wtq7CgkiAqGLfYRiJPQ within 00:01:00 for "
                    + "request ReadNodeRequest (id=ASCknHcTgkSMVRR-dy6F3Q) → target Admin. The "
                    + "request may have been undeliverable or the target hub was not found."));
            });
        }

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(node, options);

        public IObservable<string> Delete(string path) => inner.Delete(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath) => inner.ListChildPaths(parentPath);

        public IObservable<bool> Exists(string path) => inner.Exists(path);

        public IObservable<bool> ExistsInWritableStorage(string path)
            => inner.ExistsInWritableStorage(path);

        public IObservable<string?> FindDeleteBlockingProvider(string path)
            => inner.FindDeleteBlockingProvider(path);

        public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
            => inner.ListDescendantPaths(rootPath);

        public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
            string fullPath, JsonSerializerOptions options)
            => inner.FindBestPrefixMatch(fullPath, options);

        public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
            string fullPath, JsonSerializerOptions options)
            => inner.ResolvePath(fullPath, options);

        public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
            => inner.ListPartitionSubPaths(nodePath);

        public IObservable<object> GetPartitionObjects(
            string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects,
            JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(
            string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }

    /// <summary>
    /// The base mesh, with the durable read of the build root wrapped in the injected fault.
    /// <c>ConfigureServices</c> runs against the LIVE service collection, so this decorates the
    /// registration <c>ConfigureMeshBase</c> has already made; <see cref="Witness"/> is resolved
    /// back out and asserted live in every case, so a wiring change that shadowed the decorator
    /// fails these tests loudly instead of passing them silently.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                var existing = services.Last(d => d.ServiceType == typeof(IStorageAdapter));
                services.Remove(existing);
                services.AddSingleton<IStorageAdapter>(sp =>
                    new TheDurableReadFailsLikeTheTransportDid(Materialize(existing, sp)));
                return services;
            });

    /// <summary>Builds the adapter the removed descriptor described, whichever form it took.</summary>
    private static IStorageAdapter Materialize(ServiceDescriptor descriptor, IServiceProvider sp)
        => (IStorageAdapter)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));

    private TheDurableReadFailsLikeTheTransportDid Witness =>
        (TheDurableReadFailsLikeTheTransportDid)Mesh.ServiceProvider
            .GetRequiredService<IStorageAdapter>();

    private IAssemblyStore Store => Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();

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
    /// A NodeType record that claims a live-framework build — so the probe asks the store for its
    /// bytes and the STORE decides between <c>Baked</c> and <c>BytesMissing</c>. No dependency
    /// record, so the bytes-win rule is what answers.
    /// </summary>
    private static NodeTypeDefinition RecordedAgainstThisFramework() => new()
    {
        CompilationStatus = MeshWeaver.Mesh.Services.CompilationStatus.Ok,
        LatestAssemblyCollection = "nodetype-cache",
        LatestAssemblyPath = "staged.dll",
        LastCompiledVersion = StagedVersion,
    };

    /// <summary>Puts bytes in the store under the same key the probe looks the type up by.</summary>
    private Task StageBytesOnTheShare(string typePath) =>
        Store.Put(typePath, StagedVersion, [0x4D, 0x5A, 0x00, 0x00], null).Await();

    /// <summary>
    /// Writes the DURABLE build root. Present in every case so the arms differ only in whether the
    /// witness can be READ — never in whether there is something to read.
    /// </summary>
    private Task WriteTheDurableBuildRoot(ImmutableDictionary<string, BuildGo>? ready)
    {
        // Through the INNER adapter: the decorator only fails the READ, but resolving the write off
        // the decorator would still be the decorator's job to forward, and going straight to what it
        // wraps keeps the staging honest about what it exercised.
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
        return Witness.Write(root, Mesh.JsonSerializerOptions).Await();
    }

    private static ImmutableDictionary<string, BuildGo> Go(params string[] fingerprints) =>
        fingerprints.Aggregate(
            ImmutableDictionary<string, BuildGo>.Empty,
            (map, fp) => map.Add(
                fp,
                new BuildGo(fp, new DateTime(2026, 9, 6, 11, 28, 56, DateTimeKind.Utc),
                    Detail: "baked by a peer process")));

    private Task<IList<PreWarmOutcome>> DriveTheDoor(
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions, ILogger? logger = null) =>
        BuildProtocolDriver.WhenTheSubscriptionDoorIsShut(
                Observable.Throw<PreWarmOutcome>(TheSubscriptionDoorIsShut()),
                Mesh,
                MyFingerprint,
                definitions,
                Store,
                logger)
            .ToList()
            .Await();

    // ── the grant ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE FIX. Both coordination doors are silent — the subscription is unreachable AND the
    /// durable witness times out exactly like it — so the GO is UNDETERMINED, not absent. The pod's
    /// own assembly store nevertheless holds a build for every previously-healthy NodeType on this
    /// framework identity, and readiness follows that measurement rather than a guess in either
    /// direction.
    ///
    /// <para>Against <c>src/</c> before this change the door had no third branch: <c>Undetermined</c>
    /// arrived as <c>ReadBuildGo</c>'s <c>null</c>, was read as "no GO", and this case threw.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnUnreadableWitness_WithTheShareAlreadyBaked_GrantsReadiness()
    {
        await WriteTheDurableBuildRoot(Go(MyFingerprint));
        await StageBytesOnTheShare(BakedType);

        var outcomes = await DriveTheDoor(
            ImmutableDictionary<string, NodeTypeDefinition?>.Empty
                .Add(BakedType, RecordedAgainstThisFramework()));

        Witness.Served.Should().BeTrue(
            "the fault must actually have been served — a decorator shadowed by a later "
            + "registration would make every case here pass having staged nothing");
        outcomes.Should().NotBeEmpty(
            "granting opens onto the share PROBE — a door that granted without probing would "
            + "certify a share it never looked at");
        outcomes.Should().OnlyContain(o => !BuildProtocolDriver.IsGatingFailure(o),
            "every previously-healthy type is baked on this pod's own store, which is exactly the "
            + "question the readiness gate asks");
        outcomes.Should().Contain(o => o.Status == PreWarmStatus.AlreadyBaked,
            "the grant rests on bytes the probe FOUND, not on an empty definition set");
    }

    // ── the refusals: fail-closed must survive the fix ───────────────────────────────────────────

    /// <summary>
    /// 🚨 FAIL CLOSED where the measurement comes up short. The witness is unreadable AND this pod's
    /// share still needs a NodeType that was healthy before this image. Nobody reachable is going to
    /// build it — this process could neither claim nor follow — so readiness stays refused and the
    /// rollout holds the previous image. "Finding nothing is not passing" is preserved verbatim;
    /// what changed is only that the refusal now rests on something the process established.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnUnreadableWitness_WithAPreviouslyHealthyTypeStillPending_RefusesReadiness()
    {
        await WriteTheDurableBuildRoot(Go(MyFingerprint));
        await StageBytesOnTheShare(BakedType);
        // PendingType is recorded against this framework but its bytes were never staged, so the
        // store answers a miss and the probe reports BytesMissing — needs-bake AND was-healthy.

        var refuse = () => DriveTheDoor(
            ImmutableDictionary<string, NodeTypeDefinition?>.Empty
                .Add(BakedType, RecordedAgainstThisFramework())
                .Add(PendingType, RecordedAgainstThisFramework()));

        var thrown = await refuse.Should().ThrowAsync<BuildCoordinationUnreachableException>(
            "a pod whose own share is short of a previously-healthy type, with nobody reachable to "
            + "build it, has verified nothing and must never claim it did");
        thrown.Which.Message.Should().Contain("verified NOTHING",
            "the refusal keeps the message an operator reads, unrewritten by the third state");
        BuildProtocolDriver.DescribesUnreachableCoordination(thrown.Which).Should().BeTrue(
            "the readiness payload must still classify this as an unreachable node — the one "
            + "failure worth restarting on — rather than as a bad verdict about this image");
    }

    /// <summary>
    /// 🚨 THE REPORTING HALF, which is the defect itself. An undetermined read must never be
    /// narrated as an answer. Before this change the refusal line said the durable witness
    /// <i>"carries no GO for framework X"</i> — a claim about a read that timed out — and an
    /// operator reading it concluded the build had never been approved. Now the line says the
    /// witness could not be READ, and carries the reason.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnUnreadableWitness_IsNeverReportedAsAWitnessThatSaidNoGo()
    {
        await WriteTheDurableBuildRoot(Go(MyFingerprint));
        var recorder = new RecordingLogger();

        var refuse = () => DriveTheDoor(
            ImmutableDictionary<string, NodeTypeDefinition?>.Empty
                .Add(PendingType, RecordedAgainstThisFramework()),
            recorder);
        await refuse.Should().ThrowAsync<BuildCoordinationUnreachableException>();

        recorder.Entries.Should().Contain(
            e => e.Level >= LogLevel.Error && e.Message.Contains("could not be READ"),
            "the refusal must say the witness was UNREADABLE — that is what actually happened");
        recorder.Entries.Should().NotContain(
            e => e.Message.Contains("carries no GO"),
            "🚨 THE DEFECT: a read that timed out must never be reported as a witness that "
            + "answered. That sentence sent an operator looking for a build nobody had refused");
        recorder.Entries.Should().Contain(
            e => e.Message.Contains("Admin/Build"),
            "naming what could not be reached is what makes the line actionable");
    }

    /// <summary>
    /// 🚨 THE READING ITSELF, at the seam the door switches on. The injected fault is served, and
    /// the reading it produces must be <see cref="BuildGoWitness.Undetermined"/> — never
    /// <see cref="BuildGoWitness.NoGo"/> — even though the durable row exists and would have
    /// answered had it been reachable. This is the classification the whole change rests on, pinned
    /// separately from what the door then does with it.
    ///
    /// <para>The complementary arm — a witness that ANSWERS "no GO" still refusing even with a
    /// fully-baked share — lives in <c>PreWarmerReadsTheDurableGoTest</c>, where the witness is
    /// readable.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AFailedRead_ClassifiesAsUndetermined_NeverAsNoGo()
    {
        await WriteTheDurableBuildRoot(Go(MyFingerprint));

        var reading = await Mesh.ReadBuildGoReading(MyFingerprint).Await();

        Witness.Served.Should().BeTrue("the injected fault must actually have been served");
        reading.Witness.Should().Be(BuildGoWitness.Undetermined,
            "a read that timed out established NOTHING — reporting it as NoGo is the defect");
        reading.Go.Should().BeNull("an undetermined reading carries no GO");
        reading.Error.Should().BeOfType<TimeoutException>(
            "the third state always carries its diagnostic — that is what makes it actionable");
        reading.Detail.Should().Contain("could not be read",
            "the detail is what the refusal line quotes back to the operator");
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
