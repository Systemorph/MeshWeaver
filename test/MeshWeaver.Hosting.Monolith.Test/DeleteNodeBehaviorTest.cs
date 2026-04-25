using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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

    private async Task SeedTreeAsync(CancellationToken ct)
    {
        await NodeFactory.CreateNode(new MeshNode("delparent", TestPartition)
        { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("c1", Root)
        { Name = "C1", NodeType = "Markdown" });
        await NodeFactory.CreateNode(new MeshNode("c2", Root)
        { Name = "C2", NodeType = "Markdown" });
        await NodeFactory.CreateNode(new MeshNode("gc", $"{Root}/c1")
        { Name = "GC", NodeType = "Markdown" });
    }

    private async Task<DeleteNodeResponse> DeleteAsync(
        DeleteNodeRequest req, TimeSpan timeout, CancellationToken ct)
    {
        var resp = await AwaitResponseAsync(req,
            o => o.WithTarget(new Address(req.Path)),
            hub: Client,
            ct: ct);
        return resp.Message;
    }

    private async Task<bool> NodeExistsAsync(string path, CancellationToken ct)
    {
        var node = await ReadNodeAsync(path, ct);
        return node != null;
    }

    /// <summary>
    /// Catalog-aware "node is gone" check that does not suffer from the ancestor
    /// routing fallback in <see cref="ReadNodeAsync"/>. Subscribes to a
    /// subtree query and waits until <paramref name="path"/> drops out.
    /// </summary>
    private Task WaitForNodeAbsenceAsync(string path, CancellationToken ct)
        => WaitForQueryPathSetAsync(
            $"path:{TestPartition} scope:subtree",
            set => !set.Contains(path),
            ct);

    private static bool LogMentions(DeleteNodeResponse r, string path) =>
        r.Log?.Messages.Any(m => m.Message.Contains(path, StringComparison.Ordinal)) == true;

    // â”€â”€â”€ Phase 1: collection + basic reasons â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 20_000)]
    public async Task Leaf_Delete_SucceedsAndRemovesNode()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(
            new MeshNode("leaf", TestPartition) { Name = "Leaf", NodeType = "Markdown" });

        var response = await DeleteAsync(
            new DeleteNodeRequest($"{TestPartition}/leaf"), 10.Seconds(), ct);

        response.Success.Should().BeTrue($"expected OK, got: {response.Error}");
        response.Log.Should().NotBeNull();
        response.Log!.Status.Should().Be(ActivityStatus.Succeeded);
        response.Log.AffectedPaths.Should().ContainSingle().Which.Should().Be($"{TestPartition}/leaf");

        // Catalog-bound wait â€” ReadNodeAsync would falsely "find" the leaf via
        // the TestPartition ancestor's MeshNodeReference reducer.
        await WaitForNodeAbsenceAsync($"{TestPartition}/leaf", ct);
    }

    [Fact(Timeout = 20_000)]
    public async Task Recursive_Delete_RemovesEntireSubtree()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTreeAsync(ct);

        var response = await DeleteAsync(
            new DeleteNodeRequest(Root) { Recursive = true }, 10.Seconds(), ct);

        response.Success.Should().BeTrue($"expected OK, got: {response.Error}");
        response.Log!.AffectedPaths.Should().BeEquivalentTo(new[]
        {
            Root,
            $"{Root}/c1",
            $"{Root}/c2",
            $"{Root}/c1/gc"
        });

        // Wait once for the catalog to drop ALL four â€” single subscription, not four.
        var paths = await WaitForQueryPathSetAsync(
            $"path:{TestPartition} scope:subtree",
            set => !set.Contains(Root)
                && !set.Contains($"{Root}/c1")
                && !set.Contains($"{Root}/c2")
                && !set.Contains($"{Root}/c1/gc"),
            ct);
        paths.Should().NotContain(Root);
        paths.Should().NotContain($"{Root}/c1");
        paths.Should().NotContain($"{Root}/c2");
        paths.Should().NotContain($"{Root}/c1/gc");
    }

    [Fact(Timeout = 20_000)]
    public async Task NonRecursive_WithChildren_Fails_HasChildren_NothingDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTreeAsync(ct);

        var response = await DeleteAsync(
            new DeleteNodeRequest(Root), 10.Seconds(), ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.HasChildren);
        response.Log!.Status.Should().Be(ActivityStatus.Failed);

        // Nothing should have been deleted.
        (await NodeExistsAsync(Root, ct)).Should().BeTrue("parent must still exist after rejected delete");
        (await NodeExistsAsync($"{Root}/c1", ct)).Should().BeTrue("children must still exist");
    }

    [Fact(Timeout = 20_000)]
    public async Task Missing_Node_Fails_NotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await DeleteAsync(
            new DeleteNodeRequest($"{TestPartition}/does-not-exist"), 10.Seconds(), ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.NodeNotFound);
        response.Log!.Status.Should().Be(ActivityStatus.Failed);
        response.Log.Messages.Should().Contain(m => m.Message.Contains("not found"));
    }

    // â”€â”€â”€ Phase 2: permission checks â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 20_000)]
    public async Task NoDeletePermission_OnRoot_Fails_Unauthorized_AndLogsPath()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(
            new MeshNode("locked", TestPartition) { Name = "Locked", NodeType = "Markdown" });

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
            var responseDelivery = await restrictedClient.Observe(new DeleteNodeRequest(path), o => o.WithTarget(new Address(path))).FirstAsync().ToTask();
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

        (await NodeExistsAsync(path, ct)).Should().BeTrue(
            "node must not be deleted when caller lacks Delete permission");
    }

    // â”€â”€â”€ Phase 3: validator-based rejection â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 20_000)]
    public async Task Validator_RejectsRoot_Fails_ValidationFailed_LogsNodePath()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(
            new MeshNode("blocked", TestPartition)
            {
                Name = BlockingValidator.BlockedMarker,
                NodeType = "Markdown"
            });

        var response = await DeleteAsync(
            new DeleteNodeRequest($"{TestPartition}/blocked"), 10.Seconds(), ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.ValidationFailed);
        response.Log!.Status.Should().Be(ActivityStatus.Failed);
        LogMentions(response, $"{TestPartition}/blocked")
            .Should().BeTrue($"log should mention blocked path; got: {string.Join(" | ", response.Log.Messages.Select(m => m.Message))}");

        (await NodeExistsAsync($"{TestPartition}/blocked", ct)).Should().BeTrue(
            "rejected delete must leave the node in place");
    }

    [Fact(Timeout = 20_000)]
    public async Task Validator_RejectsDescendant_BlocksWholeSubtree_AllPathsListed()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(
            new MeshNode("mixed", TestPartition) { Name = "Mixed", NodeType = "Group" });
        await NodeFactory.CreateNode(
            new MeshNode("ok", $"{TestPartition}/mixed") { Name = "OK", NodeType = "Markdown" });
        await NodeFactory.CreateNode(
            new MeshNode("bad", $"{TestPartition}/mixed")
            {
                Name = BlockingValidator.BlockedMarker,
                NodeType = "Markdown"
            });

        var response = await DeleteAsync(
            new DeleteNodeRequest($"{TestPartition}/mixed") { Recursive = true },
            10.Seconds(), ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.ValidationFailed);

        // Bulk atomicity: nothing in the subtree should be deleted.
        (await NodeExistsAsync($"{TestPartition}/mixed", ct)).Should().BeTrue();
        (await NodeExistsAsync($"{TestPartition}/mixed/ok", ct)).Should().BeTrue();
        (await NodeExistsAsync($"{TestPartition}/mixed/bad", ct)).Should().BeTrue();

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
        });
    }

    // â”€â”€â”€ Phase 3: warnings + ConfirmWarnings round-trip â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 20_000)]
    public async Task Warnings_WithoutConfirm_Block_AndLogWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(
            new MeshNode("warny", TestPartition)
            {
                Name = WarningValidator.WarnMarker,
                NodeType = "Markdown"
            });

        var response = await DeleteAsync(
            new DeleteNodeRequest($"{TestPartition}/warny"), 10.Seconds(), ct);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.WarningsRequireConfirmation);
        response.Log!.Messages.Should().Contain(m =>
            m.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning
            && m.Message.Contains(WarningValidator.WarnText));

        (await NodeExistsAsync($"{TestPartition}/warny", ct)).Should().BeTrue(
            "warnings must block without ConfirmWarnings");
    }

    [Fact(Timeout = 20_000)]
    public async Task Warnings_WithConfirm_Proceed_AndLogWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(
            new MeshNode("warny2", TestPartition)
            {
                Name = WarningValidator.WarnMarker,
                NodeType = "Markdown"
            });

        var response = await DeleteAsync(
            new DeleteNodeRequest($"{TestPartition}/warny2") { ConfirmWarnings = true },
            10.Seconds(), ct);

        response.Success.Should().BeTrue($"expected OK, got: {response.Error}");
        response.Log!.Status.Should().Be(ActivityStatus.Warning,
            "completed deletes that saw warnings should surface them via Status=Warning");
        response.Log.Messages.Should().Contain(m =>
            m.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning
            && m.Message.Contains(WarningValidator.WarnText));

        await WaitForNodeAbsenceAsync($"{TestPartition}/warny2", ct);
    }

    // â”€â”€â”€ Phase 4: bulk atomicity + ActivityLog â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 20_000)]
    public async Task Recursive_Delete_Log_ListsAllAffectedPathsAndSucceeded()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTreeAsync(ct);

        var response = await DeleteAsync(
            new DeleteNodeRequest(Root) { Recursive = true }, 10.Seconds(), ct);

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

        public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
        {
            if (context.Node.Name == BlockedMarker)
                return Task.FromResult(
                    NodeValidationResult.Invalid($"'{context.Node.Path}' is protected", NodeRejectionReason.ValidationFailed));
            return Task.FromResult(NodeValidationResult.Valid());
        }
    }

    public sealed class WarningValidator : INodeValidator
    {
        public const string WarnMarker = "CONFIRM-REQUIRED";
        public const string WarnText = "node may have downstream dependencies";

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Delete];

        public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
        {
            if (context.Node.Name == WarnMarker)
                return Task.FromResult(NodeValidationResult.ValidWithWarning(
                    $"{WarnText} ({context.Node.Path})"));
            return Task.FromResult(NodeValidationResult.Valid());
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
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync(TestUsers.Admin.ObjectId, "Admin", null, "system");
    }
}
