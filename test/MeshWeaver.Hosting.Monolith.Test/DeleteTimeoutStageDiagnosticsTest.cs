using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 A TIMED-OUT DELETE MUST SAY WHERE IT TIMED OUT — issue #1198.
///
/// <para>memex-cloud logged exactly one line for a delete that never finished:
/// <c>[DeleteNode] timeout path=KmuBasics/Buchungsjournal partial-deleted=0</c>. That line names
/// the KIND of failure and nothing else, and the delete pipeline bounds SIX independent stages
/// with the same <see cref="MeshOperationOptions.Timeout"/> — so it could equally have been the
/// root read, the permission fold, the root validators, the storage enumeration, the pre-flight
/// descendant fan-out, or the commit. Worse, <c>partial-deleted=0</c> was not evidence of
/// anything: the commit's <c>Timeout</c> UNSUBSCRIBES from the fan-out, discarding the
/// deleted-path list, so a timed-out delete reported ZERO however much of the subtree it had
/// already removed. Both halves of that line are pinned here.</para>
///
/// <para>The stall is produced by a real, registered <see cref="INodeValidator"/> that never
/// emits for one specific node — the framework-legal way for a per-node hub to go silent, which
/// is precisely the shape the fan-out cannot survive: it posts
/// <see cref="ValidateDeleteRequest"/> at EVERY descendant and waits for ALL of them under ONE
/// budget, so one unresponsive hub refuses the whole delete.</para>
///
/// <para>🚨 <b>Naming the stage was only half of it.</b> Six SIBLING stages sharing one budget is
/// fine — one runs at a time and the name tells them apart. A NESTED level sharing that same value
/// is not: the levels overlap, the outer clock starts first, so the inner one — the only one that
/// knows which node and which read — could never fire. That is what
/// <see cref="CascadeLeg_GivesUpOnItsOwnNestedBudget_NamingItsStageAndItsProgress"/> pins, and why
/// the budget for anything nested is now DERIVED (see <see cref="MeshOperationOptions"/>) instead
/// of separately configured to the same number.</para>
/// </summary>
public class DeleteTimeoutStageDiagnosticsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The whole pipeline's budget. Small enough that a stage timeout is the fastest thing in the
    /// test, generous enough that node creation on a cold CI agent is nowhere near it.
    /// </summary>
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// What a level NESTED inside that budget gets — <c>MeshOperationOptions.NestedTimeout</c>,
    /// derived, never configured (#1198). Named here so the assertions can quote the number the
    /// cascade leg is supposed to give up on and would NOT have quoted before: one shared constant
    /// made the leaf's budget exactly equal to the commit stage holding it open, and the outer
    /// clock starts first, so the leaf could never be the one to speak.
    /// </summary>
    private static readonly TimeSpan NestedTimeout =
        new MeshOperationOptions { Timeout = OperationTimeout }.NestedTimeout;

    /// <summary>A node whose validators never answer, on ANY delete leg.</summary>
    private const string SilentSuffix = "-silent";

    /// <summary>A node whose validators answer the pre-flight but never the cascade leg.</summary>
    private const string CommitStallSuffix = "-stallcommit";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(s => s
                .AddSingleton(new MeshOperationOptions { Timeout = OperationTimeout })
                .AddSingleton<INodeValidator, StallingDeleteValidator>());

    /// <summary>
    /// A per-node hub that goes silent during the PRE-FLIGHT fan-out. Nothing is deleted, so the
    /// old log line was right about <c>partial-deleted=0</c> and useless about everything else.
    /// The response must now name the stage AND the descendant that did not answer — without the
    /// latter, an operator staring at a 400-node subtree has no way to find the wedged hub.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task PreFlightFanOut_TimesOut_NamesTheStageAndTheSilentDescendant()
    {
        var space = $"{TestPartition}/prevalidate";
        var silent = $"{space}/child{SilentSuffix}";
        await NodeFactory.CreateNode(new MeshNode("prevalidate", TestPartition)
        { Name = "Space", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode($"child{SilentSuffix}", space)
        { Name = "Silent", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        var response = (await Mesh
            .Observe(new DeleteNodeRequest(space) { Recursive = true, ConfirmWarnings = true },
                o => o.WithTarget(new Address(space)))
            .Should().Within(60.Seconds()).Emit()).Message;

        Output.WriteLine($"error: {response.Error}");

        response.Success.Should().BeFalse("the silent descendant never answered the pre-flight");
        response.Error.Should().Contain("pre-validate-descendants",
            "the failure must name WHICH of the pipeline's six bounded stages ran out of time — "
            + "naming only the kind ('timeout') is what made #1198 unreadable");
        response.Error.Should().Contain(silent,
            "the fan-out waits for every descendant under one budget, so the ONE hub that went "
            + "silent is the entire diagnosis — it must be named, not counted");
    }

    /// <summary>
    /// 🚨 THE NESTED BOUND FIRES FIRST — and reports the paths it ALREADY deleted.
    ///
    /// <para>Two halves of #1198 meet in this scenario. <b>The ordering:</b> a cascade leaf
    /// re-enters the delete handler from inside the root's commit stage, so the leaf's own six
    /// stage bounds are NESTED inside that stage. While both read
    /// <see cref="MeshOperationOptions.Timeout"/> the leaf could never win — equal budgets, and the
    /// root's clock starts first — so the answer was always the root's anonymous
    /// <c>stage=commit</c>, and which leaf stalled, and where, was discarded. The leaf now runs on
    /// the contracted rung, gives up first, and the response names ITS path and ITS stage.</para>
    ///
    /// <para><b>The progress:</b> reporting 0 deleted paths is not merely imprecise — it is the
    /// reading that sent #1198's triage to the pre-commit stages, because "partial-deleted=0" was
    /// taken to mean "made zero progress". Here a healthy sibling is genuinely removed before the
    /// stalled leaf holds up the cascade. Note this half now rides a DIFFERENT exception: once the
    /// leaf refuses first, the commit fails with the leaf's refusal rather than with its own stage
    /// timeout, and only the timeout used to carry the deleted-path list.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task CascadeLeg_GivesUpOnItsOwnNestedBudget_NamingItsStageAndItsProgress()
    {
        var space = $"{TestPartition}/commitstall";
        var healthy = $"{space}/healthy";
        var stalled = $"{space}/child{CommitStallSuffix}";
        await NodeFactory.CreateNode(new MeshNode("commitstall", TestPartition)
        { Name = "Space", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("healthy", space)
        { Name = "Healthy", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode($"child{CommitStallSuffix}", space)
        { Name = "Stalled", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        var response = (await Mesh
            .Observe(new DeleteNodeRequest(space) { Recursive = true, ConfirmWarnings = true },
                o => o.WithTarget(new Address(space)))
            .Should().Within(60.Seconds()).Emit()).Message;

        Output.WriteLine($"error: {response.Error}");
        Output.WriteLine($"affected: {string.Join(", ", response.Log?.AffectedPaths ?? [])}");

        response.Success.Should().BeFalse("the stalled leaf never completed its own delete");

        // 1️⃣ The LEAF answered, not the root. Its own stage name is in the message, which can only
        //    be there if the leaf's bound fired before the commit stage that encloses it.
        response.Error.Should().Contain("validate-root",
            "the leaf's own stage name is the whole diagnosis — 'the validator chain on THIS node "
            + "did not complete' is actionable, 'the commit did not drain' is not");
        response.Error.Should().Contain(stalled,
            "an operator staring at a large subtree needs the node that stalled, not a count");

        // 2️⃣ …and it gave up on the CONTRACTED rung, which is the fix itself. Quoting the number
        //    rules out the leaf having merely been lucky: on the shared budget it would read 15s,
        //    and it could not have arrived before the root's stage timeout at all.
        response.Error.Should().Contain($"exceeded {NestedTimeout.TotalSeconds:0}s",
            "a nested level must run on MeshOperationOptions.NestedTimeout, not on the operation "
            + "budget its caller is already holding open (#1198)");
        response.Error.Should().NotContain("did not drain within",
            "that phrase is the ROOT's commit-stage timeout — the very anonymous answer the "
            + "contracted rung exists to pre-empt");

        // 3️⃣ The progress survives the leaf's refusal, not just the stage timeout.
        (response.Log?.AffectedPaths ?? []).Should().Contain(healthy,
            "the healthy sibling WAS deleted before the cascade stalled; a failure that discards "
            + "that fact reports partial-deleted=0 over a half-deleted subtree (#1198)");
    }

    /// <summary>
    /// Never emits for the node under test — the framework-legal way to make one per-node hub go
    /// silent. <c>RunDeletionValidatorsObs</c> runs validators via <c>Concat</c>, so a validator
    /// that does not complete means the hub posts no <see cref="ValidateDeleteResponse"/> at all.
    ///
    /// <para>The two legs are told apart by <see cref="DeleteNodeRequest.CascadeRootPath"/>: the
    /// pre-flight runs against a FABRICATED <c>DeleteNodeRequest(path)</c> that carries none,
    /// while a cascade leaf's own delete carries the original root.</para>
    /// </summary>
    private sealed class StallingDeleteValidator : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Delete];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        {
            var path = context.Node.Path ?? string.Empty;
            var isCascadeLeg = context.Request is DeleteNodeRequest { CascadeRootPath: not null };
            var stall = path.EndsWith(SilentSuffix, StringComparison.OrdinalIgnoreCase)
                        || (isCascadeLeg
                            && path.EndsWith(CommitStallSuffix, StringComparison.OrdinalIgnoreCase));
            return stall
                ? Observable.Never<NodeValidationResult>()
                : Observable.Return(NodeValidationResult.Valid());
        }
    }
}
