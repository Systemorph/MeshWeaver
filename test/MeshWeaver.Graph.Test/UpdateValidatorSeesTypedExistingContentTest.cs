using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// 🚨 <b>AN UPDATE VALIDATOR SEES THE EXISTING NODE TYPED LIKE THE PROPOSED ONE</b> — the
/// <c>NodeUpdatePipeline</c> contract that core #3056 broke and MeshWeaver.Plugins'
/// <c>UpdateNode_VersionDowngrade_ShouldFail</c> caught on 2026-09-02.
///
/// <para>The content type here is deliberately registered on NO hub (no <c>WithType</c>). Before
/// #3056 that did not matter: every hub registered every type it posted as a side effect of
/// <c>MessageService.Post</c> rendering each delivery through the logging options, so the hub that
/// created the node could re-type it when the update pipeline read it back. #3056 removed that
/// render — correctly, it was an OOM — and with it the accidental registration, so
/// <c>NodeValidationContext.ExistingNode.Content</c> reached validators as a <c>JsonElement</c>
/// while <c>Node.Content</c> was the caller's CLR instance. A validator comparing the two skipped
/// its own check and answered Valid: a refused write went through, with nothing logged as an
/// error.</para>
///
/// <para>The validator below is written the way validators across the fleet are written — a typed
/// comparison of both sides. That shape is the SUBJECT here, not a lapse: the pipeline owes a
/// validator content typed alike on both sides, and this test pins that it delivers it. The
/// observed CLR type is recorded separately so a regression names the shape it saw.</para>
/// </summary>
public class UpdateValidatorSeesTypedExistingContentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // 🚨 No WithType(typeof(VersionedContent), …) anywhere: the pipeline must not depend on it.
        builder.ConfigureServices(services => services
            .AddSingleton<IStaticNodeProvider, VersionedTypeProvider>()
            .AddSingleton<INodeValidator, NoVersionDowngradeValidator>());
        return base.ConfigureMesh(builder);
    }

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private NoVersionDowngradeValidator Validator =>
        Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<NoVersionDowngradeValidator>().Single();

    private static string NewId() => "vc" + Guid.NewGuid().ToString("N")[..8];

    private static MeshNode Versioned(string id, string title, int version) => new(id, TestPartition)
    {
        Name = title,
        NodeType = VersionedTypeProvider.TypeName,
        State = MeshNodeState.Active,
        Content = new VersionedContent(title, version),
    };

    [Fact(Timeout = 180000)]
    public async Task UpdateNode_Downgrade_IsRefused_BecauseTheValidatorSawTypedExistingContent()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Versioned(id, "High", 5)).Take(1)
            .Should().Within(60.Seconds()).Emit("the node to downgrade must exist first");

        var failure = await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(Versioned(id, "Downgraded", 3))
                .Take(1).Timeout(60.Seconds()).Await());

        var seen = await Validator.ObservedExistingContentType.FirstAsync().Timeout(60.Seconds()).Await();
        seen.Should().Be(typeof(VersionedContent),
            "the pipeline must hand validators the existing node with content typed like the "
            + "proposed one — a JsonElement here is the silent pass #3056 opened");

        failure.Should().BeOfType<UnauthorizedAccessException>(
            "a validator's ValidationFailed maps to UnauthorizedAccessException on the IMeshService surface");
        failure!.Message.Should().Contain("downgrade");

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds()).Await();
        after.ContentAs<VersionedContent>(Mesh.JsonSerializerOptions)!.Version.Should().Be(5,
            "a refused update must leave the node as it was");
    }

    [Fact(Timeout = 180000)]
    public async Task UpdateNode_Upgrade_StillLands()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Versioned(id, "Low", 1)).Take(1)
            .Should().Within(60.Seconds()).Emit("the node to upgrade must exist first");

        await MeshService.UpdateNode(Versioned(id, "Upgraded", 2)).Take(1)
            .Should().Within(60.Seconds()).Emit("an upgrade passes the validator and lands");

        var seen = await Validator.ObservedExistingContentType.FirstAsync().Timeout(60.Seconds()).Await();
        seen.Should().Be(typeof(VersionedContent));

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && n.ContentAs<VersionedContent>(Mesh.JsonSerializerOptions)?.Version == 2)
            .FirstAsync().Timeout(60.Seconds()).Await();
        after.Name.Should().Be("Upgraded");
    }
}

/// <summary>Content type for the typed-existing-node contract. Registered on NO hub on purpose.</summary>
public record VersionedContent(string Title, int Version);

/// <summary>
/// Refuses a version downgrade. Written as validators are written across the fleet — a typed
/// comparison of the existing and the proposed content — because that shape is what the pipeline
/// owes it; see the test class remarks.
/// </summary>
public sealed class NoVersionDowngradeValidator : INodeValidator
{
    private readonly ReplaySubject<Type?> observed = new();

    /// <summary>The CLR type of <c>ExistingNode.Content</c> as this validator saw it, per call.</summary>
    public IObservable<Type?> ObservedExistingContentType => observed;

    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Update];

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (context.ExistingNode is null || context.ExistingNode.NodeType != VersionedTypeProvider.TypeName)
            return Observable.Return(NodeValidationResult.Valid());

        observed.OnNext(context.ExistingNode.Content?.GetType());

        if (context.ExistingNode.Content is VersionedContent existing
            && context.Node.Content is VersionedContent proposed
            && proposed.Version < existing.Version)
            return Observable.Return(new NodeValidationResult(
                false,
                $"Cannot downgrade version from {existing.Version} to {proposed.Version}",
                NodeRejectionReason.ValidationFailed));

        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>The static NodeType the versioned nodes are instances of, plus its static partition.</summary>
internal sealed class VersionedTypeProvider : IStaticNodeProvider
{
    public const string TypeName = "VersionedProbe";

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return new MeshNode(TypeName)
        {
            Name = "Versioned probe",
            NodeType = "NodeType",
            HubConfiguration = c => c.AddMeshDataSource(),
            Content = new NodeTypeDefinition { Description = "Test NodeType for the typed-existing-node contract." },
        };
        yield return new MeshNode(TypeName, "Admin/Partition")
        {
            NodeType = "Partition",
            Name = $"{TypeName} (static)",
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = TypeName,
                DataSource = "static",
                Description = $"Test NodeType definition partition for '{TypeName}'",
            },
        };
    }
}
