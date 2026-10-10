using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 The creation-validator runner takes EXACTLY ONE verdict from each validator (#6391) — the
/// <see cref="INodeValidator"/> contract is "emits exactly one result and completes", and the runner
/// holds every validator to it rather than trusting it.
///
/// <para><b>(a) The first emission IS the verdict.</b> The runner chains validators with
/// <c>Concat</c>, which subscribes to the next validator only when the current one COMPLETES. A
/// validator that answered <c>Valid</c> from a live read and stayed subscribed therefore held every
/// validator after it — and the create itself — until the create's own deadline answered it as a
/// stall. Repro: a validator that emits <c>Valid</c> and then never completes, followed by a
/// validator that refuses. Pre-fix the create is answered only at the budget, as a stall; post-fix
/// the later validator's refusal arrives at once, and with no refusing validator the create
/// commits.</para>
///
/// <para><b>(b) No verdict is not consent.</b> A validator that COMPLETES WITHOUT EMITTING was
/// skipped by <c>Concat</c>, so the create proceeded as if it had passed — the #2742 fail-open,
/// generalised from RLS to every validator. Repro: a validator that returns
/// <c>Observable.Empty</c>. Pre-fix the node is CREATED; post-fix the create is refused
/// <c>Unavailable</c>, naming the validator, and nothing is stored.</para>
///
/// <para>Nothing is raced and nothing is slept: the budget (<see cref="Budget"/>) is far below the
/// hub's request timeout, the assertions are on the verdict's CONTENT, and each repro has a negative
/// control on the same mesh (the same validators, unarmed for that path).</para>
/// </summary>
public class CreationValidatorGivesExactlyOneVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Rung 1 of the ladder for this mesh — the only configured value.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

    /// <summary>How long past the budget a verdict may take to travel back: one hop, generous.</summary>
    private static readonly TimeSpan Window = Budget + TimeSpan.FromSeconds(8);

    private readonly MisbehavingCreationValidator misbehaving = new();
    private readonly RefusingCreationValidator refusing = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    /// <remarks>Registration order is chain order: the misbehaving validator runs BEFORE the refusing one.</remarks>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(misbehaving)
            .AddSingleton<INodeValidator>(refusing)
            .AddSingleton(new MeshOperationOptions { Timeout = Budget }));

    /// <summary>(a) A validator that answers and stays subscribed no longer blocks the validator after it.</summary>
    [Fact(Timeout = 60000)]
    public async Task AValidatorThatAnswersAndStaysSubscribed_DoesNotBlockTheNextValidator()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("answers-then-stays-refused");
        misbehaving.AnswerThenStay(path);
        refusing.Refuse(path);

        var response = await Create(path, ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed,
            "the validator AFTER the one that stayed subscribed must be asked and its refusal must "
            + "arrive — pre-fix Concat never reached it and the create was answered as a stall");
        response.Error.Should().Contain(RefusingCreationValidator.Message);
        refusing.Asked.Should().Contain(path, "the next validator really was asked");
        (await StoredAt(path, ct)).Should().BeNull("a refused create writes nothing");
    }

    /// <summary>(a) The same validator, with nothing after it refusing: the create commits rather than stalling.</summary>
    [Fact(Timeout = 60000)]
    public async Task AValidatorThatAnswersValidAndStaysSubscribed_LetsTheCreateCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("answers-then-stays");
        misbehaving.AnswerThenStay(path);

        var response = await Create(path, ct);

        response.Success.Should().BeTrue(response.Error
            ?? "a Valid first emission is the verdict — staying subscribed must not hold the create");
        misbehaving.Asked.Should().Contain(path, "the armed validator really was asked");
        (await StoredAt(path, ct)).Should().NotBeNull();
    }

    /// <summary>(b) A validator that completes without a verdict refuses the create — it is never a pass.</summary>
    [Fact(Timeout = 60000)]
    public async Task AValidatorThatCompletesWithoutAVerdict_RefusesTheCreate_NamingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("completes-empty");
        misbehaving.CompleteEmpty(path);

        var response = await Create(path, ct);

        response.Success.Should().BeFalse("no verdict is not consent — pre-fix this node was CREATED");
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable,
            "nothing objected to the request; the check was never established");
        response.Error.Should().Contain(path);
        response.Error.Should().Contain(nameof(MisbehavingCreationValidator),
            "the refusal names the validator that gave no verdict — that is where the defect lives");
        misbehaving.Asked.Should().Contain(path, "the armed validator really was asked");
        (await StoredAt(path, ct)).Should().BeNull("a refused create writes nothing");
    }

    /// <summary>Negative control: the same validators, armed for OTHER paths, let a create through.</summary>
    [Fact(Timeout = 60000)]
    public async Task ACreateNoValidatorMisbehavesFor_StillSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        misbehaving.CompleteEmpty(NewPath("someone-else"));
        misbehaving.AnswerThenStay(NewPath("someone-else"));
        refusing.Refuse(NewPath("someone-else"));
        var path = NewPath("healthy-create");

        var response = await Create(path, ct);

        response.Success.Should().BeTrue(response.Error ?? "no validator is armed for this path");
        (await StoredAt(path, ct)).Should().NotBeNull();
    }

    private Task<CreateNodeResponse> Create(string path, CancellationToken ct) =>
        ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"CREATE {path}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(Window).Emit("every create is answered inside the operation budget",
                cancellationToken: ct);

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
}

/// <summary>
/// A creation validator that answers <c>Valid</c> and completes for every path except the ones it
/// has been armed for, where it breaks the one-verdict contract in one of two ways: it answers
/// <c>Valid</c> and then never completes (a hot fold), or it completes without ever emitting.
/// Instance state, never static.
/// </summary>
internal sealed class MisbehavingCreationValidator : INodeValidator
{
    private readonly ConcurrentDictionary<string, bool> armed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> asked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>At <paramref name="path"/>, answer Valid and then stay subscribed forever.</summary>
    public void AnswerThenStay(string path) => armed[path] = true;

    /// <summary>At <paramref name="path"/>, complete without emitting a verdict.</summary>
    public void CompleteEmpty(string path) => armed[path] = false;

    /// <summary>Every armed path this validator was actually asked about.</summary>
    public IReadOnlyCollection<string> Asked => asked.Keys.ToArray();

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (!armed.TryGetValue(context.Node.Path, out var answerThenStay))
            return Observable.Return(NodeValidationResult.Valid());
        asked.TryAdd(context.Node.Path, 0);
        return answerThenStay
            ? Observable.Return(NodeValidationResult.Valid()).Concat(Observable.Never<NodeValidationResult>())
            : Observable.Empty<NodeValidationResult>();
    }
}

/// <summary>A well-behaved creation validator that refuses the paths it has been armed for.</summary>
internal sealed class RefusingCreationValidator : INodeValidator
{
    /// <summary>The refusal text, so a test can recognise it in the verdict.</summary>
    public const string Message = "refused by RefusingCreationValidator";

    private readonly ConcurrentDictionary<string, byte> refused = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> asked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Refuse creates at <paramref name="path"/>.</summary>
    public void Refuse(string path) => refused.TryAdd(path, 0);

    /// <summary>Every armed path this validator was actually asked about.</summary>
    public IReadOnlyCollection<string> Asked => asked.Keys.ToArray();

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (!refused.ContainsKey(context.Node.Path))
            return Observable.Return(NodeValidationResult.Valid());
        asked.TryAdd(context.Node.Path, 0);
        return Observable.Return(NodeValidationResult.Invalid(Message));
    }
}
