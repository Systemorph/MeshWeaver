using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Every creation validator gives a create exactly ONE verdict, inside its own bound (#6391).
///
/// <para><b>The production shape.</b> On memex the platform-startup boot recording
/// (<c>Admin/PlatformVersion/_Activity/startup-...</c>) was answered only by the create's own 30 s
/// deadline: "reached no verdict ... stalled at stage 'validators'". The runner chained the
/// validators with <c>Concat</c> and gave none of them a terminal of its own, so a validator not
/// armed yet at boot (never answers) and one that emits from a live fold and stays subscribed (never
/// completes) held the stage until that deadline, which names the stage but never the validator,
/// and one that completed without emitting was read as a PASS.</para>
///
/// <para><b>What is pinned.</b> Silence and an empty completion are Unavailable refusals that NAME
/// the validator, reached at rung 2 of <see cref="MeshOperationOptions"/>, strictly before the
/// create's deadline, and nothing is written. A first emission is the verdict, whatever the
/// validator does after it, so a late-armed live validator lets the create commit. A slow validator
/// that answers inside its bound, and a validator that refuses, behave as before.</para>
/// </summary>
public class CreationValidatorAlwaysReachesAVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Rung 1 for this mesh; each validator's bound is Nest(Budget) = 5 s.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>How long past the budget a verdict may take to travel back.</summary>
    private static readonly TimeSpan Window = Budget + TimeSpan.FromSeconds(8);

    /// <summary>How late the late-armed validator answers: well inside its bound.</summary>
    private static readonly TimeSpan ArmingDelay = TimeSpan.FromSeconds(1);

    private readonly ScriptedCreationValidator scripted = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(scripted)
            .AddSingleton(new MeshOperationOptions { Timeout = Budget }));

    [Fact(Timeout = 60000)]
    public async Task AValidatorThatNeverAnswers_IsRefusedUnavailable_NamingIt_BeforeTheCreateDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("never-answers");
        scripted.Script(path, () => Observable.Never<NodeValidationResult>());

        var clock = Stopwatch.StartNew();
        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"CREATE {path}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(Window).Emit("a create whose validator never answers is still answered",
                cancellationToken: ct);
        clock.Stop();

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable,
            "a validator that gave no verdict decided nothing - that is not a validation refusal");
        response.Error.Should().Contain(nameof(ScriptedCreationValidator),
            "the refusal names the validator that went silent, which the create deadline cannot");
        response.Error.Should().Contain("validators");
        response.Error.Should().Contain(path);
        (clock.Elapsed < Budget).Should().BeTrue(
            $"the validator's own bound fires strictly inside the create deadline of {Budget}; the answer took {clock.Elapsed}");
        (await StoredAt(path, ct)).Should().BeNull("the refusal came before the write");
    }

    [Fact(Timeout = 60000)]
    public async Task AValidatorThatCompletesWithoutAVerdict_IsRefusedUnavailable_AndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("completes-empty");
        scripted.Script(path, () => Observable.Empty<NodeValidationResult>());

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"CREATE {path}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(Window).Emit(cancellationToken: ct);

        response.Success.Should().BeFalse("a validator that completed without a verdict decided nothing - it is never a pass");
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable);
        response.Error.Should().Contain(nameof(ScriptedCreationValidator));
        (await StoredAt(path, ct)).Should().BeNull("a check that decided nothing lets nothing through");
    }

    [Fact(Timeout = 60000)]
    public async Task AValidatorThatAnswersAndStaysSubscribed_LetsTheCreateCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("answers-stays-live");
        scripted.Script(path, () => Observable.Return(NodeValidationResult.Valid())
            .Concat(Observable.Never<NodeValidationResult>()));

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"CREATE {path}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(Window).Emit(cancellationToken: ct);

        response.Success.Should().BeTrue(response.Error ?? "the first emission is the verdict");
        (await StoredAt(path, ct)).Should().NotBeNull();
    }

    [Fact(Timeout = 60000)]
    public async Task ABootTimeValidatorThatArmsLate_AndStaysLive_LetsTheCreateCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("arms-late-live");
        scripted.Script(path, () => Observable.Timer(ArmingDelay)
            .Select(_ => NodeValidationResult.Valid())
            .Concat(Observable.Never<NodeValidationResult>()));

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"CREATE {path}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(Window).Emit(cancellationToken: ct);

        response.Success.Should().BeTrue(response.Error ?? "a validator armed late but inside its bound lets the create commit");
        (await StoredAt(path, ct)).Should().NotBeNull();
    }

    [Fact(Timeout = 60000)]
    public async Task ASlowValidatorThatAnswersInsideItsBound_StillLetsTheCreateCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("slow-answers");
        scripted.Script(path, () => Observable.Timer(ArmingDelay)
            .Select(_ => NodeValidationResult.Valid()));

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Should().Within(Window).Emit(cancellationToken: ct);

        response.Success.Should().BeTrue(response.Error ?? "a slow validator that answers inside its bound is not refused");
        (await StoredAt(path, ct)).Should().NotBeNull();
    }

    [Fact(Timeout = 60000)]
    public async Task AValidatorThatRefuses_StillRefusesWithItsOwnReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("refuses");
        scripted.Script(path, () => Observable.Return(NodeValidationResult.Invalid("scripted refusal")));

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Should().Within(Window).Emit(cancellationToken: ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
        response.Error.Should().Contain("scripted refusal");
        (await StoredAt(path, ct)).Should().BeNull();
    }

    private static MeshNode Markdown(string path) => MeshNode.FromPath(path) with
    {
        Name = path.Split('/')[^1],
        NodeType = "Markdown",
        State = MeshNodeState.Active,
    };

    private Task<MeshNode?> StoredAt(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private static string NewPath(string prefix) =>
        $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// A creation validator scripted per path; every path it was not scripted for is answered Valid
    /// at once. Instance state, never static.
    /// </summary>
    private sealed class ScriptedCreationValidator : INodeValidator
    {
        private readonly ConcurrentDictionary<string, Func<IObservable<NodeValidationResult>>> scripts =
            new(StringComparer.OrdinalIgnoreCase);

        public void Script(string path, Func<IObservable<NodeValidationResult>> verdict) => scripts[path] = verdict;

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context) =>
            scripts.TryGetValue(context.Node.Path, out var verdict)
                ? verdict()
                : Observable.Return(NodeValidationResult.Valid());
    }
}
