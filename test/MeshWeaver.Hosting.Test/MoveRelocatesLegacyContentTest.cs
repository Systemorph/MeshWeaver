using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A MOVE carries what is ON DISK — Systemorph/MeshWeaver#6951, #6617, #6952.
///
/// <para>Measured on a production portal: feedback records stored before the content-schema guard
/// existed, as <c>{"$type":"Feedback","title":…,"message":…}</c> (a <c>$type</c> naming no type),
/// were re-typed to <c>Feedback/Feedback</c>. Their hand-over then issued a move into the shared
/// Inbox, and the move's COPY LEG — a create at the new path — was refused by the guard, which
/// judges a create as new content. The guard's own Update half has always admitted re-asserting the
/// bytes on disk, precisely so that "an existing broken node" stays movable; the create the copy
/// leg issues never got that exemption. Hundreds of refusals, every one a retry of the same
/// deterministic verdict, which the move reported as <see cref="NodeMoveRejectionReason.Unknown"/>
/// — so its caller could not tell it from a failure worth retrying.</para>
///
/// <para>Each claim has its control in the same mesh: the relocation is admitted only for the
/// source's stored content (a relocation carrying anything else is still judged, and refused), and
/// a move whose copy a validator refuses reports <see cref="NodeMoveRejectionReason.ValidationFailed"/>.</para>
/// </summary>
public class MoveRelocatesLegacyContentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string LegacyType = "LegacyGuarded";
    private const string RefusedTargetSegment = "refused-target";

    /// <summary>The declared content shape of the NodeType under test.</summary>
    public record LegacyGuardedContent
    {
        /// <summary>A plain label.</summary>
        public string? Label { get; init; }

        /// <summary>The report text.</summary>
        public string? Message { get; init; }
    }

    /// <summary>The pre-hardening shape: a <c>$type</c> naming no type plus an undeclared member.</summary>
    private const string LegacyJson = """{"$type":"Feedback","title":"Legacy report","message":"what broke"}""";

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(LegacyType)
            {
                Name = "Legacy Guarded",
                // An in-mesh (runtime-compiled) NodeType, as Feedback/Feedback is: the shape for which
                // the content-schema guard, not the discriminator guard, judges a foreign `$type`.
                Content = new NodeTypeDefinition
                {
                    Configuration = "// stands in for in-mesh source — the delegate below is what runs",
                },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<LegacyGuardedContent>())
            })
            .ConfigureServices(services => services
                .AddSingleton<INodeValidator>(new RefuseCreatesUnderSegmentValidator(RefusedTargetSegment)));

    [Fact(Timeout = 120_000)]
    public async Task MovingARecordStoredInAShapeTheGuardNowRefuses_RelocatesItVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        await ActivateTheNodeType(ct);
        var source = NewPath("legacy");
        var target = NewPath("inbox");
        await StoreBypassingTheGuard(source, LegacyJson, ct);

        var moved = await Move(source, target, ct);

        moved.Success.Should().BeTrue(
            $"a relocation carries the source's stored content — it writes nothing new, so the guard that "
            + $"refuses this shape as a NEW write must not strand it where it is; it answered: {moved.Error}");
        var landed = await StoredAt(target, ct);
        landed.Should().NotBeNull("the moved record is at the target");
        JsonElement.DeepEquals(Raw(landed!.Content), JsonSerializer.Deserialize<JsonElement>(LegacyJson))
            .Should().BeTrue("the relocation carries the stored content verbatim — it neither reshapes nor drops it");
        (await StoredAt(source, ct)).Should().BeNull("and nothing is left at the source");
    }

    /// <summary>
    /// The exemption is the SOURCE's content and nothing else: a relocation create carrying a dead
    /// letter that differs from what storage holds at the source is new content and is refused, and
    /// the same create without a relocation source is refused too. The positive case is asked of the
    /// same guard instance, so a guard that admitted every relocation fails here.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARelocationIsAdmittedOnlyForTheSourcesStoredContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await ActivateTheNodeType(ct);
        var source = NewPath("legacy");
        var target = NewPath("inbox");
        await StoreBypassingTheGuard(source, LegacyJson, ct);
        var guard = new ContentSchemaValidator(Mesh, NullLogger<ContentSchemaValidator>.Instance);

        var carried = await Judge(guard, target, LegacyJson, source, ct);
        carried.IsValid.Should().BeTrue("the relocated content IS the source's stored content");

        var altered = await Judge(guard, target,
            """{"$type":"Feedback","title":"Legacy report","message":"something else"}""", source, ct);
        altered.IsValid.Should().BeFalse("content the source does not hold is a new write of a refused shape");
        altered.ErrorMessage.Should().Contain("'Feedback'");

        var plain = await Judge(guard, target, LegacyJson, relocatedFrom: null, ct);
        plain.IsValid.Should().BeFalse("without a relocation source the same content is an ordinary create");
    }

    /// <summary>
    /// A copy leg a validator refuses is a DETERMINISTIC verdict — every retry meets it again — and
    /// the move must say so (<see cref="NodeMoveRejectionReason.ValidationFailed"/>), never
    /// <see cref="NodeMoveRejectionReason.Unknown"/>, which a caller has to treat as worth retrying.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AMoveWhoseCopyLegAValidatorRefuses_AnswersValidationFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = NewPath("plain");
        await NodeFactory.CreateNode(MeshNode.FromPath(source) with
        {
            Name = "plain",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var target = NewPath(RefusedTargetSegment);

        var moved = await Move(source, target, ct);

        moved.Success.Should().BeFalse("the validator refuses every create at the target");
        moved.RejectionReason.Should().Be(NodeMoveRejectionReason.ValidationFailed,
            "a create refused by a validator is the create's own typed verdict, and the move must carry it");
        (await StoredAt(source, ct)).Should().NotBeNull("a refused copy leg deletes nothing");
    }

    private Task<NodeValidationResult> Judge(
        ContentSchemaValidator guard, string path, string contentJson, string? relocatedFrom, CancellationToken ct)
    {
        var node = Legacy(path, contentJson);
        return guard.Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = node,
                Request = new CreateNodeRequest(node) { AuthorshipFrom = relocatedFrom },
            })
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
    }

    /// <summary>A well-formed instance activates the NodeType, so the mesh-wide content-type map
    /// knows its declared content type — the guard's instrument.</summary>
    private Task<MeshNode> ActivateTheNodeType(CancellationToken ct) =>
        NodeFactory.CreateNode(Legacy(NewPath("wellformed"), $$"""{"$type":"{{nameof(LegacyGuardedContent)}}","label":"ok"}"""))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    /// <summary>
    /// Stores the pre-hardening shape the only way it can exist today: written before the guard, i.e.
    /// straight to storage, past every create-path validator.
    /// </summary>
    private Task<MeshNode?> StoreBypassingTheGuard(string path, string contentJson, CancellationToken ct) =>
        Storage.Write(Legacy(path, contentJson), Mesh.JsonSerializerOptions)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MoveNodeResponse> Move(string source, string target, CancellationToken ct) =>
        ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode?> StoredAt(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private JsonElement Raw(object? content) => content switch
    {
        JsonElement element => element,
        null => default,
        _ => JsonSerializer.SerializeToElement(content, content.GetType(), Mesh.JsonSerializerOptions),
    };

    private static MeshNode Legacy(string path, string contentJson) => MeshNode.FromPath(path) with
    {
        Name = "Legacy",
        NodeType = LegacyType,
        State = MeshNodeState.Active,
        Content = JsonSerializer.Deserialize<JsonElement>(contentJson),
    };

    private static string NewPath(string prefix) =>
        $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
}

/// <summary>Refuses every create whose path carries <paramref name="segment"/> in its last segment.</summary>
internal sealed class RefuseCreatesUnderSegmentValidator(string segment) : INodeValidator
{
    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        => Observable.Return(context.Node.Id.StartsWith(segment, StringComparison.Ordinal)
            ? NodeValidationResult.Invalid($"creates at '{segment}…' are refused by this test")
            : NodeValidationResult.Valid());
}
