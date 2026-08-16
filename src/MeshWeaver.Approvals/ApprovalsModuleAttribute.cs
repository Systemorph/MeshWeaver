using MeshWeaver.Mesh;

[assembly: MeshWeaver.Approvals.ApprovalsModule]

namespace MeshWeaver.Approvals;

/// <summary>
/// Module registration for approval workflows. Listing this DLL under <c>Modules:Assemblies</c>
/// registers the <c>Approval</c> node type on the mesh and the Request Approval form, the inline
/// approvals section, and the approvals menu entry on EVERY per-node hub — approvals can be
/// requested on any node, not only Markdown documents.
///
/// <para>The whole registration rides ONE <see cref="MeshNodeProviderAttribute.BuilderConfigurations"/>
/// hook invoking <see cref="ApprovalExtensions.AddApprovals"/> — the same call a test fixture or a
/// bespoke host makes — so the boot-pack lane and the compiled-in lane cannot drift
/// (the <c>MarkdownExportProviderAttribute</c> shape).</para>
///
/// <para>Delisting the module removes the Approvals UI mesh-wide: the per-node areas and menu entry
/// disappear, and the markdown overview's embedded Approvals section self-suppresses (it checks the
/// <c>ApprovalsEnabled</c> marker via <c>HasApprovals()</c>, which stays in MeshWeaver.Graph). The
/// <c>Approval</c> content record, its type-registry seeding, and the <c>_Approval</c> →
/// <c>annotations</c> satellite-table mapping stay platform-level, so existing approval data keeps
/// deserializing and routing while the module is delisted.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ApprovalsModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
        [builder => builder.AddApprovals()];
}
