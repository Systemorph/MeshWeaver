using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// The documentation partition is a synced, public read-only Space: an ordinary user (and, because
/// a <c>PartitionAccessPolicy</c> caps the maximum permission at a scope, even an admin) may READ it
/// but never Create / Update / Delete — and this holds for BOTH the pages under the partition AND the
/// partition <c>Space</c> root itself (path <c>Doc</c>, namespace <c>""</c>, which sits in the scope
/// hierarchy under <c>Doc</c>). Enforced by the <c>Doc/_Policy</c> seeded by
/// <see cref="DocumentationExtensions.AddDocumentation{TBuilder}"/> (CUD = false) plus the Public→Viewer
/// grant for readability. Agent/Model synced partitions use the same shape (<c>PublicRead = true</c>,
/// CUD = false) via their built-in static providers. Regression guard for task #12.
/// </summary>
public class SyncedPartitionReadOnlyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan StepTimeout = 30.Seconds();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddDocumentation();

    [Theory]
    [InlineData("Doc")]                      // the partition Space root (namespace="")
    [InlineData("Doc/DataMesh/UnifiedPath")] // a page under the root
    public async Task Doc_IsPublicReadOnly(string path)
    {
        // A plain user with no explicit grant. Even though the test mesh seeds Public→Admin at root
        // (so this user resolves to Admin = Permission.All), the Doc/_Policy cap strips C/U/D at the
        // Doc scope and below — proving the synced space cannot be written, not even by an admin.
        const string user = "ordinary.user@example.com";

        await Mesh.GetEffectivePermissions(path, user)
            .Should().Within(StepTimeout)
            .Match(p => p.HasFlag(Permission.Read)
                        && !p.HasFlag(Permission.Create)
                        && !p.HasFlag(Permission.Update)
                        && !p.HasFlag(Permission.Delete),
                $"'{path}' must be publicly readable but never user-writable (Doc/_Policy CUD=false)");
    }

    // ── System must ALWAYS bypass row-based access — even on a read-only _Policy partition ──
    //
    // The atioz 2026-06-18 wedge: compile/import write their progress as `_Activity` nodes UNDER
    // the read-only Doc partition, as System (the partition provisioner). If that System write is
    // denied, the activity node never lands in `doc.activities`, yet the parent is still stamped
    // with its path → progress readers subscribe to a non-existent node → `[ROUTE] NotFound`
    // resubscribe storm → routing starves → portal wedges. Root cause: the owner per-node-hub gate
    // (AccessControlPipeline) authorizes against `delivery.AccessContext`, with NO System bypass —
    // so System is honoured ONLY if the identity actually survives to the write. These pin that
    // System writes to Doc persist, including when the impersonation scope is no longer the ambient
    // context at subscribe time (the emission-thread loss the importer/compiler hit in prod).

    private static MeshNode NewDocActivityNode()
    {
        const string main = "Doc/DataMesh/UnifiedPath";
        var id = "compile-" + Guid.NewGuid().AsString();
        return new MeshNode(id, $"{main}/_Activity")
        {
            NodeType = ActivityNodeType.NodeType,
            Name = "Compile activity (test)",
            MainNode = main,
            State = MeshNodeState.Active,
            Content = new ActivityLog(ActivityCategory.Compilation)
            {
                Id = id,
                HubPath = main,
                Status = ActivityStatus.Running,
            },
        };
    }

    [Fact(Timeout = 30000)]
    public async Task SystemCreate_OfActivityUnderReadOnlyDoc_Persists()
    {
        // Baseline: with the System scope live across the subscribe, the write must succeed even
        // though Doc/_Policy caps CUD=false for every ordinary/admin identity.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var node = NewDocActivityNode();

        MeshNode? created = null;
        Exception? error = null;
        using (accessService.ImpersonateAsSystem())
        {
            try { created = await NodeFactory.CreateNode(node).FirstAsync().ToTask(); }
            catch (Exception ex) { error = ex; }
        }
        error.Should().BeNull(
            $"System provisions the Doc partition — it must bypass the Doc/_Policy CUD=false cap; got {error?.GetType().FullName}: '{error?.Message}'");
        created.Should().NotBeNull();
    }

    [Fact(Timeout = 30000)]
    public async Task SystemCreate_WhenScopeNotAmbientAtSubscribe_StillPersists()
    {
        // The actual prod repro: the write observable is BUILT under ImpersonateAsSystem but
        // SUBSCRIBED later, when the ambient identity is no longer System (mirrors the cross-hub
        // write subscribed on a PG/remote emission thread where the AsyncLocal is gone). The write
        // MUST carry System captured at creation; otherwise the owner gate sees the non-System
        // ambient identity → Doc/_Policy denies → the activity node is silently dropped.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var node = NewDocActivityNode();

        IObservable<MeshNode> create;
        using (accessService.ImpersonateAsSystem())
            create = NodeFactory.CreateNode(node);   // cold, NOT subscribed yet
        // scope disposed — ambient identity reverts to the DevLogin admin (capped on Doc)

        MeshNode? created = null;
        Exception? error = null;
        try { created = await create.FirstAsync().ToTask(); }   // subscribe now
        catch (Exception ex) { error = ex; }
        error.Should().BeNull(
            $"System must be captured at the write's creation, not re-read at subscribe; got {error?.GetType().FullName}: '{error?.Message}'");
        created.Should().NotBeNull();
    }
}
