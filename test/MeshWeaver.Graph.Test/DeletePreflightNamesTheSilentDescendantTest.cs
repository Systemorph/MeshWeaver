using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Systemorph/MeshWeaver#1198, the BEHAVIOURAL half — a recursive delete's pre-flight fan-out
/// waited for every descendant under ONE budget, so a single unresponsive per-node hub consumed
/// the whole subtree's budget and refused the delete anonymously.
///
/// <para><b>The production line</b>, memex-cloud 2026-09-10T13:39:00Z, verbatim:</para>
/// <code>
/// [DeleteNode] timeout path=sglauser/AgenticBusiness stage=pre-validate-descendants
///     partial-deleted=0 unanswered=sglauser/AgenticBusiness/01-MeetYourCoworker/AskAdvisor, …
/// System.TimeoutException: [DeleteNode:pre-validate-descendants] 7 of 83 descendant(s) of
///     'sglauser/AgenticBusiness' did not answer ValidateDeleteRequest within 30s …
/// </code>
///
/// <para><b>Why the list was not enough.</b> #1294 added the <c>unanswered=</c> names, and they
/// are what makes the line readable at all. What they cannot do is ATTRIBUTE: 76 of those 83 leaves
/// had answered in milliseconds and still paid the silent ones' 30 s, and the refusal that reached
/// the caller was the STAGE's — one anonymous timeout over the whole fan-out, at the same rung the
/// operation itself is bounded at. The diagnostic half landed; the behavioural half is that one
/// unresponsive descendant must report ITSELF, inside the stage's budget, while its siblings
/// finish.</para>
///
/// <para><b>The repro.</b> A subtree of three descendants, one of which carries a deletion
/// validator that never emits — the leaf hub took the <c>ValidateDeleteRequest</c> and went silent,
/// which is exactly the shape a starved permission read produces (#1446) and exactly what a bare
/// <c>Observable.Timeout</c> reports. Nothing is widened and nothing is raced: the leg's own budget
/// ends the wait, and the assertions are about WHICH bound fired and what it SAID.</para>
/// </summary>
public class DeletePreflightNamesTheSilentDescendantTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RootId = "preflight-silent";
    private const string SilentId = "silent";

    /// <summary>
    /// The one configured value; every rung below it is derived by <c>MeshOperationOptions.Nest</c>,
    /// so nothing else has to be configured and the ordering cannot be set up wrong by this test.
    /// At 10 s the stage keeps 10 s, one fan-out LEG gets 5 s and the leaf's own answer 2.5 s —
    /// small enough to stay far inside xunit's method timeout, large enough that the healthy legs
    /// have no trouble answering.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Three, not one: the subject is that the SILENT leaf is named and the healthy ones are not,
    /// which a single-descendant subtree cannot tell apart from "the report echoes the plan".
    /// </summary>
    private static readonly ImmutableArray<string> DescendantIds =
        ["healthy-a", SilentId, "healthy-b"];

    private readonly SilentOnOnePathDeletionValidator validator =
        new($"{TestPartition}/{RootId}/{SilentId}");

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(validator)
            .AddSingleton(new MeshOperationOptions { Timeout = Budget }));

    [Fact]
    public async Task OneSilentDescendant_IsRefusedByName_WhileItsSiblingsAnswer()
    {
        var rootPath = $"{TestPartition}/{RootId}";
        var silentPath = $"{rootPath}/{SilentId}";

        await NodeFactory.CreateNode(
                new MeshNode(RootId, TestPartition) { Name = "Pre-flight root", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var id in DescendantIds)
            await NodeFactory.CreateNode(
                    new MeshNode(id, rootPath) { Name = id, NodeType = "Markdown" })
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        validator.Armed = true;

        // Producer -> test signal: the delete's ERROR arm completes an AsyncSubject the assertion
        // helpers await. A success emission leaves it empty and times the wait out, which is itself
        // the failure this test must report.
        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(rootPath).Subscribe(
            _ => { },
            ex =>
            {
                failure.OnNext(ex);
                failure.OnCompleted();
            });

        var reported = await failure.Should().Within(TestTimeouts.WriteConvergence).Emit(
            "one descendant's hub never answers the pre-flight, so the delete must be refused",
                cancellationToken: TestContext.Current.CancellationToken);

        Output.WriteLine(reported.Message);

        // THE SUBJECT. One leaf, by name — the node whose hub an operator has to go and look at.
        reported.Message.Should().Contain(silentPath,
            "the refusal must name the descendant that went silent — a fan-out-wide count "
            + "identifies nothing, which is what made the 2026-09-10 occurrence unactionable");
        reported.Message.Should().Contain("did not answer ValidateDeleteRequest within",
            "the report says what was asked of that node and was not answered");

        // POSITIVE CONTROL — this is the PER-LEG bound, not the stage backstop. Without it the
        // assertions above would also pass on the old shape, whose stage-level message names every
        // outstanding descendant and would therefore contain the silent path too.
        reported.Message.Should().Contain("its per-node hub never replied",
            "a leg that lapses reports itself; the stage backstop says 'the pre-flight fan-out "
            + "did not settle', which is a different sentence about a different failure");
        reported.Message.Should().NotContain("the pre-flight fan-out for",
            "reaching the STAGE backstop would mean the fan-out as a whole stalled — that is the "
            + "very outcome the per-leg bound exists to replace, so seeing it here is the "
            + "pre-fix behaviour wearing the new message");

        // NEGATIVE CONTROL — the healthy siblings are NOT named. They answered; telling an operator
        // to investigate them is the fan-out-wide report under a new label.
        foreach (var healthy in DescendantIds.Where(id => id != SilentId))
            reported.Message.Should().NotContain($"{rootPath}/{healthy}",
                "this sibling answered the pre-flight — naming it in the refusal would send an "
                + "operator to look at a node that did its job");

        // 🚨 An AVAILABILITY failure, never a verdict. MeshService maps ValidationFailed and
        // Unauthorized to UnauthorizedAccessException; anything that reaches the caller as one of
        // those tells a correctly-entitled user to go and request permissions they already hold
        // (#1446). A hub that did not speak decided nothing.
        reported.Should().BeOfType<InvalidOperationException>(
            "a descendant whose hub never answered is Unavailable — not a denial and not a "
            + "validation verdict");

        // POSITIVE CONTROL — the other legs really did run. If the fan-out had stopped at the
        // silent leaf, the siblings would never have been asked at all and "they are not named"
        // would be true for the wrong reason.
        foreach (var id in DescendantIds)
            validator.Asked.Should().Contain($"{rootPath}/{id}",
                "every descendant must have been posted its own ValidateDeleteRequest — the cap on "
                + "the fan-out bounds how many are in flight, it does not drop any");
        foreach (var healthy in DescendantIds.Where(id => id != SilentId))
            validator.Answered.Should().Contain($"{rootPath}/{healthy}",
                "the healthy legs must have COMPLETED while the silent one was still outstanding — "
                + "that is the behavioural claim: one unresponsive descendant no longer consumes "
                + "the whole subtree's budget");
        validator.Answered.Should().NotContain(silentPath,
            "the silent leaf is the one that never answered — if it did, this test is measuring "
            + "something else entirely");

        // And the subtree is UNTOUCHED: the pre-flight refuses before any storage side effect, so
        // this reads the STORE OF RECORD rather than the report about it. A message can be
        // internally consistent and still describe a state that never happened.
        var survivors = await NodeFactory
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{rootPath} scope:children"))
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        survivors.Items.Select(n => n.Path).Should().Contain(
            DescendantIds.Select(id => $"{rootPath}/{id}"),
            "the pre-flight runs before the commit, so a refusal there must leave every descendant "
            + "in storage — a partially destroyed subtree is the outcome the bulk-atomic "
            + "pre-flight exists to prevent");
    }
}

/// <summary>
/// A deletion validator that answers immediately for every node except ONE, where it returns a
/// sequence that never emits, never completes and never errors — the leaf hub accepted the
/// <c>ValidateDeleteRequest</c> and went silent. That is the state <c>Observable.Timeout</c>
/// reports and the one a poisoned/starved read actually produces (#1446); an ERRORING validator
/// would be answered, which is a different test.
///
/// <para>Instance state, never static: the mesh owns this singleton for the life of the test's
/// mesh, so nothing bleeds into another suite in the same process.</para>
/// </summary>
internal sealed class SilentOnOnePathDeletionValidator(string silentPath) : INodeValidator
{
    private readonly ConcurrentDictionary<string, byte> asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> answered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Off until the subtree is built: the creates must not be slowed, and the silence is only
    /// meaningful once the delete asks.
    /// </summary>
    public bool Armed { get; set; }

    /// <summary>Every path this validator was asked about, for a Delete.</summary>
    public IReadOnlyCollection<string> Asked => asked.Keys.ToArray();

    /// <summary>Every path it actually answered.</summary>
    public IReadOnlyCollection<string> Answered => answered.Keys.ToArray();

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var path = context.Node.Path;
        asked.TryAdd(path, 0);
        if (Armed && string.Equals(path, silentPath, StringComparison.OrdinalIgnoreCase))
            return Observable.Never<NodeValidationResult>();
        answered.TryAdd(path, 0);
        return Observable.Return(NodeValidationResult.Valid());
    }
}
