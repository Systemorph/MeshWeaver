using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#6930 / review of #7015 — the boot sweep hands a type the untracked-module gate refuses
/// to the compile watcher, and READS the watcher's answer. Three properties of that read, pinned with
/// no mesh so each is deterministic:
/// <list type="bullet">
///   <item><description>it is correlated by VERSION with the hand-off write, so a recovery that
///   delivers the terminal snapshot directly — no <c>Pending</c> frame in between — is still the
///   answer, while a stale terminal replay of the record BEFORE the hand-off never is;</description></item>
///   <item><description>a hand-off write that fails is a fault, at once — never a wait that ends
///   as "not evaluated";</description></item>
///   <item><description>an <c>Ok</c> answer is reported as a success only when this replica's
///   store serves the build the record names — a held build whose bytes are gone is a
///   fault, not "baked".</description></item>
/// </list>
/// </summary>
public class AHandOffToTheCompileWatcherIsReadByVersionTest
{
    private const string TypePath = "untracked/Widget";
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(long version, CompilationStatus status, string? error = null) =>
        MeshNode.FromPath(TypePath) with
        {
            NodeType = MeshNode.NodeTypePath,
            Version = version,
            Content = new NodeTypeDefinition { CompilationStatus = status, CompilationError = error },
        };

    [Fact]
    public async Task ATerminalSnapshotDeliveredWithoutThePendingFrame_IsTheAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        // The type's stream replays the pre-hand-off record: a STALE terminal Error at v4.
        var frames = new BehaviorSubject<MeshNode?>(Node(4, CompilationStatus.Error, "stale"));
        // The hand-off write is acknowledged at v5 (Pending) — but that frame never reaches the
        // stream: lost-frame recovery delivers the watcher's settle at v7 directly.
        var handOff = Observable.Return(Node(5, CompilationStatus.Pending));

        var settled = DynamicTypePreWarmer.SettleAfterHandOff(frames, handOff, Options);
        var pending = settled.Should().Within(TestTimeouts.Quick)
            .Emit("the watcher's answer is recognised by its version, not by an intermediate frame", ct);
        frames.OnNext(Node(7, CompilationStatus.Error, "parked"));

        var answer = await pending;
        answer.CompilationError.Should().Be("parked", "the v7 settle is the answer — never the stale v4 replay");
    }

    [Fact]
    public async Task AStaleTerminalReplayOfTheRecordBeforeTheHandOff_IsNeverTheAnswer()
    {
        var frames = new BehaviorSubject<MeshNode?>(Node(4, CompilationStatus.Error, "stale"));
        var handOff = Observable.Return(Node(5, CompilationStatus.Pending));

        await DynamicTypePreWarmer.SettleAfterHandOff(frames, handOff, Options)
            .Should().NotEmit(TimeSpan.FromSeconds(1),
                "a terminal record at a version BELOW the hand-off predates it — reading it as the verdict "
                + "would report the previous answer as this one",
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AHandOffAcknowledgedAlreadySettled_IsTheAnswer()
    {
        var frames = new BehaviorSubject<MeshNode?>(Node(6, CompilationStatus.Error, "parked"));
        var handOff = Observable.Return(Node(6, CompilationStatus.Error, "parked"));

        var answer = await DynamicTypePreWarmer.SettleAfterHandOff(frames, handOff, Options)
            .Should().Within(TestTimeouts.Quick)
            .Emit("an acknowledgement at-or-past the write that is already terminal IS the settle",
                TestContext.Current.CancellationToken);
        answer.CompilationError.Should().Be("parked");
    }

    [Fact]
    public async Task AFailedHandOffWrite_IsAFaultAtOnce_NeverAWait()
    {
        var frames = new BehaviorSubject<MeshNode?>(Node(4, CompilationStatus.Error, "stale"));
        var handOff = Observable.Throw<MeshNode>(new InvalidOperationException("write refused"));

        var first = await DynamicTypePreWarmer.SettleAfterHandOff(frames, handOff, Options)
            .Materialize()
            .Should().Within(TestTimeouts.Quick)
            .Emit("a write that was never made requests nothing — the answer is the fault, now",
                TestContext.Current.CancellationToken);
        first.Kind.Should().Be(NotificationKind.OnError);
        first.Exception!.Message.Should().Be("write refused");
    }

    private static Func<IReadOnlyDictionary<string, NodeTypeDefinition?>, IObservable<NodeTypeBakeReport>> Probe(
        BakeState state) =>
        _ => Observable.Return(new NodeTypeBakeReport(
            ImmutableList.Create(new NodeTypeBakeEntry(TypePath, state, state.ToString())), "fw"));

    [Fact]
    public async Task AStalledStoreVerification_EndsAtTheBudget_AsAClassifiedOutcome()
    {
        var frames = new BehaviorSubject<MeshNode?>(Node(6, CompilationStatus.Ok));
        var handOff = Observable.Return(Node(6, CompilationStatus.Ok));
        var budget = TimeSpan.FromMilliseconds(500);

        var outcome = await DynamicTypePreWarmer.ReadHandOff(
                TypePath, frames, handOff, Options,
                _ => Observable.Never<NodeTypeBakeReport>(), budget)
            .Should().Within(TestTimeouts.Quick)
            .Emit("the per-type budget covers the store verification too — a stalled probe must not hold "
                  + "the type, and the sequential sweep behind it", TestContext.Current.CancellationToken);
        outcome.Status.Should().Be(PreWarmStatus.TimedOut, "an elapsed budget is 'not evaluated'");
    }

    private static readonly IReadOnlyDictionary<string, NodeTypeDefinition?> Adopted =
        ImmutableDictionary<string, NodeTypeDefinition?>.Empty.Add(TypePath, new NodeTypeDefinition());

    [Fact]
    public async Task AStalledAdoptionVerification_EndsAtTheBudget_Unverified_NeverAbsent()
    {
        var verified = await DynamicTypePreWarmer.VerifyAdoptions(
                Adopted, _ => Observable.Never<NodeTypeBakeReport>(), TimeSpan.FromMilliseconds(500), null)
            .Should().Within(TestTimeouts.Quick)
            .Emit("the verification runs before the sweep — a stalled probe must not hold the boot sweep",
                TestContext.Current.CancellationToken);
        verified.Unverified.Should().Contain(TypePath,
            "an adoption that could not be verified is UNVERIFIED — not absent (the watcher would park a "
            + "build this process holds) and not a fault reaching the routing fallback (the batch would compile it)");
        verified.Baked.Should().BeEmpty();
    }

    [Fact]
    public async Task AFaultedAdoptionVerification_IsUnverified_NotAnError()
    {
        var verified = await DynamicTypePreWarmer.VerifyAdoptions(
                Adopted, _ => Observable.Throw<NodeTypeBakeReport>(new IOException("share unreachable")),
                TestTimeouts.Quick, null)
            .Should().Within(TestTimeouts.Quick).Emit(cancellationToken: TestContext.Current.CancellationToken);
        verified.Unverified.Should().Contain(TypePath);

        var control = await DynamicTypePreWarmer.VerifyAdoptions(Adopted, Probe(BakeState.Baked), TestTimeouts.Quick, null)
            .Should().Within(TestTimeouts.Quick).Emit(cancellationToken: TestContext.Current.CancellationToken);
        control.Baked.Should().Contain(TypePath, "a probe that answers baked verifies the adoption");
        control.Unverified.Should().BeEmpty();
    }

    [Fact]
    public async Task AHeldBuildWhoseBytesAreGone_IsAFault_NotBaked()
    {
        var held = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Ok,
            BuildProvenance = BuildProvenance.StaleAdopted,
        };

        var outcome = await DynamicTypePreWarmer.OutcomeOfHandOff(TypePath, held, Probe(BakeState.BytesMissing))
            .Should().Within(TestTimeouts.Quick).Emit(cancellationToken: TestContext.Current.CancellationToken);
        outcome.ReachedUsableBuild.Should().BeFalse(
            "the hold judged the record, never the store — dependents and readiness must not build on "
            + "a build this replica cannot load");
        outcome.Status.Should().Be(PreWarmStatus.Faulted);
        outcome.Detail.Should().Contain("BytesMissing");

        var control = await DynamicTypePreWarmer.OutcomeOfHandOff(TypePath, held, Probe(BakeState.Baked))
            .Should().Within(TestTimeouts.Quick).Emit(cancellationToken: TestContext.Current.CancellationToken);
        control.Status.Should().Be(PreWarmStatus.AlreadyBaked, "the same hold over bytes the store serves IS baked");
    }
}
