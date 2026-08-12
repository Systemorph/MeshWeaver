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
/// the KIND of failure and nothing else, and the delete pipeline bounds FIVE independent stages
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
/// </summary>
public class DeleteTimeoutStageDiagnosticsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The whole pipeline's budget. Small enough that a stage timeout is the fastest thing in the
    /// test, generous enough that node creation on a cold CI agent is nowhere near it.
    /// </summary>
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

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
            "the failure must name WHICH of the pipeline's five bounded stages ran out of time — "
            + "naming only the kind ('timeout') is what made #1198 unreadable");
        response.Error.Should().Contain(silent,
            "the fan-out waits for every descendant under one budget, so the ONE hub that went "
            + "silent is the entire diagnosis — it must be named, not counted");
    }

    /// <summary>
    /// 🚨 The other half: a commit that times out must report the paths it ALREADY deleted.
    /// Reporting 0 is not merely imprecise — it is the reading that sent #1198's triage to the
    /// pre-commit stages, because "partial-deleted=0" was taken to mean "made zero progress".
    /// Here a healthy sibling is genuinely removed before the stalled leaf holds up the cascade.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task CommitTimesOut_ReportsThePathsItAlreadyDeleted_NotZero()
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
        response.Error.Should().Contain("commit",
            "the stage name distinguishes 'nothing was touched' from 'the subtree is half gone' — "
            + "which is the difference between a retry and an investigation");
        (response.Log?.AffectedPaths ?? []).Should().Contain(healthy,
            "the healthy sibling WAS deleted before the cascade stalled; a timeout that discards "
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
