using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>AN IN-PLACE NODETYPE CHANGE MAKES THE PROPOSAL THE WRONG TYPE AUTHORITY</b> — issue
/// #3803, the other half of the contract <see cref="UpdateValidatorSeesTypedExistingContentTest"/>
/// pins.
///
/// <para><c>NodeUpdatePipeline.WithExistingContentTyped</c> used to type the EXISTING snapshot as
/// the PROPOSED content's CLR type, on a premise it states itself: <i>"same node, same
/// NodeType"</i>. An in-place retype is exactly where that premise is false — and
/// <c>System.Text.Json</c> ignores unmapped members by default, so the old node's JSON
/// deserialises CLEANLY into the new type whenever the new type's members are present or
/// defaultable. Validators are then handed a well-formed instance of the type the update is
/// PROPOSING, built out of the node's OLD bytes: a state the node was never in, whose absent
/// members read as defaults.</para>
///
/// <para>The two probe contents are built for exactly that: <c>Title</c> is shared and
/// <c>Reason</c> is nullable, so <c>{"title":"Original","sequence":5}</c> becomes a perfectly
/// valid <c>RetypeAfterContent("Original", null)</c>. Nothing throws, nothing logs an error, and a
/// validator written the way validators are written across the fleet —
/// <c>ExistingNode.Content is TAfter e &amp;&amp; Node.Content is TAfter p &amp;&amp; …</c> —
/// compares the proposal against the ghost and answers on it.</para>
///
/// <para>🚨 The pipeline can add NOTHING legitimate on this path.
/// <c>MeshNodeStreamExtensions.EnsureTypedContent</c> has ALREADY offered the snapshot the exact
/// recovery — <c>IMeshContentTypeRegistry.TryRecoverForNodeType(node.NodeType, …)</c>, keyed on
/// the node's OWN NodeType — before the pipeline ever sees it. A snapshot still untyped at this
/// point is untyped because nothing in the process can type it, and typing it as the PROPOSAL's
/// type is not recovery, it is manufacture.</para>
///
/// <para>The content types are registered on NO hub, deliberately: that is what makes the existing
/// snapshot arrive degraded, which is the state in which the manufacture succeeds silently. With
/// the type registered the snapshot arrives as a live CLR instance and <c>As</c> merely logs and
/// declines — the benign half of the same defect.</para>
/// </summary>
public class UpdateRetypeExistingContentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // 🚨 No WithType / WithContentType for either content record anywhere: the existing
        // snapshot must reach the pipeline degraded, which is the state the defect lives in.
        builder.ConfigureServices(services => services
            .AddSingleton<IStaticNodeProvider, RetypeTypeProvider>()
            .AddSingleton<INodeValidator, RetypeObservingValidator>());
        return base.ConfigureMesh(builder);
    }

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private RetypeObservingValidator Validator =>
        Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<RetypeObservingValidator>().Single();

    private static string NewId() => "rt" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 🚨 THE STATE THE DEFECT LIVES IN, written out rather than hoped for. This is what a node's
    /// content IS once it has been through the wire or through storage for a type no hub
    /// registered: <c>ObjectPolymorphicConverter.Write</c> takes its "type not registered,
    /// serialize without type information" branch, so the bytes carry NO <c>$type</c> at all and
    /// every subsequent read degrades to a bare <see cref="JsonElement"/> — for ever, on every
    /// hub, because there is no discriminator left for anything to resolve. That is the world
    /// #3056 opened and the reason <c>WithExistingContentTyped</c> exists.
    ///
    /// <para>The monolith rig does not produce it by itself: a create hands the store a LIVE CLR
    /// instance and the same process reads that instance straight back, so content never degrades
    /// and the pipeline's typing step is never reached. Creating the node with the degraded shape
    /// is what puts the pipeline in front of the state it was written for, deterministically and
    /// in one process.</para>
    /// </summary>
    private static JsonElement DegradedBefore() =>
        JsonDocument.Parse("""{"title":"Original","sequence":5}""").RootElement.Clone();

    private static MeshNode Stored(string id) => new(id, TestPartition)
    {
        Name = "Original",
        NodeType = RetypeTypeProvider.BeforeType,
        State = MeshNodeState.Active,
        Content = DegradedBefore(),
    };

    private static MeshNode ProposedRetyped(string id) => new(id, TestPartition)
    {
        Name = "Retyped",
        NodeType = RetypeTypeProvider.AfterType,
        State = MeshNodeState.Active,
        Content = new RetypeAfterContent("Retyped", "the sanctioned repair route"),
    };

    private static MeshNode ProposedSameType(string id) => new(id, TestPartition)
    {
        Name = "Edited",
        NodeType = RetypeTypeProvider.BeforeType,
        State = MeshNodeState.Active,
        Content = new RetypeBeforeContent("Edited", 6),
    };

    /// <summary>Same degraded content, but under the NodeType that DECLARES its content type.</summary>
    private static MeshNode StoredUnderRegisteredType(string id) => new(id, TestPartition)
    {
        Name = "Original",
        NodeType = RetypeTypeProvider.RegisteredBeforeType,
        State = MeshNodeState.Active,
        Content = DegradedBefore(),
    };

    /// <summary>
    /// 🚨 THE DEFECT. A NodeType-changing update must never hand a validator an "existing" content
    /// that is an instance of the type the update is PROPOSING — that value describes no state the
    /// node was ever in.
    /// </summary>
    // 300_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant,
    // and this outer bound only has to DOMINATE the inner TestTimeouts.Convergence waits so one
    // of them loses first and names what did not converge.
    [Fact(Timeout = 300_000)]
    public async Task AnInPlaceRetype_DoesNotHandValidatorsAnExistingContentOfTheProposedType()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";

        await MeshService.CreateNode(Stored(id)).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the node to retype must exist first");

        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        // The verdict is not the subject — what the pipeline HANDED the validator is.
        await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(ProposedRetyped(id)).Take(1).Timeout(TestTimeouts.Convergence).Await());

        var seen = await Validator.ObservedExistingContent
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        seen.Should().NotBeOfType<RetypeAfterContent>(
            "an in-place NodeType change is precisely where the pipeline's own premise — 'same "
            + "node, same NodeType' — is false, so the PROPOSAL is not the type authority for the "
            + "EXISTING snapshot. System.Text.Json ignores unmapped members, so the old node's "
            + "bytes deserialise cleanly into the proposed type and the validator receives a "
            + "well-formed instance of a state the node was never in, every member the old content "
            + "did not carry silently defaulted");

        seen.As<RetypeBeforeContent>(Mesh.JsonSerializerOptions)!.Sequence.Should().Be(5,
            "whatever shape the snapshot reaches a validator in, it must still be the node's OWN "
            + "content — recoverable as what it actually is");
    }

    /// <summary>
    /// 🚨 THE COUNTERPARTY, and the reason the fix is a GATE rather than a removal. When the
    /// NodeType is unchanged the proposal IS the type authority, and typing the degraded snapshot
    /// by it is the #3056 cure that <see cref="UpdateValidatorSeesTypedExistingContentTest"/>
    /// pins. Narrowing the recovery must not take that with it.
    /// </summary>
    // 300_000 ms — see the note on the test above.
    [Fact(Timeout = 300_000)]
    public async Task AnUpdateKeepingTheNodeType_StillGetsTheSnapshotTypedByTheProposal()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";

        await MeshService.CreateNode(Stored(id)).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the node to update must exist first");

        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(ProposedSameType(id)).Take(1).Timeout(TestTimeouts.Convergence).Await());

        var seen = await Validator.ObservedExistingContent
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        seen.Should().BeOfType<RetypeBeforeContent>(
            "with the NodeType unchanged the proposal carries a live instance of exactly the type "
            + "the existing content must have, and typing the degraded snapshot by it is the "
            + "#3056 cure — a JsonElement here is the silent validator pass that reopened");
        ((RetypeBeforeContent)seen!).Sequence.Should().Be(5);
    }

    /// <summary>
    /// 🚨 THE CLAIM THE FIX RESTS ON, MEASURED RATHER THAN INFERRED — and the answer to "does a
    /// retype now leave validators permissive?"
    ///
    /// <para>Leaving the snapshot alone on a retype is only correct if the EXACT recovery has
    /// already been tried by the time the pipeline sees it: <c>MeshNodeStreamExtensions.
    /// EnsureTypedContent</c> calls <c>IMeshContentTypeRegistry.TryRecoverForNodeType</c> keyed on
    /// the node's OWN NodeType, on every emission of the stream <c>NodeUpdatePipeline</c> reads.
    /// Reading that in the source is an inference. This measures it: the probe node's NodeType
    /// DECLARES its content type (<c>WithContentType&lt;RetypeBeforeContent&gt;</c>), the node is
    /// stored with the SAME degraded, discriminator-less JSON as the test above, and the update
    /// retypes it. If the NodeType-keyed recovery really runs upstream, the validator sees
    /// <c>RetypeBeforeContent</c> — recovered from a payload whose bytes name no type at all, so
    /// the ONLY thing that could have recovered it is the NodeType route.</para>
    ///
    /// <para>🚨 That also bounds what the fix gives up. A retype hands validators UNTYPED content
    /// only where nothing in the process can type it — which is #3056's own precondition, and the
    /// state in which the old code was equally broken (it manufactured a ghost of the proposal's
    /// type instead, or left the snapshot untyped anyway when the conversion threw). Wherever the
    /// content type is known ANYWHERE in the process, a retype's validators get it correctly typed
    /// and their typed comparison does NOT skip. The fix narrows the untyped window; it does not
    /// open one.</para>
    /// </summary>
    // 300_000 ms — see the note on the first test.
    [Fact(Timeout = 300_000)]
    public async Task ARetypeStillGetsTheExactRecovery_WhenTheExistingNodeTypeDeclaresItsContentType()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";

        await MeshService.CreateNode(StoredUnderRegisteredType(id)).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the node to retype must exist first");

        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(ProposedRetyped(id)).Take(1).Timeout(TestTimeouts.Convergence).Await());

        var seen = await Validator.ObservedExistingContent
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        seen.Should().BeOfType<RetypeBeforeContent>(
            "the stored bytes carry NO $type, so the only route that can type them is "
            + "IMeshContentTypeRegistry.TryRecoverForNodeType keyed on the node's OWN NodeType — "
            + "seeing the old type here MEASURES that the exact recovery runs on every emission of "
            + "the stream the update pipeline reads, which is what makes leaving the snapshot alone "
            + "on a retype correct rather than a silent pass. It also bounds the cost of the fix: a "
            + "retype only ever hands validators untyped content where nothing in the process can "
            + "type it at all");
        ((RetypeBeforeContent)seen!).Sequence.Should().Be(5,
            "and the recovered value is the node's real stored state, not a default-valued shell");
    }
}

/// <summary>The content a probe node is created with. Registered on NO hub on purpose.</summary>
/// <param name="Title">A member the retyped content also declares.</param>
/// <param name="Sequence">A member it does NOT declare — dropped silently by the manufacture.</param>
public record RetypeBeforeContent(string Title, int Sequence);

/// <summary>
/// The content the retyping update proposes. Registered on NO hub on purpose. <c>Reason</c> is
/// nullable so the old node's JSON deserialises into this type WITHOUT error — which is what makes
/// the wrong-authority conversion silent rather than loud.
/// </summary>
/// <param name="Title">Shared with <see cref="RetypeBeforeContent"/>.</param>
/// <param name="Reason">Absent from the old content, so it defaults to null.</param>
public record RetypeAfterContent(string Title, string? Reason);

/// <summary>
/// Records what <c>NodeValidationContext.ExistingNode.Content</c> actually was, per Update call on
/// a probe node. It reaches no verdict of its own: the subject here is what the PIPELINE hands a
/// validator, not what a validator decides.
/// </summary>
public sealed class RetypeObservingValidator : INodeValidator
{
    private readonly ReplaySubject<object?> observed = new();

    /// <summary>The existing content as this validator saw it, per Update call.</summary>
    public IObservable<object?> ObservedExistingContent => observed;

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Update];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (context.ExistingNode?.NodeType is RetypeTypeProvider.BeforeType
            or RetypeTypeProvider.RegisteredBeforeType)
            observed.OnNext(context.ExistingNode.Content);
        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>The two static NodeTypes the retype moves between, plus their static partitions.</summary>
internal sealed class RetypeTypeProvider : IStaticNodeProvider
{
    /// <summary>The NodeType the probe node is created with.</summary>
    public const string BeforeType = "RetypeBeforeProbe";

    /// <summary>The NodeType the update retypes it to.</summary>
    public const string AfterType = "RetypeAfterProbe";

    /// <summary>
    /// Same shape as <see cref="BeforeType"/>, but it DECLARES its content type — so the mesh-wide
    /// <c>IMeshContentTypeRegistry</c> can resolve it by NodeType path even for stored bytes that
    /// carry no <c>$type</c>. This is what makes the exact-recovery claim measurable.
    /// </summary>
    public const string RegisteredBeforeType = "RetypeRegisteredBeforeProbe";

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        foreach (var name in new[] { BeforeType, AfterType, RegisteredBeforeType })
        {
            var declaresContentType = name == RegisteredBeforeType;
            yield return new MeshNode(name)
            {
                Name = name,
                NodeType = "NodeType",
                HubConfiguration = declaresContentType
                    ? c => c.AddMeshDataSource(s => s.WithContentType<RetypeBeforeContent>())
                    : c => c.AddMeshDataSource(),
                Content = new NodeTypeDefinition { Description = "Test NodeType for the in-place retype contract." },
            };
            yield return new MeshNode(name, "Admin/Partition")
            {
                NodeType = "Partition",
                Name = $"{name} (static)",
                State = MeshNodeState.Active,
                Content = new PartitionDefinition
                {
                    Namespace = name,
                    DataSource = "static",
                    Description = $"Test NodeType definition partition for '{name}'",
                },
            };
        }
    }
}
