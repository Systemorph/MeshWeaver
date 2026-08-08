using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// ATOMICITY OF A TOP-LEVEL CREATE (#638). A <c>Space</c> create is provision → write the root row
/// → grant the creator Admin (<c>SpacePostCreationHandler</c>, <c>FailsCreateOnError</c>). When the
/// grant does not land, the create must leave NOTHING behind.
///
/// <para>Before this fix the row STAYED and the caller got a Fail ("Node persisted but
/// post-creation handler failed"). That is the exact production residue this issue is named
/// after: a partition root nobody owns — RLS denies every user, so it cannot be written, deleted,
/// or repaired — and which the obvious remedy, creating it again, refuses with
/// <i>"Node already exists"</i>. The one thing that makes it recoverable is removing the row the
/// failed create wrote, so the caller can simply retry.</para>
///
/// <para>The grant is made to fail the way it really fails — by refusing the
/// <c>AccessAssignment</c> write itself — rather than by throwing from a fake handler, so the
/// production code path (handler → <c>meshService.CreateNode</c> → validators) is the one under
/// test.</para>
/// </summary>
public class SpaceCreateAtomicityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "AtomicSpace";
    private const string Creator = "Roland";   // TestUsers.Admin.ObjectId (DevLogin)

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                services.AddSingleton<INodeValidator>(new GrantRefusingValidator(Partition)));

    /// <summary>
    /// Refuses exactly one write: any <c>_Access</c> grant on the partition under test. That is
    /// the creator-Admin grant <c>SpacePostCreationHandler</c> writes, so the Space create fails
    /// in its post-creation step — the shape of the production incident (a grant that could not
    /// be written during pod churn), reproduced deterministically.
    /// </summary>
    private sealed class GrantRefusingValidator(string partition) : INodeValidator
    {
        internal const string Reason = "test harness: the creator-Admin grant is refused";

        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => context.Node.Path.StartsWith($"{partition}/_Access", StringComparison.OrdinalIgnoreCase)
                ? Observable.Return(NodeValidationResult.Invalid(Reason, NodeRejectionReason.ValidationFailed))
                : Observable.Return(NodeValidationResult.Valid());
    }

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private Task<IMessageDelivery<CreateNodeResponse>> CreateSpace() =>
        AwaitResponseAsync(
            new CreateNodeRequest(new MeshNode(Partition)
            {
                NodeType = "Space",
                Name = "Atomic Space",
                State = MeshNodeState.Active,
            })
            { CreatedBy = Creator },
            o => o.WithTarget(Mesh.Address)
                .WithAccessContext(new AccessContext { ObjectId = Creator, Name = Creator }));

    [Fact(Timeout = 60_000)]
    public async Task FailedCreatorGrant_RollsBackTheRow_AndTheCreateStaysRetryable()
    {
        var first = await CreateSpace();

        first.Message.Success.Should().BeFalse(
            "the creator-Admin grant is part of the Space create's contract, not a side effect");
        first.Message.Error.Should().Contain(GrantRefusingValidator.Reason,
            "the ORIGINAL cause must survive the rollback — never be replaced by it");
        first.Message.Error.Should().Contain("rolled back",
            "the response must say what happened to the half-written node");

        // The heart of #638: no ghost root is left behind.
        var row = await Storage.Read(Partition, Mesh.JsonSerializerOptions).Should().Within(15.Seconds()).Emit();
        row.Should().BeNull("a create that could not complete must leave nothing behind");

        // …which is what makes a retry meaningful: the caller hits the SAME real cause again,
        // never the dead end ("Node already exists") that made the production residue permanent.
        var second = await CreateSpace();
        second.Message.Success.Should().BeFalse();
        second.Message.Error.Should().Contain(GrantRefusingValidator.Reason);
        second.Message.Error.Should().NotContain("already exists",
            "the retry must not be blocked by the residue of the previous attempt");
    }

    [Fact(Timeout = 60_000)]
    public async Task FailedCreatorGrant_LeavesNoOrphanGrantEither()
    {
        await CreateSpace();

        // The grant is what failed, so there must be none — and no partition-definition either:
        // additional nodes are written only AFTER the handler's own work succeeded.
        (await Storage.Read($"{Partition}/_Access/{Creator}_Access", Mesh.JsonSerializerOptions)
                .Should().Within(15.Seconds()).Emit())
            .Should().BeNull();
        (await Storage.Read($"Admin/Partition/{Partition}", Mesh.JsonSerializerOptions)
                .Should().Within(15.Seconds()).Emit())
            .Should().BeNull("the partition definition rides behind the grant in the handler chain");
    }
}
