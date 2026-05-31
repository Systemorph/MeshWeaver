using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Comprehensive coverage for the four-phase <see cref="DeleteNodeRequest"/> orchestrator:
/// <list type="number">
/// <item><description><b>Collect</b> â€” root + (recursive) descendants.</description></item>
/// <item><description><b>Permission</b> â€” every node must have <see cref="Permission.Delete"/>.</description></item>
/// <item><description><b>Validate</b> â€” per-node <see cref="INodeValidator"/> chain;
/// errors block; warnings block without <see cref="DeleteNodeRequest.ConfirmWarnings"/>.</description></item>
/// <item><description><b>Commit</b> â€” bulk delete via storage adapter; all-or-nothing.</description></item>
/// </list>
///
/// Negative paths each assert (a) the correct <see cref="NodeDeletionRejectionReason"/>
/// and (b) that the <see cref="DeleteNodeResponse.Log"/> <see cref="ActivityLog"/>
/// lists every offending path so the UI can show the full picture. Positive paths
/// additionally verify that the deletion was atomic â€” nothing is written to storage
/// on a blocked delete, and on success every path really is gone.
/// </summary>
public class DeleteNodeBehaviorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMessageHub Client => _client ??= GetClient();
    private IMessageHub? _client;

    private const string Root = TestPartition + "/delparent";

    // â”€â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void SeedTree()
    {
        NodeFactory.CreateNode(new MeshNode("delparent", TestPartition)
        { Name = "Parent", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(new MeshNode("c1", Root)
        { Name = "C1", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(new MeshNode("c2", Root)
        { Name = "C2", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(new MeshNode("gc", $"{Root}/c1")
        { Name = "GC", NodeType = "Markdown" }).Should().Emit();
    }

    private DeleteNodeResponse Delete(DeleteNodeRequest req, TimeSpan timeout)
    {
        // The DeleteNodeRequest handler is registered on the mesh hub (see
        // MeshExtensions.AddDefaultMeshHandlers → WithHandler<DeleteNodeRequest>).
        // Post to Mesh.Address so the handler runs and we get a structured
        // DeleteNodeResponse — no per-node-path routing, no NotFound at the
        // routing layer for missing-node deletes.
        return Client.Observe(req, o => o.WithTarget(Mesh.Address))
            .Should().Within(timeout).Emit().Message;
    }

    private bool NodeExists(string path)
        => ReadNode(path).Should().Emit() != null;

    /// <summary>
    /// Catalog-aware "node is gone" check that does not suffer from the ancestor
    /// routing fallback in <see cref="MonolithMeshTestBase.ReadNode"/>. Subscribes to a
    /// subtree query and waits until <paramref name="path"/> drops out.
    /// </summary>
    private void WaitForNodeAbsence(string path)
        => WaitForQueryPathSet(
            $"path:{TestPartition} scope:subtree",
            set => !set.Contains(path));

    /// <summary>
    /// Reactive path-set wait: folds live query deltas into a running path set and
    /// blocks (≤ 60 s) until <paramref name="predicate"/> holds. Returns the satisfying set.
    /// </summary>
    private IReadOnlySet<string> WaitForQueryPathSet(
        string query, Func<IReadOnlySet<string>, bool> predicate)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        return MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan((IReadOnlySet<string>)paths, (acc, change) =>
            {
                var set = (HashSet<string>)acc;
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                {
                    set.Clear();
                    foreach (var n in change.Items) if (n.Path is { } p) set.Add(p);
                }
                else if (change.ChangeType is QueryChangeType.Added or QueryChangeType.Updated)
                {
                    foreach (var n in change.Items) if (n.Path is { } p) set.Add(p);
                }
                else if (change.ChangeType is QueryChangeType.Removed)
                {
                    foreach (var n in change.Items) if (n.Path is { } p) set.Remove(p);
                }
                return acc;
            })
            .Should().Within(ReadNodeTimeout).Match(predicate);
    }

    private static bool LogMentions(DeleteNodeResponse r, string path) =>
        r.Log?.Messages.Any(m => m.Message.Contains(path, StringComparison.Ordinal)) == true;

    // â”€â”€â”€ Phase 1: collection + basic reasons â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 20_000)]
    public void Leaf_Delete_SucceedsAndRemovesNode()
    {
        NodeFactory.CreateNode(
            new MeshNode("leaf", TestPartition) { Name = "Leaf", NodeType = "Markdown" }).Should().Emit();

        var response = Delete(
            new DeleteNodeRequest($"{TestPartition}/leaf"), 10.Seconds());

        response.Success.Should().BeTrue($"expected OK, got: {response.Error}");
        response.Log.Should().NotBeNull();
        response.Log!.Status.Should().Be(ActivityStatus.Succeeded);
        response.Log.AffectedPaths.Should().ContainSingle().Which.Should().Be($"{TestPartition}/leaf");

        // Catalog-bound wait — ReadNode would falsely "find" the leaf via
        // the TestPartition ancestor's MeshNodeReference reducer.
        WaitForNodeAbsence($"{TestPartition}/leaf");
    }

    [Fact(Timeout = 20_000)]
    public void Recursive_Delete_RemovesEntireSubtree()
    {
        SeedTree();

        var response = Delete(
            new DeleteNodeRequest(Root) { Recursive = true }, 10.Seconds());

        response.Success.Should().BeTrue($"expected OK, got: {response.Error}");
        response.Log!.AffectedPaths.Should().BeEquivalentTo(new[]
        {
            Root,
            $"{Root}/c1",
            $"{Root}/c2",
            $"{Root}/c1/gc"
        }, System.Text.Json.JsonSerializerOptions.Default);

        // Wait once for the catalog to drop ALL four — single subscription, not four.
        var paths = WaitForQueryPathSet(
            $"path:{TestPartition} scope:subtree",
            set => !set.Contains(Root)
                && !set.Contains($"{Root}/c1")
                && !set.Contains($"{Root}/c2")
                && !set.Contains($"{Root}/c1/gc"));
        paths.Should().NotContain(Root);
        paths.Should().NotContain($"{Root}/c1");
        paths.Should().NotContain($"{Root}/c2");
        paths.Should().NotContain($"{Root}/c1/gc");
    }

    [Fact(Timeout = 20_000)]
    public void NonRecursive_WithChildren_Fails_HasChildren_NothingDeleted()
    {
        SeedTree();

        var response = Delete(
            new DeleteNodeRequest(Root), 10.Seconds());

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.HasChildren);
        response.Log!.Status.Should().Be(ActivityStatus.Failed);

        // Nothing should have been deleted.
        NodeExists(Root).Should().BeTrue("parent must still exist after rejected delete");
        NodeExists($"{Root}/c1").Should().BeTrue("children must still exist");
    }

    [Fact(Timeout = 20_000)]
    public void Missing_Node_Fails_NotFound()
    {
        var response = Delete(
            new DeleteNodeRequest($"{TestPartition}/does-not-exist"), 10.Seconds());

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.NodeNotFound);
        response.Log!.Status.Should().Be(ActivityStatus.Failed);
        response.Log.Messages.Should().Contain(m => m.Message.Contains("not found"));
    }

    // â”€â”€â”€ Phase 2: permission checks â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 20_000)]
    public void NoDeletePermission_OnRoot_Fails_Unauthorized_AndLogsPath()
    {
        NodeFactory.CreateNode(
            new MeshNode("locked", TestPartition) { Name = "Locked", NodeType = "Markdown" }).Should().Emit();

        // Dedicated client hub whose AccessService is scoped to nobody â€”
        // the access context flows with the outbound message as Sender identity.
        var restrictedClient = GetClient();
        var clientAccess = restrictedClient.ServiceProvider.GetRequiredService<AccessService>();
        clientAccess.SetCircuitContext(new AccessContext
        {
            ObjectId = "nodelete-user",
            Name = "No Delete",
            Email = "nodelete@test.local",
            Roles = []
        });

        var path = $"{TestPartition}/locked";

        // The [RequiresPermission(Permission.Delete)] gate on DeleteNodeRequest runs at the
        // envelope layer and denies without invoking the handler â€” AwaitResponse receives a
        // DeliveryFailure which is surfaced as DeliveryFailureException. Either that, or the
        // in-handler Phase 2 check fires and we get a DeleteNodeResponse.Fail â€” the invariant
        // that matters for this test is: no matter which gate trips, the node is NOT deleted.
        Exception? caughtException = null;
        DeleteNodeResponse? failedResponse = null;
        try
        {
            var responseDelivery = restrictedClient.Observe(new DeleteNodeRequest(path), o => o.WithTarget(new Address(path))).Should().Emit();
            failedResponse = responseDelivery?.Message;
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }

        if (failedResponse != null)
        {
            failedResponse.Success.Should().BeFalse("Phase 2 permission denial must fail the response");
            (failedResponse.RejectionReason == NodeDeletionRejectionReason.Unauthorized
                || failedResponse.RejectionReason == NodeDeletionRejectionReason.ValidationFailed)
                .Should().BeTrue($"got {failedResponse.RejectionReason}");
        }
        else
        {
            caughtException.Should().NotBeNull(
                "denial must produce either a failed response or a DeliveryFailureException");
        }

        // Restore admin context so the existence check can actually see the node â€”
        // the shared AccessService singleton was flipped to nodelete-user above and
        // MeshQuery applies RLS.
        clientAccess.SetCircuitContext(TestUsers.Admin);

        NodeExists(path).Should().BeTrue(
            "node must not be deleted when caller lacks Delete permission");
    }

    // â”€â”€â”€ Phase 3: validator-based rejection â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 20_000)]
    public void Validator_RejectsRoot_Fails_ValidationFailed_LogsNodePath()
    {
        NodeFactory.CreateNode(
            new MeshNode("blocked", TestPartition)
            {
                Name = BlockingValidator.BlockedMarker,
                NodeType = "Markdown"
            }).Should().Emit();

        var response = Delete(
            new DeleteNodeRequest($"{TestPartition}/blocked"), 10.Seconds());

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.ValidationFailed);
        response.Log!.Status.Should().Be(ActivityStatus.Failed);
        LogMentions(response, $"{TestPartition}/blocked")
            .Should().BeTrue($"log should mention blocked path; got: {string.Join(" | ", response.Log.Messages.Select(m => m.Message))}");

        NodeExists($"{TestPartition}/blocked").Should().BeTrue(
            "rejected delete must leave the node in place");
    }

    [Fact(Timeout = 20_000)]
    public void Validator_RejectsDescendant_BlocksWholeSubtree_AllPathsListed()
    {
        NodeFactory.CreateNode(
            new MeshNode("mixed", TestPartition) { Name = "Mixed", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(
            new MeshNode("ok", $"{TestPartition}/mixed") { Name = "OK", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(
            new MeshNode("bad", $"{TestPartition}/mixed")
            {
                Name = BlockingValidator.BlockedMarker,
                NodeType = "Markdown"
            }).Should().Emit();

        var response = Delete(
            new DeleteNodeRequest($"{TestPartition}/mixed") { Recursive = true },
            10.Seconds());

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.ValidationFailed);

        // Bulk atomicity: nothing in the subtree should be deleted.
        NodeExists($"{TestPartition}/mixed").Should().BeTrue();
        NodeExists($"{TestPartition}/mixed/ok").Should().BeTrue();
        NodeExists($"{TestPartition}/mixed/bad").Should().BeTrue();

        // The ActivityLog should mention the offending descendant so the UI can show
        // the user exactly which node blocked the delete.
        LogMentions(response, $"{TestPartition}/mixed/bad").Should().BeTrue(
            $"bad path must appear in log; got: {string.Join(" | ", response.Log!.Messages.Select(m => m.Message))}");

        // AffectedPaths must list everything we attempted to delete.
        response.Log.AffectedPaths.Should().BeEquivalentTo(new[]
        {
            $"{TestPartition}/mixed",
            $"{TestPartition}/mixed/ok",
            $"{TestPartition}/mixed/bad"
        }, System.Text.Json.JsonSerializerOptions.Default);
    }

    // â”€â”€â”€ Phase 3: warnings + ConfirmWarnings round-trip â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 20_000)]
    public void Warnings_WithoutConfirm_Block_AndLogWarning()
    {
        NodeFactory.CreateNode(
            new MeshNode("warny", TestPartition)
            {
                Name = WarningValidator.WarnMarker,
                NodeType = "Markdown"
            }).Should().Emit();

        var response = Delete(
            new DeleteNodeRequest($"{TestPartition}/warny"), 10.Seconds());

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.WarningsRequireConfirmation);
        response.Log!.Messages.Should().Contain(m =>
            m.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning
            && m.Message.Contains(WarningValidator.WarnText));

        NodeExists($"{TestPartition}/warny").Should().BeTrue(
            "warnings must block without ConfirmWarnings");
    }

    [Fact(Timeout = 20_000)]
    public void Warnings_WithConfirm_Proceed_AndLogWarning()
    {
        NodeFactory.CreateNode(
            new MeshNode("warny2", TestPartition)
            {
                Name = WarningValidator.WarnMarker,
                NodeType = "Markdown"
            }).Should().Emit();

        var response = Delete(
            new DeleteNodeRequest($"{TestPartition}/warny2") { ConfirmWarnings = true },
            10.Seconds());

        response.Success.Should().BeTrue($"expected OK, got: {response.Error}");
        response.Log!.Status.Should().Be(ActivityStatus.Warning,
            "completed deletes that saw warnings should surface them via Status=Warning");
        response.Log.Messages.Should().Contain(m =>
            m.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning
            && m.Message.Contains(WarningValidator.WarnText));

        WaitForNodeAbsence($"{TestPartition}/warny2");
    }

    // â”€â”€â”€ Phase 4: bulk atomicity + ActivityLog â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 20_000)]
    public void Recursive_Delete_Log_ListsAllAffectedPathsAndSucceeded()
    {
        SeedTree();

        var response = Delete(
            new DeleteNodeRequest(Root) { Recursive = true }, 10.Seconds());

        response.Success.Should().BeTrue();
        response.Log!.Status.Should().Be(ActivityStatus.Succeeded);
        response.Log.AffectedPaths.Count.Should().Be(4);
        response.Log.Start.Should().BeBefore(response.Log.End!.Value);
    }

    // â”€â”€â”€ Custom test validators (wired in ConfigureMesh) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public sealed class BlockingValidator : INodeValidator
    {
        public const string BlockedMarker = "DO-NOT-DELETE";

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Delete];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        {
            if (context.Node.Name == BlockedMarker)
                return Observable.Return(
                    NodeValidationResult.Invalid($"'{context.Node.Path}' is protected", NodeRejectionReason.ValidationFailed));
            return Observable.Return(NodeValidationResult.Valid());
        }
    }

    public sealed class WarningValidator : INodeValidator
    {
        public const string WarnMarker = "CONFIRM-REQUIRED";
        public const string WarnText = "node may have downstream dependencies";

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Delete];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        {
            if (context.Node.Name == WarnMarker)
                return Observable.Return(NodeValidationResult.ValidWithWarning(
                    $"{WarnText} ({context.Node.Path})"));
            return Observable.Return(NodeValidationResult.Valid());
        }
    }

    /// <summary>
    /// Use <see cref="ConfigureMeshBase"/> (no root-level Publicâ†’Admin) so the
    /// permission-denied test can actually observe a denial. The admin user gets
    /// explicit access via <see cref="SetupAccessRightsAsync"/>.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<INodeValidator, BlockingValidator>();
                services.AddSingleton<INodeValidator, WarningValidator>();
                return services;
            });

    protected override async Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null)).FirstAsync().ToTask();
    }
}
