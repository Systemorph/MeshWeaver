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
/// 🚨 Every lifecycle request on the node-operations hub reaches a VERDICT inside the mesh
/// operation budget — never silence until the CALLER's request timeout (#6149, #6107, #6105, #5856,
/// #2254).
///
/// <para><b>The production shape.</b> Three request types, one trail: <c>ROUTED onTarget=True →
/// HANDLER_ENTER → (CREATE_CHAIN_SUBSCRIBED →) HANDLER_EXIT state=Processed</c>, then nothing for
/// the caller's whole 60 s. The handlers return <c>Processed()</c> at once and owe their reply from a
/// detached chain, and — unlike <c>HandleDeleteNodeRequest</c>, which bounds every stage at
/// <see cref="MeshOperationOptions.Timeout"/> — the create, copy and move chains carried NO bound of
/// their own. Any leg that went quiet (a validator, a guard's read, a storage read parked behind a
/// saturated pool) therefore left the requester with nothing: no verdict, no stage, and a timeout
/// whose cause the caller cannot know. Nested, it was worse: a move's copy leg and a copy's per-node
/// creates are issued with the SAME 60 s request timeout as their caller, whose clock started first,
/// so an inner answer could never arrive before the outer one gave up (#6105).</para>
///
/// <para><b>The repro.</b> A creation validator that, once armed for ONE path, returns a sequence that
/// never emits, completes or errors — exactly what a starved read looks like from inside the chain.
/// Nothing is raced and nothing is widened: the operation budget is set to
/// <see cref="Budget"/>, far below the hub's 60 s request timeout, and the assertion window is the
/// budget plus slack, so the PRE-FIX code (which answers only when the caller's 60 s request timeout
/// fires) fails every assertion window here.</para>
/// </summary>
public class LifecycleRequestsAlwaysReachAVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Rung 1 of the ladder for this mesh — the only configured value.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

    /// <summary>How long past the budget a verdict may take to travel back: one hop, generous.</summary>
    private static readonly TimeSpan Window = Budget + TimeSpan.FromSeconds(8);

    private readonly SilentCreationValidator silent = new();
    private readonly SilentPostCreationHandler silentHandler = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(silent)
            .AddSingleton<INodePostCreationHandler>(silentHandler)
            .AddSingleton(new MeshOperationOptions { Timeout = Budget }));

    [Fact(Timeout = 60000)]
    public async Task ACreateWhoseValidatorNeverAnswers_IsAnsweredUnavailable_NamingTheStage()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("stalled-create");
        silent.Silence(path);

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"CREATE {path}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(Window).Emit(
                "a create whose chain stalls must still be ANSWERED inside the operation budget",
                cancellationToken: ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable,
            "a create that reached no verdict decided nothing — it is not a validation refusal");
        response.Error.Should().Contain(path);
        response.Error.Should().Contain("validators",
            "the verdict names the stage that went silent, which is what the trail could not say");
        response.Error.Should().Contain($"validators: {nameof(SilentCreationValidator)} (",
            "the verdict names the VALIDATOR the chain was waiting on — 'validators' alone named "
            + "every registered validator at once (#6391)");
        silent.Asked.Should().Contain(path, "the stall really was the armed validator");
        (await StoredAt(path, ct)).Should().BeNull("the stall came before the write");
    }

    /// <summary>The negative control: the same validator, armed for ANOTHER path, lets a create through.</summary>
    [Fact(Timeout = 60000)]
    public async Task ACreateTheValidatorAnswers_StillSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        silent.Silence(NewPath("someone-else"));
        var path = NewPath("healthy-create");

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Should().Within(Window).Emit(cancellationToken: ct);

        response.Success.Should().BeTrue(response.Error ?? "nothing stalls this create");
        (await StoredAt(path, ct)).Should().NotBeNull();
    }

    /// <summary>
    /// #6391, the leg AFTER the write. "Its post-creation handlers had not finished" named every
    /// registered handler at once; the verdict now names the one the leg is waiting on, and its
    /// position among the handlers that apply to the node.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ACreateWhosePostCreationHandlerNeverCompletes_IsAnswered_NamingTheHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath("held-after-write");
        silentHandler.Silence(path);

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"CREATE {path}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(Window).Emit(
                "a create whose post-creation leg stalls must still be ANSWERED inside the operation budget",
                cancellationToken: ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable);
        response.Error.Should().Contain("outcome is unknown",
            "the row is written and a handler is still running — neither applied nor refused");
        response.Error.Should().Contain($"post-creation-handlers: {nameof(SilentPostCreationHandler)} (",
            "the verdict names the HANDLER the leg was waiting on — 'its post-creation handlers' "
            + "named every registered handler at once (#6391)");
        silentHandler.Asked.Should().Contain(path, "the stall really was the armed handler");
        (await StoredAt(path, ct)).Should().NotBeNull("the stall came after the write");
    }

    /// <summary>The negative control: the same handler, armed for ANOTHER path, lets a create through.</summary>
    [Fact(Timeout = 60000)]
    public async Task ACreateThePostCreationHandlerCompletes_StillSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        silentHandler.Silence(NewPath("someone-else"));
        var path = NewPath("handled-create");

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Should().Within(Window).Emit(cancellationToken: ct);

        response.Success.Should().BeTrue(response.Error ?? "nothing stalls this create");
        (await StoredAt(path, ct)).Should().NotBeNull();
    }

    [Fact(Timeout = 60000)]
    public async Task ACopyWhoseTargetCreateStalls_IsAnswered_NamingTheTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await Seed("copy-source", ct);
        var target = NewPath("copy-target");
        silent.Silence(target);

        var response = await ObserveNodeOperation(new CopyNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"COPY {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(Window).Emit(
                "a copy whose per-node create stalls must still be answered inside the budget (#6105)",
                cancellationToken: ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().NotBe(NodeCopyRejectionReason.SourceNotFound,
            "the source is right there — a stall is never 'not found' (#5856)");
        response.Error.Should().Contain(target, "the verdict names the create that went silent");
        (await StoredAt(source, ct)).Should().NotBeNull("a copy never touches its source");
    }

    [Fact(Timeout = 60000)]
    public async Task AMoveWhoseCopyLegStalls_IsAnsweredUnavailable_AndTheSourceStays()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await Seed("move-source", ct);
        var target = NewPath("move-target");
        silent.Silence(target);

        var response = await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(Window).Emit(
                "a move whose copy leg stalls must still be answered inside the budget (#6107)",
                cancellationToken: ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeMoveRejectionReason.Unavailable,
            "nothing refused the move — a leg went silent, so the honest answer is 'retry', and "
            + "never SourceNotFound for a source that exists (#5856)");
        response.Error.Should().Contain(target);
        (await StoredAt(source, ct)).Should().NotBeNull(
            "the delete leg runs only after a SUCCESSFUL copy, so a stalled copy leaves the source");
    }

    /// <summary>The negative control for the move: unarmed for this target, the move relocates.</summary>
    [Fact(Timeout = 60000)]
    public async Task AMoveNothingStalls_StillRelocates()
    {
        var ct = TestContext.Current.CancellationToken;
        silent.Silence(NewPath("someone-else"));
        var source = await Seed("free-source", ct);
        var target = NewPath("free-target");

        var response = await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Should().Within(Window).Emit(cancellationToken: ct);

        response.Success.Should().BeTrue(response.Error ?? "nothing stalls this move");
        (await StoredAt(target, ct)).Should().NotBeNull();
        (await StoredAt(source, ct)).Should().BeNull();
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

    private async Task<string> Seed(string prefix, CancellationToken ct)
    {
        var path = NewPath(prefix);
        await NodeFactory.CreateNode(Markdown(path))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        return path;
    }
}

/// <summary>
/// A creation validator that answers immediately for every path except the ones it has been told to
/// silence, where it returns a sequence that never emits, never completes and never errors — the
/// shape a starved read takes from inside the create chain. Instance state, never static.
/// </summary>
internal sealed class SilentCreationValidator : INodeValidator
{
    private readonly ConcurrentDictionary<string, byte> silenced = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> asked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Silence creates at <paramref name="path"/> from now on.</summary>
    public void Silence(string path) => silenced.TryAdd(path, 0);

    /// <summary>Every silenced path this validator was actually asked about.</summary>
    public IReadOnlyCollection<string> Asked => asked.Keys.ToArray();

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (!silenced.ContainsKey(context.Node.Path))
            return Observable.Return(NodeValidationResult.Valid());
        asked.TryAdd(context.Node.Path, 0);
        return Observable.Never<NodeValidationResult>();
    }
}

/// <summary>
/// A post-creation handler that completes at once for every node except the paths it has been told
/// to silence, where it returns a sequence that never emits, never completes and never errors — a
/// handler whose own write never lands. Instance state, never static.
/// </summary>
internal sealed class SilentPostCreationHandler : INodePostCreationHandler
{
    private readonly ConcurrentDictionary<string, byte> silenced = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> asked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Never complete for creates at <paramref name="path"/> from now on.</summary>
    public void Silence(string path) => silenced.TryAdd(path, 0);

    /// <summary>Every silenced path this handler was actually run for.</summary>
    public IReadOnlyCollection<string> Asked => asked.Keys.ToArray();

    /// <inheritdoc />
    public string NodeType => "Markdown";

    /// <inheritdoc />
    public IObservable<System.Reactive.Unit> Handle(MeshNode createdNode, string? createdBy)
    {
        if (!silenced.ContainsKey(createdNode.Path))
            return Observable.Empty<System.Reactive.Unit>();
        asked.TryAdd(createdNode.Path, 0);
        return Observable.Never<System.Reactive.Unit>();
    }
}
