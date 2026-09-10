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
/// <para>🚨 <b>THE FIXTURE IS THE CROSS-PACKAGE COLLISION, NOT UNREADABLE JSON.</b> Both probe
/// contents are REGISTERED types (declared by their NodeTypes via <c>WithContentType</c>), so the
/// stream cache types them happily and nothing degrades. What makes the conversion fire is that
/// they share a SHORT NAME — <c>RetypeFrom.PackageContent(Title, Sequence)</c> and
/// <c>RetypeTo.PackageContent(Title, Reason)</c> — because <c>ObjectAsExtensions.As</c> recovers a
/// foreign runtime type exactly when <c>value.GetType().Name == type.Name</c>. That is the
/// production case: a discriminator is a package-local name, and one customer repo ships
/// <c>Currency</c> four times (see <c>IMeshContentTypeRegistry</c>). <c>Reason</c> is nullable, so
/// the other package's bytes deserialise CLEANLY: the validator receives
/// <c>RetypeTo.PackageContent("Original", null)</c> — the old title under a new record,
/// <c>Sequence</c> silently dropped, <c>Reason</c> invented — and a validator written the way the
/// fleet writes them (<c>ExistingNode.Content is TNew e &amp;&amp; Node.Content is TNew p</c>)
/// compares the proposal against that ghost and answers on it.</para>
///
/// <para>🚨 An earlier revision of this fixture seeded discriminator-less JSON instead. That
/// reddened <c>check-untyped-content.sh</c> — correctly: content nothing can read is a DIFFERENT
/// defect, the one that gate exists to report, and there is no allow-list for it on purpose. The
/// registered-but-foreign shape reproduces the same defect through the mechanism a running mesh
/// actually produces, so the fixture is closer to production, not further from it.</para>
///
/// <para>🚨 The pipeline can add NOTHING legitimate on this path.
/// <c>MeshNodeStreamExtensions.EnsureTypedContent</c> has ALREADY offered the snapshot the exact
/// recovery — <c>IMeshContentTypeRegistry.TryRecoverForNodeType(node.NodeType, …)</c>, keyed on
/// the node's OWN NodeType — before the pipeline ever sees it, and
/// <see cref="ARetypeStillGetsTheExactRecovery_WhenTheExistingNodeTypeDeclaresItsContentType"/>
/// measures that rather than asserting it from a code read.</para>
/// </summary>
public class UpdateRetypeExistingContentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // The probe NodeTypes DECLARE their content types (see RetypeTypeProvider), so nothing
        // degrades and check-untyped-content.sh has nothing to report. The defect is reproduced by
        // the SHORT-NAME collision between two packages' records, not by unreadable bytes.
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
    /// 🚨 THE STATE THE DEFECT LIVES IN — present, typed, and READABLE, just not as the type the
    /// proposal names. A LIVE instance of one package's record, declared by this node's NodeType,
    /// so the stream cache types it and nothing anywhere degrades.
    ///
    /// <para>The monolith rig will not manufacture this state by accident: a create hands the store
    /// a live CLR instance and the same process reads it straight back. Seeding the node with the
    /// OTHER package's record is what puts the pipeline in front of the cross-package case,
    /// deterministically and in one process.</para>
    /// </summary>
    private static MeshNode Stored(string id) => new(id, TestPartition)
    {
        Name = "Original",
        NodeType = RetypeTypeProvider.BeforeType,
        State = MeshNodeState.Active,
        Content = new RetypeFrom.PackageContent("Original", 5),
    };

    private static MeshNode ProposedRetyped(string id) => new(id, TestPartition)
    {
        Name = "Retyped",
        NodeType = RetypeTypeProvider.AfterType,
        State = MeshNodeState.Active,
        Content = new RetypeTo.PackageContent("Retyped", "the sanctioned repair route"),
    };

    /// <summary>
    /// The #3056 counterparty's seed: a same-short-named record from a THIRD declaration, standing
    /// in for the copy another collectible assembly compiled. The NodeType is unchanged by the
    /// update, so the pipeline's proposal-typed recovery must still convert it.
    /// </summary>
    private static MeshNode StoredForeign(string id) => new(id, TestPartition)
    {
        Name = "Original",
        NodeType = RetypeTypeProvider.BeforeType,
        State = MeshNodeState.Active,
        Content = new RetypeForeign.PackageContent("Original", 5),
    };

    private static MeshNode ProposedSameType(string id) => new(id, TestPartition)
    {
        Name = "Edited",
        NodeType = RetypeTypeProvider.BeforeType,
        State = MeshNodeState.Active,
        Content = new RetypeFrom.PackageContent("Edited", 6),
    };

    /// <summary>
    /// 🚨 Discriminator-less bytes, used by the exact-recovery measurement ALONE — and legitimate
    /// there precisely because they DO resolve: the node's NodeType declares its content type, so
    /// <c>TryRecoverForNodeType</c> types them and nothing is left unresolved at teardown. That is
    /// what makes them a measurement of the NodeType route rather than a seeded unreadable node.
    ///
    /// <para>🚨 Do NOT reach for this from the other tests. Content nothing can read is a
    /// DIFFERENT defect and <c>check-untyped-content.sh</c> reds the shard for it at teardown,
    /// with no allow-list by design.</para>
    /// </summary>
    private static JsonElement DiscriminatorLessBytes() =>
        JsonDocument.Parse("""{"title":"Original","sequence":5}""").RootElement.Clone();

    /// <summary>Discriminator-less content under the NodeType that DECLARES its content type.</summary>
    private static MeshNode StoredUnderRegisteredType(string id) => new(id, TestPartition)
    {
        Name = "Original",
        NodeType = RetypeTypeProvider.RegisteredBeforeType,
        State = MeshNodeState.Active,
        Content = DiscriminatorLessBytes(),
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

        // 🚨 Assert on the FULL name. Both records are called PackageContent — that collision IS
        // the mechanism — so a BeOfType failure would read "expected PackageContent, found
        // PackageContent" and name nothing. The namespaces are what tell the two apart.
        seen?.GetType().FullName.Should().Be(
            typeof(RetypeFrom.PackageContent).FullName,
            "an in-place NodeType change is precisely where the pipeline's own premise — 'same "
            + "node, same NodeType' — is false, so the PROPOSAL is not the type authority for the "
            + "EXISTING snapshot. ObjectAsExtensions.As recovers a foreign runtime type whenever "
            + "the SHORT name matches, and these two packages both call their record "
            + "PackageContent — so the old record round-trips into the proposed one and the "
            + "validator receives a well-formed instance of a state the node was never in, with "
            + "Sequence dropped and Reason defaulted");

        ((RetypeFrom.PackageContent)seen!).Sequence.Should().Be(5,
            "the snapshot a validator sees must still be the node's OWN content — the member the "
            + "proposed record does not declare is exactly what a wrong-authority conversion loses");
    }

    /// <summary>
    /// 🚨 THE COUNTERPARTY, and the reason the fix is a GATE rather than a removal. When the
    /// NodeType is unchanged the proposal IS the type authority, and typing the snapshot by it is
    /// the #3056 cure that <see cref="UpdateValidatorSeesTypedExistingContentTest"/> pins.
    /// Narrowing the recovery must not take that with it.
    ///
    /// <para>The seed is a same-short-named record from a THIRD declaration — the copy another
    /// collectible assembly compiled, which every NodeType recompile mints for real. The proposal
    /// carries the LOCAL record under the SAME NodeType, so the recovery must still convert it. If
    /// someone widened the gate to skip the recovery on every update, the validator would see the
    /// foreign record and this test would name it.</para>
    /// </summary>
    // 300_000 ms — see the note on the test above.
    [Fact(Timeout = 300_000)]
    public async Task AnUpdateKeepingTheNodeType_StillGetsTheSnapshotTypedByTheProposal()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";

        await MeshService.CreateNode(StoredForeign(id)).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the node to update must exist first");

        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(ProposedSameType(id)).Take(1).Timeout(TestTimeouts.Convergence).Await());

        var seen = await Validator.ObservedExistingContent
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        seen?.GetType().FullName.Should().Be(
            typeof(RetypeFrom.PackageContent).FullName,
            "with the NodeType unchanged the proposal carries a live instance of exactly the type "
            + "the existing content must have, so recovering the snapshot by it is the #3056 cure "
            + "— narrowing the recovery to non-retypes must not take that with it");
        ((RetypeFrom.PackageContent)seen!).Sequence.Should().Be(5,
            "and the recovery must carry the node's real stored values across, not defaults");
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

/// <summary>
/// The content type <see cref="RetypeTypeProvider.RegisteredBeforeType"/> declares, used only by
/// the exact-recovery measurement. Its NAME is deliberately unique — that test is about the
/// NodeType-keyed registry route, not about the short-name collision the other two exercise.
/// </summary>
/// <param name="Title">Free-form.</param>
/// <param name="Sequence">Free-form.</param>
public record RetypeBeforeContent(string Title, int Sequence);

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

/// <summary>
/// The static NodeTypes the retype moves between, plus their static partitions. 🚨 Every one
/// DECLARES its content type: nothing here may leave a node whose content the mesh cannot read,
/// or <c>check-untyped-content.sh</c> reds the shard at teardown — and rightly so, since that is a
/// different defect from the one these tests are about.
/// </summary>
internal sealed class RetypeTypeProvider : IStaticNodeProvider
{
    /// <summary>The NodeType the probe node is created with; declares RetypeFrom.PackageContent.</summary>
    public const string BeforeType = "RetypeBeforeProbe";

    /// <summary>The NodeType the update retypes it to; declares RetypeTo.PackageContent.</summary>
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
            // 🚨 BeforeType and AfterType declare records that share the short name
            // "PackageContent" on purpose — that collision is the mechanism the retype defect
            // rides. Each is registered, so neither ever degrades.
            Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration = name switch
            {
                AfterType => c => c.AddMeshDataSource(s => s.WithContentType<RetypeTo.PackageContent>()),
                RegisteredBeforeType => c => c.AddMeshDataSource(s => s.WithContentType<RetypeBeforeContent>()),
                _ => c => c.AddMeshDataSource(s => s.WithContentType<RetypeFrom.PackageContent>()),
            };
            yield return new MeshNode(name)
            {
                Name = name,
                NodeType = "NodeType",
                HubConfiguration = hubConfiguration,
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
